using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Resilience;
using Fotoprint.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Fotoprint.Core.Llm;

public interface IGeminiClient
{
    /// <summary>
    /// Вызов модели Gemini через Google AI Studio API с защитой от превышения RPM и 3 повторами.
    /// </summary>
    Task<GeminiGenerateResponse> GenerateContentAsync(
        string prompt,
        string? systemInstruction = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        string? traceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Универсальный диалоговый шаг с поддержкой Function Calling (вызов инструментов MCP).
    /// </summary>
    Task<GeminiTurnResponse> ChatTurnAsync(
        List<object> contents,
        object? tools = null,
        string? systemInstruction = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        string? traceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Статистика вызовов Gemini: всего вызовов, вызовов за последние 60 сек, остаток лимита RPM.
    /// </summary>
    GeminiCallStats GetStats();
}

public record GeminiFunctionCall(string Name, JsonElement Arguments);

public record GeminiTurnResponse(
    bool IsSuccess,
    string? Text,
    GeminiFunctionCall? FunctionCall,
    string? FinishReason,
    int PromptTokens,
    int CandidatesTokens,
    int TotalTokens,
    int AttemptsMade,
    string? ErrorMessage);

public record GeminiCallStats(
    int TotalCalls,
    int CallsInLast60Seconds,
    int RpmLimit,
    int RemainingQuotaInCurrentMinute,
    double CurrentUsagePercent,
    DateTime? OldestCallInWindow,
    int EstimatedWaitMsUntilNextSlot);

public record GeminiGenerateResponse(
    bool IsSuccess,
    string? Text,
    string? FinishReason,
    int PromptTokens,
    int CandidatesTokens,
    int TotalTokens,
    int AttemptsMade,
    string? ErrorMessage);

public class GeminiClient : IGeminiClient
{
    private readonly HttpClient _httpClient;
    private readonly FotoprintConfig _config;
    private readonly ILlmLoadBalancer _loadBalancer;
    private readonly IRollbackManager _rollbackManager;
    private readonly ILangfuseClient? _langfuse;
    private readonly ILogger<GeminiClient>? _logger;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly int _rpmLimit;

    // Счётчик вызовов в скользящем окне 60 секунд
    private readonly Queue<DateTime> _callTimestamps = new();
    private readonly object _counterLock = new();
    private int _totalCalls = 0;

    public GeminiClient(
        HttpClient httpClient,
        FotoprintConfig config,
        ILlmLoadBalancer loadBalancer,
        IRollbackManager rollbackManager,
        ILangfuseClient? langfuse = null,
        ILogger<GeminiClient>? logger = null)
    {
        _httpClient = httpClient;
        _config = config;
        _loadBalancer = loadBalancer;
        _rollbackManager = rollbackManager;
        _langfuse = langfuse;
        _logger = logger;

        var apiKeyEnvVar = _config.Llm.Proxy.ApiKeyEnvVar;
        if (string.IsNullOrWhiteSpace(apiKeyEnvVar)) apiKeyEnvVar = "GEMINI_API_KEY";

        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable(apiKeyEnvVar)
            ?? string.Empty;

        _model = Environment.GetEnvironmentVariable("GEMINI_MODEL")
            ?? _config.Llm.Models.ChatModel;

        if (string.Equals(_model, "gpt-4o", StringComparison.OrdinalIgnoreCase))
        {
            _model = "gemini-1.5-flash";
        }

        _baseUrl = _config.Llm.Proxy.BaseUrl;
        if (string.IsNullOrWhiteSpace(_baseUrl) || _baseUrl.Contains("api.openai.com"))
        {
            _baseUrl = "https://generativelanguage.googleapis.com/v1beta";
        }

        _rpmLimit = _config.Llm.RateLimits.Rpm > 0 ? _config.Llm.RateLimits.Rpm : 60;
    }

    public async Task<GeminiGenerateResponse> GenerateContentAsync(
        string prompt,
        string? systemInstruction = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        string? traceId = null,
        CancellationToken ct = default)
    {
        var contents = new List<object>
        {
            new
            {
                role = "user",
                parts = new object[] { new { text = prompt } }
            }
        };

        var turn = await ChatTurnAsync(
            contents: contents,
            tools: null,
            systemInstruction: systemInstruction,
            temperature: temperature,
            maxOutputTokens: maxOutputTokens,
            traceId: traceId,
            ct: ct);

        return new GeminiGenerateResponse(
            IsSuccess: turn.IsSuccess,
            Text: turn.Text,
            FinishReason: turn.FinishReason,
            PromptTokens: turn.PromptTokens,
            CandidatesTokens: turn.CandidatesTokens,
            TotalTokens: turn.TotalTokens,
            AttemptsMade: turn.AttemptsMade,
            ErrorMessage: turn.ErrorMessage);
    }

    public async Task<GeminiTurnResponse> ChatTurnAsync(
        List<object> contents,
        object? tools = null,
        string? systemInstruction = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        string? traceId = null,
        CancellationToken ct = default)
    {
        var temp = temperature ?? _config.Llm.Parameters.Temperature;
        var maxTokens = maxOutputTokens ?? _config.Llm.Parameters.MaxTokens;

        return await _loadBalancer.ScheduleAsync(
            taskName: $"Gemini:{_model}:ChatTurn",
            work: async token =>
            {
                var rollbackResult = await _rollbackManager.ExecuteWithRollbackAsync(
                    nodeName: $"Gemini:{_model}:Turn",
                    action: async (attempt, retryToken) =>
                    {
                        RecordCall();

                        var endpointUrl = $"{_baseUrl.TrimEnd('/')}/models/{_model}:generateContent";

                        var requestBody = BuildRawRequestBody(contents, tools, systemInstruction, temp, maxTokens);

                        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
                        request.Content = JsonContent.Create(requestBody);

                        if (!string.IsNullOrWhiteSpace(_apiKey))
                        {
                            request.Headers.Add("x-goog-api-key", _apiKey);
                        }

                        var response = await _httpClient.SendAsync(request, retryToken);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errContent = await response.Content.ReadAsStringAsync(retryToken);
                            var statusCode = (int)response.StatusCode;
                            _logger?.LogWarning("⚠️ [Gemini API] Ошибка {StatusCode}: {ErrorBody}", statusCode, errContent);

                            throw new NodeTransientException(
                                $"Gemini API вернул код {statusCode}: {response.ReasonPhrase}. Детали: {errContent}",
                                statusCode);
                        }

                        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: retryToken);
                        return ParseGeminiTurnResponse(json, attempt);
                    },
                    compensatingRollbackAction: (ex, rollbackToken) =>
                    {
                        _logger?.LogError(ex, "🚨 [Gemini API] Исчерпаны 3 повтора вызова модели {Model}. Роллбэк зафиксирован.", _model);
                        return Task.CompletedTask;
                    },
                    traceId: traceId,
                    maxRetries: _config.Resilience.MaxRetries,
                    initialDelayMs: _config.Resilience.InitialDelayMs,
                    ct: token);

                if (rollbackResult.IsSuccess && rollbackResult.Value != null)
                {
                    if (!string.IsNullOrEmpty(traceId) && _langfuse != null)
                    {
                        _ = _langfuse.LogEventAsync(
                            traceId,
                            "Gemini:TurnCompleted",
                            "INFO",
                            $"Шаг выполнен. Токенов: {rollbackResult.Value.TotalTokens}. Функция: {rollbackResult.Value.FunctionCall?.Name ?? "none"}",
                            metadata: new
                            {
                                model = _model,
                                total_tokens = rollbackResult.Value.TotalTokens,
                                has_function_call = rollbackResult.Value.FunctionCall != null
                            },
                            ct: token);
                    }

                    return rollbackResult.Value;
                }

                return new GeminiTurnResponse(
                    IsSuccess: false,
                    Text: null,
                    FunctionCall: null,
                    FinishReason: "ERROR",
                    PromptTokens: 0,
                    CandidatesTokens: 0,
                    TotalTokens: 0,
                    AttemptsMade: rollbackResult.AttemptsMade,
                    ErrorMessage: rollbackResult.ErrorMessage);
            },
            traceId: traceId,
            ct: ct);
    }

    private void RecordCall()
    {
        lock (_counterLock)
        {
            _totalCalls++;
            _callTimestamps.Enqueue(DateTime.UtcNow);
            CleanOldTimestamps();
        }
    }

    private void CleanOldTimestamps()
    {
        var oneMinuteAgo = DateTime.UtcNow.AddSeconds(-60);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < oneMinuteAgo)
        {
            _callTimestamps.Dequeue();
        }
    }

    public GeminiCallStats GetStats()
    {
        lock (_counterLock)
        {
            CleanOldTimestamps();

            var currentInWindow = _callTimestamps.Count;
            var remaining = Math.Max(0, _rpmLimit - currentInWindow);
            var usagePercent = Math.Round((double)currentInWindow / _rpmLimit * 100, 1);
            DateTime? oldest = _callTimestamps.Count > 0 ? _callTimestamps.Peek() : null;

            int waitMs = 0;
            if (currentInWindow >= _rpmLimit && oldest.HasValue)
            {
                var timeUntilFree = (int)(oldest.Value.AddSeconds(60) - DateTime.UtcNow).TotalMilliseconds;
                waitMs = Math.Max(0, timeUntilFree);
            }

            return new GeminiCallStats(
                TotalCalls: _totalCalls,
                CallsInLast60Seconds: currentInWindow,
                RpmLimit: _rpmLimit,
                RemainingQuotaInCurrentMinute: remaining,
                CurrentUsagePercent: usagePercent,
                OldestCallInWindow: oldest,
                EstimatedWaitMsUntilNextSlot: waitMs);
        }
    }

    private static object BuildRawRequestBody(
        List<object> contents,
        object? tools,
        string? systemInstruction,
        double temperature,
        int maxOutputTokens)
    {
        var body = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new
            {
                temperature = temperature,
                maxOutputTokens = maxOutputTokens,
                topP = 0.95
            }
        };

        if (tools != null)
        {
            body["tools"] = tools;
        }

        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            body["systemInstruction"] = new
            {
                parts = new object[]
                {
                    new { text = systemInstruction }
                }
            };
        }

        return body;
    }

    private static GeminiTurnResponse ParseGeminiTurnResponse(JsonElement json, int attempt)
    {
        string? text = null;
        string? finishReason = null;
        GeminiFunctionCall? functionCall = null;
        int promptTokens = 0;
        int candidatesTokens = 0;
        int totalTokens = 0;

        if (json.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var t))
                    {
                        text = t.GetString();
                    }

                    if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var name = fc.GetProperty("name").GetString() ?? "";
                        var args = fc.TryGetProperty("args", out var a) ? a : default;
                        functionCall = new GeminiFunctionCall(name, args);
                    }
                }
            }

            if (firstCandidate.TryGetProperty("finishReason", out var fr))
            {
                finishReason = fr.GetString();
            }
        }

        if (json.TryGetProperty("usageMetadata", out var usage))
        {
            if (usage.TryGetProperty("promptTokenCount", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("candidatesTokenCount", out var ct)) candidatesTokens = ct.GetInt32();
            if (usage.TryGetProperty("totalTokenCount", out var tt)) totalTokens = tt.GetInt32();
        }

        return new GeminiTurnResponse(
            IsSuccess: true,
            Text: text,
            FunctionCall: functionCall,
            FinishReason: finishReason ?? "STOP",
            PromptTokens: promptTokens,
            CandidatesTokens: candidatesTokens,
            TotalTokens: totalTokens,
            AttemptsMade: attempt,
            ErrorMessage: null);
    }
}
