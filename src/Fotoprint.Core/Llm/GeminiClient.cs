using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Resilience;
using Fotoprint.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Fotoprint.Core.Llm;

/// <summary>
/// Единый интерфейс взаимодействия с LLM (NVIDIA NIM / Nemotron).
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Вызов модели LLM с защитой от превышения RPM и автоматическими повторами.
    /// </summary>
    Task<GeminiGenerateResponse> GenerateContentAsync(
        string prompt,
        string? systemInstruction = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        string? traceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Диалоговый шаг с поддержкой Function Calling (Tool Calling в формате OpenAI/NVIDIA).
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
    /// Статистика вызовов LLM: скользящее окно 60 сек, остаток лимита RPM.
    /// </summary>
    GeminiCallStats GetStats();
}

public interface INvidiaClient : ILlmClient { }
public interface IGeminiClient : ILlmClient { }

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

/// <summary>
/// Клиент вызова модели NVIDIA NIM (nvidia/nemotron-3.5-super-vl-120b-a12b).
/// Реализует стандартный OpenAI-совместимый протокол с поддержкой reasoning (enable_thinking),
/// function calling, мультимодальности и интеграции с Langfuse и балансировщиком нагрузки.
/// </summary>
public class NvidiaClient : INvidiaClient, IGeminiClient
{
    private readonly HttpClient _httpClient;
    private readonly FotoprintConfig _config;
    private readonly ILlmLoadBalancer _loadBalancer;
    private readonly IRollbackManager _rollbackManager;
    private readonly ILangfuseClient? _langfuse;
    private readonly ILogger<NvidiaClient>? _logger;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly int _rpmLimit;

    // Счётчик вызовов в скользящем окне 60 секунд
    private readonly Queue<DateTime> _callTimestamps = new();
    private readonly object _counterLock = new();
    private int _totalCalls = 0;

    public NvidiaClient(
        HttpClient httpClient,
        FotoprintConfig config,
        ILlmLoadBalancer loadBalancer,
        IRollbackManager rollbackManager,
        ILangfuseClient? langfuse = null,
        ILogger<NvidiaClient>? logger = null)
    {
        _httpClient = httpClient;
        _config = config;
        _loadBalancer = loadBalancer;
        _rollbackManager = rollbackManager;
        _langfuse = langfuse;
        _logger = logger;

        var apiKeyEnvVar = _config.Llm.Proxy.ApiKeyEnvVar;
        if (string.IsNullOrWhiteSpace(apiKeyEnvVar)) apiKeyEnvVar = "NVIDIA_API_KEY";

        _apiKey = Environment.GetEnvironmentVariable(apiKeyEnvVar)
            ?? Environment.GetEnvironmentVariable("NVIDIA_API_KEY")
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? string.Empty;

        _model = Environment.GetEnvironmentVariable("LLM_MODEL")
            ?? _config.Llm.Models.ChatModel;

        if (string.IsNullOrWhiteSpace(_model) || _model.StartsWith("gemini") || _model.Equals("gpt-4o", StringComparison.OrdinalIgnoreCase))
        {
            _model = "nvidia/nemotron-3.5-super-vl-120b-a12b";
        }

        var configBaseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL")
            ?? _config.Llm.Proxy.BaseUrl;

        _baseUrl = !string.IsNullOrWhiteSpace(configBaseUrl)
            ? configBaseUrl.TrimEnd('/')
            : "https://integrate.api.nvidia.com/v1";

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
            new { role = "user", content = prompt }
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
            taskName: $"Nvidia:{_model}:ChatTurn",
            work: async token =>
            {
                var rollbackResult = await _rollbackManager.ExecuteWithRollbackAsync(
                    nodeName: $"Nvidia:{_model}:Turn",
                    action: async (attempt, retryToken) =>
                    {
                        RecordCall();

                        var endpointUrl = $"{_baseUrl}/chat/completions";
                        var requestBody = BuildNvidiaRequestBody(contents, tools, systemInstruction, temp, maxTokens, _model);

                        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
                        request.Content = JsonContent.Create(requestBody);

                        if (!string.IsNullOrWhiteSpace(_apiKey))
                        {
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                        }

                        var response = await _httpClient.SendAsync(request, retryToken);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errContent = await response.Content.ReadAsStringAsync(retryToken);
                            var statusCode = (int)response.StatusCode;
                            _logger?.LogWarning("⚠️ [NVIDIA API] Ошибка {StatusCode}: {ErrorBody}", statusCode, errContent);

                            throw new NodeTransientException(
                                $"LLM API вернул код {statusCode}: {response.ReasonPhrase}. Детали: {errContent}",
                                statusCode);
                        }

                        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: retryToken);
                        return ParseNvidiaTurnResponse(json, attempt);
                    },
                    compensatingRollbackAction: (ex, rollbackToken) =>
                    {
                        _logger?.LogWarning("🔄 [NVIDIA Rollback] Откат состояния после ошибки: {Message}", ex.Message);
                        return Task.CompletedTask;
                    },
                    traceId: traceId,
                    ct: token);

                if (!rollbackResult.IsSuccess)
                {
                    var lastErr = rollbackResult.ErrorMessage ?? rollbackResult.LastException?.Message ?? "Неизвестная ошибка вызова NVIDIA API";
                    _logger?.LogError("❌ [NVIDIA Failed] Все попытки исчерпаны: {Error}", lastErr);

                    if (_langfuse != null && !string.IsNullOrEmpty(traceId))
                    {
                        _ = _langfuse.LogEventAsync(
                            traceId,
                            "Nvidia:TurnFailed",
                            "ERROR",
                            lastErr,
                            metadata: new { model = _model, attempts = rollbackResult.AttemptsMade });
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
                        ErrorMessage: lastErr);
                }

                var turn = rollbackResult.Value!;

                if (_langfuse != null && !string.IsNullOrEmpty(traceId))
                {
                    _ = _langfuse.CreateGenerationAsync(
                        traceId: traceId,
                        name: $"Gen:Nvidia-{_model}",
                        model: _model,
                        startTime: DateTime.UtcNow.AddMilliseconds(-100),
                        endTime: DateTime.UtcNow,
                        promptTokens: turn.PromptTokens,
                        completionTokens: turn.CandidatesTokens,
                        totalTokens: turn.TotalTokens,
                        input: JsonSerializer.Serialize(contents),
                        output: turn.FunctionCall != null ? $"FunctionCall: {turn.FunctionCall.Name}" : turn.Text,
                        metadata: new
                        {
                            attempts = turn.AttemptsMade,
                            finishReason = turn.FinishReason,
                            hasFunctionCall = turn.FunctionCall != null
                        });
                }

                return turn;
            },
            traceId: traceId,
            ct: ct);
    }

    public GeminiCallStats GetStats()
    {
        lock (_counterLock)
        {
            CleanExpiredCalls();
            var activeCount = _callTimestamps.Count;
            var remaining = Math.Max(0, _rpmLimit - activeCount);
            var usagePct = _rpmLimit > 0 ? Math.Round((double)activeCount / _rpmLimit * 100.0, 1) : 0.0;
            DateTime? oldest = activeCount > 0 ? _callTimestamps.Peek() : null;

            int waitMs = 0;
            if (activeCount >= _rpmLimit && oldest.HasValue)
            {
                var expiresAt = oldest.Value.AddSeconds(60);
                var diff = (int)(expiresAt - DateTime.UtcNow).TotalMilliseconds;
                waitMs = Math.Max(0, diff);
            }

            return new GeminiCallStats(
                TotalCalls: _totalCalls,
                CallsInLast60Seconds: activeCount,
                RpmLimit: _rpmLimit,
                RemainingQuotaInCurrentMinute: remaining,
                CurrentUsagePercent: usagePct,
                OldestCallInWindow: oldest,
                EstimatedWaitMsUntilNextSlot: waitMs);
        }
    }

    private void RecordCall()
    {
        lock (_counterLock)
        {
            _callTimestamps.Enqueue(DateTime.UtcNow);
            _totalCalls++;
            CleanExpiredCalls();
        }
    }

    private void CleanExpiredCalls()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff)
        {
            _callTimestamps.Dequeue();
        }
    }

    private static object BuildNvidiaRequestBody(
        List<object> contents,
        object? tools,
        string? systemInstruction,
        double temperature,
        int maxOutputTokens,
        string model)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            messages.Add(new { role = "system", content = systemInstruction });
        }

        foreach (var c in contents)
        {
            if (c is JsonElement el)
            {
                messages.Add(el);
            }
            else
            {
                var jsonStr = JsonSerializer.Serialize(c);
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                string role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                if (role == "model") role = "assistant";

                // Проверяем наличие content в OpenAI формате (строка или массив мультимодальных блоков)
                if (root.TryGetProperty("content", out var contentProp))
                {
                    if (contentProp.ValueKind == JsonValueKind.Array)
                    {
                        var blocks = new List<object>();
                        foreach (var b in contentProp.EnumerateArray())
                        {
                            blocks.Add(JsonSerializer.Deserialize<object>(b.GetRawText())!);
                        }
                        messages.Add(new { role, content = blocks });
                    }
                    else
                    {
                        var msgObj = new Dictionary<string, object>
                        {
                            ["role"] = role,
                            ["content"] = contentProp.GetString() ?? ""
                        };
                        if (root.TryGetProperty("tool_call_id", out var tcid))
                        {
                            msgObj["tool_call_id"] = tcid.GetString() ?? "";
                        }
                        if (root.TryGetProperty("name", out var tname))
                        {
                            msgObj["name"] = tname.GetString() ?? "";
                        }
                        if (root.TryGetProperty("tool_calls", out var tcArray))
                        {
                            msgObj["tool_calls"] = JsonSerializer.Deserialize<object>(tcArray.GetRawText())!;
                        }
                        messages.Add(msgObj);
                    }
                }
                // Либо распаковываем Gemini-формат parts: [{ text }]
                else if (root.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    string text = "";
                    foreach (var p in parts.EnumerateArray())
                    {
                        if (p.TryGetProperty("text", out var pt))
                        {
                            text += pt.GetString();
                        }
                    }
                    messages.Add(new { role, content = text });
                }
                else
                {
                    messages.Add(new { role, content = jsonStr });
                }
            }
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["top_p"] = 0.95,
            ["max_tokens"] = maxOutputTokens,
            ["chat_template_kwargs"] = new
            {
                enable_thinking = true
            }
        };

        if (tools != null)
        {
            body["parallel_tool_calls"] = true;
            body["tools"] = NormalizeTools(tools);
        }

        return body;
    }

    private static object NormalizeTools(object rawTools)
    {
        var jsonStr = JsonSerializer.Serialize(rawTools);
        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;

        var normalized = new List<object>();

        // Если это массив объектов:
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                // Уже в OpenAI формате: { type: "function", function: { ... } }
                if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "function")
                {
                    normalized.Add(JsonSerializer.Deserialize<object>(item.GetRawText())!);
                }
                // Gemini формат: { functionDeclarations: [ { name, description, parameters } ] }
                else if (item.TryGetProperty("functionDeclarations", out var decls) && decls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fn in decls.EnumerateArray())
                    {
                        var fnObj = JsonSerializer.Deserialize<object>(fn.GetRawText())!;
                        normalized.Add(new
                        {
                            type = "function",
                            function = fnObj
                        });
                    }
                }
                else
                {
                    normalized.Add(new
                    {
                        type = "function",
                        function = JsonSerializer.Deserialize<object>(item.GetRawText())!
                    });
                }
            }
            return normalized;
        }

        return rawTools;
    }

    private static GeminiTurnResponse ParseNvidiaTurnResponse(JsonElement json, int attempt)
    {
        string? text = null;
        string? finishReason = null;
        GeminiFunctionCall? functionCall = null;
        int promptTokens = 0;
        int candidatesTokens = 0;
        int totalTokens = 0;

        if (json.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    text = c.GetString();
                }

                // Поддержка reasoning_content модели Nemotron-3.5
                if (string.IsNullOrWhiteSpace(text) && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                {
                    text = rc.GetString();
                }

                if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                {
                    var firstTool = toolCalls[0];
                    if (firstTool.TryGetProperty("function", out var fn))
                    {
                        var fnName = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var fnArgsStr = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                        try
                        {
                            using var doc = JsonDocument.Parse(fnArgsStr);
                            functionCall = new GeminiFunctionCall(fnName, doc.RootElement.Clone());
                        }
                        catch
                        {
                            // fallback
                        }
                    }
                }
            }

            if (firstChoice.TryGetProperty("finish_reason", out var fr))
            {
                finishReason = fr.GetString();
            }
        }

        if (json.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct)) candidatesTokens = ct.GetInt32();
            if (usage.TryGetProperty("total_tokens", out var tt)) totalTokens = tt.GetInt32();
        }

        return new GeminiTurnResponse(
            IsSuccess: true,
            Text: text,
            FunctionCall: functionCall,
            FinishReason: finishReason ?? "stop",
            PromptTokens: promptTokens,
            CandidatesTokens: candidatesTokens,
            TotalTokens: totalTokens,
            AttemptsMade: attempt,
            ErrorMessage: null);
    }
}

/// <summary>
/// Для полной обратной совместимости существующих модулей.
/// </summary>
public class GeminiClient : NvidiaClient
{
    public GeminiClient(
        HttpClient httpClient,
        FotoprintConfig config,
        ILlmLoadBalancer loadBalancer,
        IRollbackManager rollbackManager,
        ILangfuseClient? langfuse = null,
        ILogger<NvidiaClient>? logger = null)
        : base(httpClient, config, loadBalancer, rollbackManager, langfuse, logger)
    {
    }
}
