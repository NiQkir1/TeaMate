using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Data;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Models;
using Fotoprint.Core.Security;
using Fotoprint.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Fotoprint.Core.Agents;

public interface IFotoprintAgent
{
    /// <summary>
    /// Автономный цикл агента: Gemini самостоятельно понимает намерение пользователя,
    /// выбирает нужный MCP инструмент (1C, Битрикс, Календарь, RAG), вызывает его и синтезирует финальный ответ.
    /// </summary>
    Task<AgentResponse> ProcessMessageAsync(
        long telegramId,
        long chatId,
        string messageText,
        string? traceId = null,
        CancellationToken ct = default);
}

public record AgentResponse(
    bool IsSuccess,
    string AnswerText,
    List<string> ToolsInvoked,
    int TotalLlmCalls,
    int TotalTokensUsed,
    string? ErrorMessage,
    object? ActionCard = null);

public class FotoprintAiAgent : IFotoprintAgent
{
    private static readonly JsonSerializerOptions Utf8JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeSpan ResponseCacheTtl = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<string, (AgentResponse Response, DateTime ExpiresAtUtc)> _responseCache = new(StringComparer.Ordinal);

    private readonly IGeminiClient _gemini;
    private readonly IFotoprintRepository _repo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FotoprintConfig _config;
    private readonly ILangfuseClient? _langfuse;
    private readonly ILlmJudgeService? _judgeService;
    private readonly IEmbeddingClient? _embeddingClient;
    private readonly ILogger<FotoprintAiAgent>? _logger;

    public FotoprintAiAgent(
        IGeminiClient gemini,
        IFotoprintRepository repo,
        IHttpClientFactory httpClientFactory,
        FotoprintConfig config,
        ILangfuseClient? langfuse = null,
        ILlmJudgeService? judgeService = null,
        ILogger<FotoprintAiAgent>? logger = null,
        IEmbeddingClient? embeddingClient = null)
    {
        _gemini = gemini;
        _repo = repo;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _langfuse = langfuse;
        _judgeService = judgeService;
        _logger = logger;
        _embeddingClient = embeddingClient;
    }

    public async Task<AgentResponse> ProcessMessageAsync(
        long telegramId,
        long chatId,
        string messageText,
        string? traceId = null,
        CancellationToken ct = default)
    {
        traceId ??= _langfuse?.GenerateTraceId() ?? Guid.NewGuid().ToString("N");
        var toolsInvoked = new List<string>();
        object? capturedActionCard = null;
        int llmCallsCount = 0;
        int totalTokens = 0;

        // 1. Получаем контекст безопасности сотрудника (RBAC)
        // Fail-closed: при недоступности БД сотрудник остаётся в роли Guest (EmployeeId = 0), без повышенных привилегий.
        EmployeeSecurityContext? secContext = null;
        try
        {
            secContext = await _repo.GetEmployeeSecurityContextAsync(telegramId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "[FotoprintAiAgent] БД недоступна при чтении RBAC-контекста, применяем fail-closed Guest.");
        }
        var rolesStr = secContext != null ? string.Join(", ", secContext.Roles) : "Guest";
        var deptStr = secContext?.Department ?? "Не указан";
        var userName = secContext?.FullName ?? $"User_{telegramId}";
        var empIdForCache = secContext?.EmployeeId ?? 0;
        var accessKey = secContext?.AccessGroups is { Count: > 0 } agCache
            ? string.Join(",", agCache.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            : "all";

        // Краткосрочный RBAC-кэш (45 сек) для одинаковых read-only запросов (строго изолирован по empId во избежание утечки данных между сотрудниками)
        string? cacheKey = null;
        if (IsCacheableReadQuery(messageText))
        {
            var scopeKey = $"emp:{empIdForCache}|dept:{deptStr}|roles:{rolesStr}|ag:{accessKey}";
            cacheKey = $"{scopeKey}|msg:{messageText.Trim().ToLowerInvariant()}";

            if (_responseCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
            {
                _logger?.LogInformation("⚡ [AgentCache] Возвращен кэшированный ответ (0 вызовов LLM) для '{Message}'", messageText);
                if (_langfuse != null)
                {
                    _ = _langfuse.LogEventAsync(
                        traceId,
                        "Agent:CacheHit",
                        "DEFAULT",
                        "Ответ возвращен из краткосрочного RBAC-кэша (0 вызовов LLM)",
                        metadata: new { department = deptStr, role = rolesStr },
                        ct: ct);
                }
                return cached.Response with { TotalLlmCalls = 0, TotalTokensUsed = 0 };
            }
        }

        var contents = new List<object>();
        const int maxHistoryMessagesInContext = 10;
        int loadedHistoryCount = 0;
        var validHistory = new List<ChatMessage>();

        // Подгружаем историю текущей активной сессии (до 10 последних незамороженных сообщений: если 9 — берем 9, если 11+ — берем 10 последних)
        if (secContext != null && secContext.MasterId > 0)
        {
            try
            {
                var activeSession = await _repo.GetOrCreateActiveSessionByMasterIdAsync(secContext.MasterId, chatId, "web", ct);
                var sessionMessages = await _repo.GetMessagesBySessionIdAsync(activeSession.Id, limit: maxHistoryMessagesInContext + 5, ct: ct);

                validHistory = sessionMessages
                    .Where(m => !m.IsFrozen && !string.IsNullOrWhiteSpace(m.Content))
                    .ToList();

                // Если последнее сообщение в БД — это уже текущее сообщение пользователя (сохраненное на входе в шлюз), исключаем его из истории
                if (validHistory.Count > 0 &&
                    string.Equals(validHistory[^1].Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(validHistory[^1].Content.Trim(), messageText.Trim(), StringComparison.Ordinal))
                {
                    validHistory.RemoveAt(validHistory.Count - 1);
                }

                // Берем строго не более 10 последних сообщений сессии (если <= 10 — все, если > 10 — последние 10)
                if (validHistory.Count > maxHistoryMessagesInContext)
                {
                    validHistory = validHistory.TakeLast(maxHistoryMessagesInContext).ToList();
                }

                loadedHistoryCount = validHistory.Count;

                foreach (var histMsg in validHistory)
                {
                    var role = string.Equals(histMsg.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                    contents.Add(new
                    {
                        role,
                        content = histMsg.Content
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Не удалось загрузить историю сессии для MasterId={MasterId}", secContext.MasterId);
            }
        }

        bool isConfirmedByUser = IsMutatingActionConfirmed(validHistory, messageText);
        var nowMsk = DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd HH:mm (dddd)", new System.Globalization.CultureInfo("ru-RU"));

        var systemInstruction = $"""
            Ты — корпоративный AI-ассистент типографии «Фотопринт» (Fotoprint Copilot).
            Текущая дата и время (МСК): {nowMsk}. При расчете сроков, дедлайнов и дат встреч («завтра», «до пятницы», «на следующей неделе») опирайся СТРОГО на эту текущую дату!
            Ты общаешься с сотрудником: {userName} (ID сотрудника: {secContext?.EmployeeId ?? 0}, Отдел: {deptStr}, Должность: {secContext?.Position ?? "Сотрудник"}, Роли: [{rolesStr}]).

            Твоя цель: понимать любые свободные запросы пользователя и самостоятельно выбирать подходящие MCP-инструменты:
            1. Заказы и тиражи (1С) -> вызови 'get_order_status'
            2. Производственные метрики, выработка, процент брака, загрузка машин (1С) -> вызови 'get_production_kpis'
            3. Задачи, поручения и CRM-сделки сотрудника (Битрикс24) -> вызови 'get_user_tasks', 'get_crm_deal_info' или 'create_task'
            4. Расписание, подбор окон и история планерок/совещаний (Календарь) -> вызови 'find_free_slots' для подбора свободного времени встречи, 'get_upcoming_meetings', 'get_meeting_history', 'search_meeting_rag' или 'schedule_meeting'
            5. Технологические карты, нормы отходов, регламенты оборудования, коммерческая политика/скидки, оплата сверхурочных, ПВТР и корпоративная база знаний (RAG регламентов) -> вызови 'search_typography_regulations'
            6. Персональные настройки уведомлений и напоминаний (время утренней сводки по МСК, за сколько минут напоминать о встречах, за сколько часов о дедлайнах задач, тихие часы, будни/ежедневно, включение/выключение уведомлений) -> вызови 'get_notification_settings' или 'update_notification_settings' (подтверждение НЕ требуется, выполняй изменение настроек сразу по просьбе сотрудника!)

            Правило подтверждения (Human-in-the-Loop): для инструментов с атрибутом [RequireConfirmation(true)] ('create_task', 'schedule_meeting') без подтверждения пользователя (сейчас confirmed={isConfirmedByUser}) сначала выведи точный «📋 План действий (требуется подтверждение)» со всеми параметрами и попроси подтвердить выполнение («Да, подтверждаю» / «Выполняй»). Если confirmed=True — сразу вызови инструмент создания. Инструмент 'update_notification_settings' НЕ требует подтверждения — вызывай его сразу.
            Всегда формулируй ответ четко, структурированно, опираясь на факты из вызванных инструментов, не опускай важные детали ответа инструментов (наличие материалов/остатков, сроки, риски, время и участников встреч).
            """;

        // 2. Формируем список Function Tools для LLM (OpenAI / NVIDIA формат)
        var toolDeclarations = GetAvailableTools();

        contents.Add(new
        {
            role = "user",
            content = messageText
        });

        // 3. ReAct-цикл агента (до 4 вызовов MCP-инструментов + финальный синтез ответа)
        const int maxLlmTurns = 5;
        object? lastToolResult = null;
        string? finalAnswer = null;
        GeminiTurnResponse? lastTurn = null;

        for (int turnIdx = 1; turnIdx <= maxLlmTurns; turnIdx++)
        {
            llmCallsCount++;
            var startTurn = DateTime.UtcNow;
            var toolsForTurn = (turnIdx == 1 && toolsInvoked.Count == 0) ? toolDeclarations : null;
            var turn = turnIdx > 1
                ? await _gemini.ChatTurnNoThinkingAsync(
                    contents: contents,
                    tools: toolsForTurn,
                    systemInstruction: systemInstruction,
                    traceId: traceId,
                    ct: ct)
                : await _gemini.ChatTurnAsync(
                    contents: contents,
                    tools: toolsForTurn,
                    systemInstruction: systemInstruction,
                    traceId: traceId,
                    ct: ct);
            var endTurn = DateTime.UtcNow;

            totalTokens += turn.TotalTokens;
            lastTurn = turn;

            var calls = turn.FunctionCalls is { Count: > 0 } batchCalls
                ? batchCalls
                : (turn.FunctionCall != null ? new[] { turn.FunctionCall } : Array.Empty<GeminiFunctionCall>());

            if (_langfuse != null)
            {
                await _langfuse.CreateGenerationAsync(
                    traceId: traceId,
                    name: "Gen:Main_Agent",
                    model: turn.Model ?? (turnIdx > 1 ? _config.Llm.Models.FastModel : _config.Llm.Models.ChatModel),
                    startTime: startTurn,
                    endTime: endTurn,
                    promptTokens: turn.PromptTokens,
                    completionTokens: turn.CandidatesTokens,
                    totalTokens: turn.TotalTokens,
                    input: turnIdx == 1 ? (object)messageText : lastToolResult,
                    output: calls.Count > 0 ? $"FunctionCalls: {string.Join(", ", calls.Select(c => c.Name))}" : turn.Text,
                    metadata: new { turn = turnIdx, department = deptStr, role = rolesStr, tool_calls_count = calls.Count, history_messages_count = loadedHistoryCount },
                    ct: ct);
            }

            if (!turn.IsSuccess)
            {
                return new AgentResponse(
                    IsSuccess: false,
                    AnswerText: lastToolResult != null
                        ? JsonSerializer.Serialize(lastToolResult, Utf8JsonOptions)
                        : "Извините, произошла ошибка при обращении к нейросети.",
                    ToolsInvoked: toolsInvoked,
                    TotalLlmCalls: llmCallsCount,
                    TotalTokensUsed: totalTokens,
                    ErrorMessage: turn.ErrorMessage,
                    ActionCard: capturedActionCard);
            }

            // Если модель дала финальный текстовый ответ без вызова инструмента (или достигнут лимит шагов):
            if (calls.Count == 0 || turnIdx == maxLlmTurns)
            {
                finalAnswer = calls.Count == 0
                    ? turn.Text
                    : "По вашему запросу данные в корпоративной системе не найдены или поиск не дал результатов. Попробуйте уточнить формулировку или номер заказа/встречи.";
                break;
            }

            // Проверяем атрибут [RequireConfirmation] на выбранных инструментах
            if (!isConfirmedByUser && calls.All(c => RequiresConfirmation(c.Name)))
            {
                finalAnswer = FormatConfirmationPrompt(calls);
                break;
            }

            foreach (var fnCall in calls.Where(c => isConfirmedByUser || !RequiresConfirmation(c.Name)))
            {
                toolsInvoked.Add(fnCall.Name);
                _logger?.LogInformation("🧠 [Agent] Модель выбрала инструмент '{ToolName}' (шаг {Turn}) с аргументами: {Args}",
                    fnCall.Name, turnIdx, fnCall.Arguments.ToString());

                if (_langfuse != null)
                {
                    _ = _langfuse.LogEventAsync(
                        traceId,
                        "Agent:ToolDecision",
                        "DEFAULT",
                        $"Модель автономно выбрала инструмент '{fnCall.Name}'",
                        metadata: new { tool = fnCall.Name, turn = turnIdx, arguments = fnCall.Arguments.ToString() },
                        ct: ct);
                }
            }

            // Параллельно выполняем все выбранные MCP-инструменты этого шага
            var empId = secContext?.EmployeeId ?? 0;
            var userAccessGroups = (secContext?.AccessGroups is { Count: > 0 } ag)
                ? ag.Union(new[] { "all" }, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { "all" };
            if (secContext != null)
            {
                if (secContext.MasterId > 0 && !userAccessGroups.Contains($"master:{secContext.MasterId}", StringComparer.OrdinalIgnoreCase))
                {
                    userAccessGroups.Add($"master:{secContext.MasterId}");
                }
                if (secContext.IsExecutive && !userAccessGroups.Contains("c-level", StringComparer.OrdinalIgnoreCase))
                {
                    userAccessGroups.Add("c-level");
                }
            }
            var execTasks = calls.Select(async fnCall =>
            {
                var callId = !string.IsNullOrWhiteSpace(fnCall.CallId)
                    ? fnCall.CallId!
                    : $"call_{fnCall.Name}_{Guid.NewGuid():N}";
                var startSpan = DateTime.UtcNow;
                var result = await ExecuteToolAsync(fnCall.Name, fnCall.Arguments, telegramId, empId, userAccessGroups, secContext, traceId, isConfirmedByUser, ct);
                var endSpan = DateTime.UtcNow;
                return (Call: fnCall, CallId: callId, Result: result, Start: startSpan, End: endSpan);
            });
            var execResults = await Task.WhenAll(execTasks);

            lastToolResult = execResults.Length == 1
                ? execResults[0].Result
                : execResults.Select(r => new { tool = r.Call.Name, result = r.Result }).ToList();

            if (_langfuse != null)
            {
                foreach (var r in execResults)
                {
                    await _langfuse.CreateSpanAsync(
                        traceId: traceId,
                        name: $"Span:MCP-{r.Call.Name}",
                        startTime: r.Start,
                        endTime: r.End,
                        input: r.Call.Arguments.ToString(),
                        output: r.Result,
                        metadata: new { tool = r.Call.Name, turn = turnIdx, department = deptStr },
                        ct: ct);
                }
            }

            contents.Add(new
            {
                role = "assistant",
                content = "",
                tool_calls = execResults.Select(r => (object)new
                {
                    id = r.CallId,
                    type = "function",
                    function = new
                    {
                        name = r.Call.Name,
                        arguments = r.Call.Arguments.ToString()
                    }
                }).ToArray()
            });

            foreach (var r in execResults)
            {
                if (r.Call.Name == "find_free_slots" && r.Result != null)
                {
                    capturedActionCard = await BuildSlotSelectionActionCardAsync(r.Call.Arguments, r.Result, ct);
                }

                var toolResultJson = r.Result is string strResult
                    ? strResult
                    : JsonSerializer.Serialize(r.Result, Utf8JsonOptions);

                contents.Add(new
                {
                    role = "tool",
                    tool_call_id = r.CallId,
                    name = r.Call.Name,
                    content = toolResultJson
                });
            }
        }

        if (toolsInvoked.Count == 0)
        {
            var directResponse = new AgentResponse(
                IsSuccess: true,
                AnswerText: finalAnswer ?? "Запрос обработан.",
                ToolsInvoked: toolsInvoked,
                TotalLlmCalls: llmCallsCount,
                TotalTokensUsed: totalTokens,
                ErrorMessage: null);

            if (cacheKey != null)
            {
                _responseCache[cacheKey] = (directResponse, DateTime.UtcNow.Add(ResponseCacheTtl));
            }

            return directResponse;
        }

        var finalText = finalAnswer ?? (lastToolResult != null ? JsonSerializer.Serialize(lastToolResult, Utf8JsonOptions) : "Запрос обработан.");

        // 4. Фоновая оценка адекватности и качества (LLM-as-a-Judge)
        if (_judgeService != null && _config.Telemetry.Evaluations.EnableLlmAsJudge)
        {
            var rawContextJson = JsonSerializer.Serialize(lastToolResult, Utf8JsonOptions);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _judgeService.EvaluateFaithfulnessAsync(
                        traceId: traceId,
                        userQuestion: messageText,
                        rawContextData: rawContextJson,
                        agentAnswer: finalText);

                    await _judgeService.EvaluateFormattingAndPolicyAsync(
                        traceId: traceId,
                        outputText: finalText,
                        isProtocol: false);
                }
                catch { }
            });
        }

        var finalResponse = new AgentResponse(
            IsSuccess: lastTurn?.IsSuccess ?? true,
            AnswerText: finalText,
            ToolsInvoked: toolsInvoked,
            TotalLlmCalls: llmCallsCount,
            TotalTokensUsed: totalTokens,
            ErrorMessage: lastTurn?.ErrorMessage,
            ActionCard: capturedActionCard);

        if (cacheKey != null && finalResponse.IsSuccess && !toolsInvoked.Any(RequiresConfirmation))
        {
            _responseCache[cacheKey] = (finalResponse, DateTime.UtcNow.Add(ResponseCacheTtl));
        }

        return finalResponse;
    }

    public static bool RequiresConfirmation(string toolName)
    {
        var method = typeof(FotoprintAiAgent).GetMethod(
            $"Tool_{toolName}",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
        var attr = method != null
            ? System.Reflection.CustomAttributeExtensions.GetCustomAttribute<RequireConfirmationAttribute>(method)
            : null;
        return attr?.Required ?? false;
    }

    public static bool IsMutatingActionConfirmed(IReadOnlyList<ChatMessage> validHistory, string currentMessage)
    {
        if (validHistory == null || validHistory.Count == 0 || string.IsNullOrWhiteSpace(currentMessage)) return false;
        var t = currentMessage.Trim().ToLowerInvariant();

        if (t.Contains("нет") || t.Contains("отмен") || t.Contains("не надо") || t.Contains("не нужно") ||
            t.StartsWith("измени ") || t.StartsWith("поменяй ") ||
            t.Contains("поставь задач") || t.Contains("создай задач") || t.Contains("назначь встреч"))
            return false;

        var lastReply = validHistory.LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))?.Content?.ToLowerInvariant();
        if (lastReply == null || (!lastReply.Contains("план действий") && !lastReply.Contains("подтвердите") && !lastReply.Contains("подтверждение")))
            return false;

        return ContextSwitchDetector.IsActionPlanConfirmationOrAdjustment(t);
    }

    private static string FormatConfirmationPrompt(IReadOnlyList<GeminiFunctionCall> calls)
    {
        var lines = calls.Select(c => $"- **{c.Name}**: `{JsonSerializer.Serialize(c.Arguments, Utf8JsonOptions)}`");
        return $"📋 **План действий (требуется подтверждение):**\n{string.Join("\n", lines)}\n\nПожалуйста, подтвердите выполнение плана (напишите **«Да, подтверждаю»** или **«Выполняй»**).";
    }

    private static bool IsCacheableReadQuery(string messageText)
    {
        if (string.IsNullOrWhiteSpace(messageText) || messageText.Length < 4) return false;
        var t = messageText.Trim().ToLowerInvariant();
        if (t.Contains("поставь") || t.Contains("создай") || t.Contains("назначь") || t.Contains("заведи") ||
            t.Contains("запланируй") || t.Contains("организуй") ||
            t.Contains("добавь") || t.Contains("удали") || t.Contains("измени") || t.Contains("поменяй") ||
            t.Contains("перенес") || t.Contains("включи") || t.Contains("выключи") || t.Contains("отключи") ||
            t.Contains("уведомл") || t.Contains("напомин") || t.Contains("дайджест") || t.Contains("сводк") || t.Contains("тихие часы") ||
            ContextSwitchDetector.IsActionPlanConfirmationOrAdjustment(t))
            return false;
        if (t.StartsWith("а ") || t.StartsWith("и ") || t.StartsWith("подробнее") || t.StartsWith("уточни"))
            return false;
        return true;
    }

    private HttpRequestMessage CreateMcpRequest(HttpMethod method, string mcpUrl, long telegramId, string traceId, bool isConfirmed = false)
    {
        var req = new HttpRequestMessage(method, mcpUrl);
        req.Headers.TryAddWithoutValidation("X-Internal-Service-Key", _config.Security.InternalServiceSecretKey);
        req.Headers.TryAddWithoutValidation("X-Telegram-Id", telegramId.ToString());
        req.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        req.Headers.TryAddWithoutValidation("X-User-Confirmed", isConfirmed ? "true" : "false");
        return req;
    }

    private async Task<object> GetMcpJsonAsync(string mcpUrl, string serviceName, long telegramId, string traceId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        using var req = CreateMcpRequest(HttpMethod.Get, mcpUrl, telegramId, traceId);
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        return new { status = "error", message = $"{serviceName} вернул код {res.StatusCode}" };
    }

    private static string? TryGetStringArg(JsonElement arguments, string propName)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(propName, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }
        return null;
    }

    private static int? TryGetIntArg(JsonElement arguments, string propName)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(propName, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            return val;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static bool? TryGetBoolArg(JsonElement arguments, string propName)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(propName, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.True) return true;
        if (prop.ValueKind == JsonValueKind.False) return false;
        if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private async Task<object> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        long telegramId,
        int defaultEmployeeId,
        List<string> userAccessGroups,
        EmployeeSecurityContext? secContext,
        string traceId,
        bool isConfirmed,
        CancellationToken ct)
    {
        try
        {
            if (RequiresConfirmation(toolName) && !isConfirmed)
            {
                return new
                {
                    status = "confirmation_required",
                    tool = toolName,
                    planned_arguments = arguments,
                    message = "Действие НЕ выполнено: требуется подтверждение пользователя. Выведи точный План действий и попроси подтвердить его выполнение."
                };
            }

            return toolName switch
            {
                "get_order_status" => await Tool_get_order_status(arguments, telegramId, traceId, ct),
                "get_production_kpis" => await Tool_get_production_kpis(arguments, telegramId, traceId, ct),
                "get_user_tasks" => await Tool_get_user_tasks(arguments, telegramId, defaultEmployeeId, secContext, traceId, ct),
                "get_crm_deal_info" => await Tool_get_crm_deal_info(arguments, telegramId, traceId, ct),
                "create_task" => await Tool_create_task(arguments, telegramId, defaultEmployeeId, traceId, isConfirmed, ct),
                "get_upcoming_meetings" => await Tool_get_upcoming_meetings(arguments, telegramId, traceId, ct),
                "get_meeting_history" => await Tool_get_meeting_history(arguments, telegramId, traceId, ct),
                "search_meeting_rag" => await Tool_search_meeting_rag(arguments, telegramId, traceId, ct),
                "find_free_slots" => await Tool_find_free_slots(arguments, telegramId, traceId, ct),
            "schedule_meeting" => await Tool_schedule_meeting(arguments, telegramId, traceId, isConfirmed, ct),
                "search_typography_regulations" => await Tool_search_typography_regulations(arguments, telegramId, traceId, userAccessGroups, ct),
                "get_notification_settings" => await Tool_get_notification_settings(defaultEmployeeId, secContext, ct),
                "update_notification_settings" => await Tool_update_notification_settings(arguments, defaultEmployeeId, secContext, ct),
                _ => new { status = "unknown_tool", tool = toolName }
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка исполнения инструмента {ToolName}", toolName);
            return new { error = ex.Message };
        }
    }

    private static string ResolveMcpBaseUrl(string envKey, string containerDefault, string localDefault)
    {
        var configured = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        return inContainer ? containerDefault : localDefault;
    }

    private static string OneCMcpBaseUrl => ResolveMcpBaseUrl("McpUrls__OneC", "http://mcp-1c:8082", "http://localhost:8082");
    private static string BitrixMcpBaseUrl => ResolveMcpBaseUrl("McpUrls__Bitrix24", "http://mcp-bitrix24:8083", "http://localhost:8083");
    private static string CalendarMcpBaseUrl => ResolveMcpBaseUrl("McpUrls__Calendar", "http://mcp-calendar:8084", "http://localhost:8084");
    private static string KnowledgeBaseMcpBaseUrl => ResolveMcpBaseUrl("McpUrls__KnowledgeBase", "http://mcp-knowledge-base:8086", "http://localhost:8086");

    [RequireConfirmation(false)]
    private Task<object> Tool_get_order_status(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var orderId = TryGetStringArg(arguments, "order_id") ?? "44102";
        var url = $"{OneCMcpBaseUrl}/tools/get_order_status?order_id=" + Uri.EscapeDataString(orderId);
        return GetMcpJsonAsync(url, "1C", telegramId, traceId, ct);
    }

    [RequireConfirmation(false)]
    private Task<object> Tool_get_production_kpis(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var queryParts = new List<string>();
        var dept = TryGetStringArg(arguments, "department");
        var dateFrom = TryGetStringArg(arguments, "date_from");
        var dateTo = TryGetStringArg(arguments, "date_to");
        if (!string.IsNullOrWhiteSpace(dept)) queryParts.Add("department=" + Uri.EscapeDataString(dept));
        if (!string.IsNullOrWhiteSpace(dateFrom)) queryParts.Add("date_from=" + Uri.EscapeDataString(dateFrom));
        if (!string.IsNullOrWhiteSpace(dateTo)) queryParts.Add("date_to=" + Uri.EscapeDataString(dateTo));
        var qs = queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : "";
        return GetMcpJsonAsync($"{OneCMcpBaseUrl}/tools/get_production_kpis{qs}", "1C", telegramId, traceId, ct);
    }

    [RequireConfirmation(false)]
    private Task<object> Tool_get_user_tasks(
        JsonElement arguments,
        long telegramId,
        int defaultEmployeeId,
        EmployeeSecurityContext? secContext,
        string traceId,
        CancellationToken ct)
    {
        var requestedEmpId = TryGetIntArg(arguments, "employee_id");
        var isExecutive = secContext?.IsExecutive == true;
        var empId = (isExecutive && requestedEmpId.HasValue && requestedEmpId.Value > 0)
            ? requestedEmpId.Value
            : defaultEmployeeId;

        var statusFilter = TryGetStringArg(arguments, "status_filter");
        var url = $"{BitrixMcpBaseUrl}/tools/get_user_tasks?employee_id={empId}";
        if (!string.IsNullOrWhiteSpace(statusFilter))
            url += "&status_filter=" + Uri.EscapeDataString(statusFilter);
        return GetMcpJsonAsync(url, "Bitrix24", telegramId, traceId, ct);
    }

    [RequireConfirmation(false)]
    private Task<object> Tool_get_crm_deal_info(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var dealQuery = TryGetStringArg(arguments, "deal_query") ?? "44102";
        var url = $"{BitrixMcpBaseUrl}/tools/get_crm_deal_info?deal_query=" + Uri.EscapeDataString(dealQuery);
        return GetMcpJsonAsync(url, "Bitrix24", telegramId, traceId, ct);
    }

    [RequireConfirmation(true)]
    private async Task<object> Tool_create_task(JsonElement arguments, long telegramId, int defaultEmployeeId, string traceId, bool isConfirmed, CancellationToken ct)
    {
        var title = TryGetStringArg(arguments, "title") ?? "Поручение по запросу";
        var description = TryGetStringArg(arguments, "description");
        var responsibleId = TryGetIntArg(arguments, "responsible_id") ?? defaultEmployeeId;
        DateTime? deadline = DateTime.TryParse(TryGetStringArg(arguments, "deadline"), out var dl) ? dl : null;
        Guid? meetingId = Guid.TryParse(TryGetStringArg(arguments, "meeting_id"), out var mid) ? mid : null;

        var client = _httpClientFactory.CreateClient();
        using var req = CreateMcpRequest(HttpMethod.Post, $"{BitrixMcpBaseUrl}/tools/create_task", telegramId, traceId, isConfirmed);
        req.Content = JsonContent.Create(new
        {
            title,
            description,
            responsibleId,
            deadline,
            meetingId
        });
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        return new { status = "error", message = $"Bitrix24 вернул код {res.StatusCode}" };
    }

    [RequireConfirmation(false)]
    private Task<object> Tool_get_upcoming_meetings(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var windowMinutes = TryGetIntArg(arguments, "time_window_minutes") ?? 60;
        var url = $"{CalendarMcpBaseUrl}/tools/get_upcoming_meetings?time_window_minutes={windowMinutes}";
        return GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
    }

    [RequireConfirmation(false)]
    private Task<object> Tool_get_meeting_history(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var keywords = TryGetStringArg(arguments, "topic_keywords") ?? "";
        var url = $"{CalendarMcpBaseUrl}/tools/get_meeting_history?topic_keywords=" + Uri.EscapeDataString(keywords);
        return GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
    }


        [RequireConfirmation(false)]
    private async Task<object> Tool_find_free_slots(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var participantIds = new List<int>();
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("participant_employee_ids", out var pProp))
        {
            if (pProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pProp.EnumerateArray())
                    if (el.TryGetInt32(out var pVal)) participantIds.Add(pVal);
            }
            else if (pProp.ValueKind == JsonValueKind.Number && pProp.TryGetInt32(out var sVal))
            {
                participantIds.Add(sVal);
            }
        }
        if (participantIds.Count == 0 && arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("participant_ids", out var pProp2))
        {
            if (pProp2.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pProp2.EnumerateArray())
                    if (el.TryGetInt32(out var pVal)) participantIds.Add(pVal);
            }
            else if (pProp2.ValueKind == JsonValueKind.Number && pProp2.TryGetInt32(out var sVal))
            {
                participantIds.Add(sVal);
            }
        }

        var orgId = TryGetIntArg(arguments, "organizer_employee_id") ?? 1;
        var dateStr = TryGetStringArg(arguments, "date") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var duration = TryGetIntArg(arguments, "duration_minutes") ?? 30;

        var client = _httpClientFactory.CreateClient();
        var calendarBaseUrl = !string.IsNullOrWhiteSpace(_config.McpUrls?.Calendar)
            ? _config.McpUrls.Calendar.TrimEnd('/')
            : CalendarMcpBaseUrl;

        using var req = CreateMcpRequest(HttpMethod.Post, $"{calendarBaseUrl}/tools/find_free_slots", telegramId, traceId, isConfirmed: true);
        req.Content = JsonContent.Create(new
        {
            organizer_employee_id = orgId,
            participant_employee_ids = participantIds,
            date = dateStr,
            duration_minutes = duration,
            working_start = _config.Calendar?.WorkingHoursStart ?? "09:00",
            working_end = _config.Calendar?.WorkingHoursEnd ?? "18:00",
            buffer_minutes = _config.Calendar?.DefaultBufferMinutes ?? 10
        });

        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadFromJsonAsync<object>(cancellationToken: ct) ?? new { };
        }

        var err = await res.Content.ReadAsStringAsync(ct);
        return new { error = $"Ошибка сервиса Календарь ({(int)res.StatusCode}): {err}" };
    }

    public async Task<object?> BuildSlotSelectionActionCardAsync(JsonElement arguments, object toolResult, CancellationToken ct)
    {
        try
        {
            int targetEmployeeId = 0;
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                if (arguments.TryGetProperty("participant_employee_ids", out var pProp))
                {
                    if (pProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in pProp.EnumerateArray())
                        {
                            if (el.TryGetInt32(out var pId))
                            {
                                targetEmployeeId = pId;
                                break;
                            }
                        }
                    }
                    else if (pProp.ValueKind == JsonValueKind.Number && pProp.TryGetInt32(out var pNum))
                    {
                        targetEmployeeId = pNum;
                    }
                }

                if (targetEmployeeId == 0 && arguments.TryGetProperty("participant_ids", out var pProp2))
                {
                    if (pProp2.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in pProp2.EnumerateArray())
                        {
                            if (el.TryGetInt32(out var pId))
                            {
                                targetEmployeeId = pId;
                                break;
                            }
                        }
                    }
                    else if (pProp2.ValueKind == JsonValueKind.Number && pProp2.TryGetInt32(out var pNum))
                    {
                        targetEmployeeId = pNum;
                    }
                }
            }

            string targetPerson = "Сотрудник";
            if (targetEmployeeId > 0)
            {
                var emp = await _repo.GetEmployeeByIdAsync(targetEmployeeId, ct);
                targetPerson = emp?.FullName ?? $"Сотрудник #{targetEmployeeId}";
            }

            string targetDateStr = TryGetStringArg(arguments, "date") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            int durationMinutes = TryGetIntArg(arguments, "duration_minutes") ?? 30;

            var slotDtos = new List<object>();

            JsonElement root;
            if (toolResult is JsonElement je)
            {
                root = je;
            }
            else
            {
                var serialized = JsonSerializer.Serialize(toolResult);
                root = JsonSerializer.Deserialize<JsonElement>(serialized);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("date", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                {
                    targetDateStr = dProp.GetString() ?? targetDateStr;
                }
                if (root.TryGetProperty("duration_minutes", out var durProp) && durProp.TryGetInt32(out var dVal))
                {
                    durationMinutes = dVal;
                }

                if (root.TryGetProperty("available_slots", out var slotsArr) && slotsArr.ValueKind == JsonValueKind.Array)
                {
                    int idx = 1;
                    foreach (var s in slotsArr.EnumerateArray())
                    {
                        var startIso = s.TryGetProperty("start_time", out var st) ? (st.GetString() ?? "") : "";
                        var endIso = s.TryGetProperty("end_time", out var et) ? (et.GetString() ?? "") : "";
                        var label = s.TryGetProperty("label", out var lb) ? (lb.GetString() ?? "") : "";

                        string startTimeFormatted = "";
                        string endTimeFormatted = "";
                        if (DateTimeOffset.TryParse(startIso, out var parsedStart))
                            startTimeFormatted = parsedStart.ToString("HH:mm");
                        if (DateTimeOffset.TryParse(endIso, out var parsedEnd))
                            endTimeFormatted = parsedEnd.ToString("HH:mm");

                        if (string.IsNullOrEmpty(startTimeFormatted) && label.Contains('-'))
                        {
                            var parts = label.Split('-');
                            startTimeFormatted = parts[0].Trim();
                            endTimeFormatted = parts[1].Trim();
                        }

                        slotDtos.Add(new
                        {
                            id = $"slot_{idx++}",
                            start = startTimeFormatted,
                            end = endTimeFormatted,
                            iso_start = startIso,
                            iso_end = endIso
                        });
                    }
                }
            }

            var durationButtons = new object[]
            {
                new { label = "⏱ 15 мин", minutes = 15, is_default = durationMinutes == 15 },
                new { label = "⏱ 30 мин", minutes = 30, is_default = durationMinutes == 30 },
                new { label = "⏱ 45 мин", minutes = 45, is_default = durationMinutes == 45 },
                new { label = "⏱ 1 час", minutes = 60, is_default = durationMinutes == 60 }
            };

            return new
            {
                type = "slot_selection",
                target_person = targetPerson,
                target_employee_id = targetEmployeeId,
                duration_minutes = durationMinutes,
                date = targetDateStr,
                slots = slotDtos,
                duration_buttons = durationButtons
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[FotoprintAiAgent] Не удалось детерминированно сконструировать ActionCard");
            return null;
        }
    }

    [RequireConfirmation(true)]
    private async Task<object> Tool_schedule_meeting(JsonElement arguments, long telegramId, string traceId, bool isConfirmed, CancellationToken ct)
    {
        var title = TryGetStringArg(arguments, "title") ?? "Рабочее совещание";
        var description = TryGetStringArg(arguments, "description");
        var startTime = TryGetStringArg(arguments, "start_time");
        var durationMinutes = TryGetIntArg(arguments, "duration_minutes") ?? 30;
        var participants = TryGetStringArg(arguments, "participants");

        var client = _httpClientFactory.CreateClient();
        using var req = CreateMcpRequest(HttpMethod.Post, $"{CalendarMcpBaseUrl}/tools/schedule_meeting", telegramId, traceId, isConfirmed);
        req.Content = JsonContent.Create(new
        {
            title,
            description,
            startTime,
            durationMinutes,
            participants
        });
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        return new { status = "error", message = $"Календарь вернул код {res.StatusCode}" };
    }

        [RequireConfirmation(false)]
    private async Task<object> Tool_search_meeting_rag(JsonElement arguments, long telegramId, string traceId, CancellationToken ct)
    {
        var query = TryGetStringArg(arguments, "query") ?? "";
        var topK = TryGetIntArg(arguments, "top_k") ?? (_config.Rag.TopK > 0 ? _config.Rag.TopK : 5);
        var url = $"{CalendarMcpBaseUrl}/tools/search_meeting_rag?query={Uri.EscapeDataString(query)}&top_k={topK}";

        try
        {
            return await GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "MCP-Calendar недоступен по HTTP ({Url}), используется локальный fallback поиска по meeting_chunks", url);
        }

        var secContext = await _repo.GetEmployeeSecurityContextAsync(telegramId, ct);
        if (secContext == null)
        {
            return new { found = false, access_denied = true, message = "Не удалось определить сотрудника для поиска по созвонам." };
        }

        var meetingAccessGroups = new List<string> { $"master:{secContext.MasterId}" };
        if (secContext.IsExecutive)
        {
            meetingAccessGroups.Add("c-level");
        }

        if (_embeddingClient is not { IsConfigured: true })
        {
            return new { found = false, storage_table = "meeting_chunks", message = "Сервис эмбеддингов не настроен." };
        }

        var queryVector = await _embeddingClient.EmbedTextAsync(query, "RETRIEVAL_QUERY", ct);
        if (queryVector == null)
        {
            return new { found = false, storage_table = "meeting_chunks", message = "Не удалось построить эмбеддинг запроса." };
        }

        var matchedChunks = await _repo.SearchMeetingSimilarChunksAsync(queryVector, meetingAccessGroups, topK, ct);
        if (matchedChunks.Count == 0)
        {
            return new
            {
                found = false,
                storage_table = "meeting_chunks",
                master_id = secContext.MasterId,
                message = "По вашему запросу не найдено доступных созвонов (доступ имеют только участники встречи и директор)."
            };
        }

        var bestMeetingId = matchedChunks[0].DocumentId;
        var meeting = bestMeetingId != Guid.Empty ? await _repo.GetMeetingByIdAsync(bestMeetingId, ct) : null;
        var protocol = bestMeetingId != Guid.Empty ? await _repo.GetMeetingProtocolAsync(bestMeetingId, ct) : null;

        return new
        {
            found = true,
            storage_table = "meeting_chunks",
            master_id = secContext.MasterId,
            meeting_id = bestMeetingId,
            title = meeting?.Title ?? protocol?.Title ?? "Созвон Google Meet",
            summary = protocol?.Summary ?? meeting?.Description ?? matchedChunks[0].ChunkText,
            matched_chunks = matchedChunks.Select(c => new { chunk_id = c.Id, meeting_id = c.DocumentId, text = c.ChunkText }).ToList(),
            export_docx_url = bestMeetingId != Guid.Empty ? $"/api/meetings/{bestMeetingId}/export-full?format=docx" : null,
            export_pdf_url = bestMeetingId != Guid.Empty ? $"/api/meetings/{bestMeetingId}/export-full?format=pdf" : null
        };
    }

    [RequireConfirmation(false)]
    private async Task<object> Tool_get_notification_settings(
        int defaultEmployeeId,
        EmployeeSecurityContext? secContext,
        CancellationToken ct)
    {
        if (defaultEmployeeId <= 0)
        {
            return new { status = "error", message = "Не удалось определить профиль сотрудника для чтения настроек уведомлений." };
        }

        var prefs = await _repo.GetNotificationPreferencesAsync(defaultEmployeeId, secContext?.MasterId ?? 0, ct);
        var employee = await _repo.GetEmployeeByIdAsync(defaultEmployeeId, ct);
        var notificationsEnabled = employee?.NotificationsEnabled ?? true;

        return new
        {
            status = "ok",
            employee_id = defaultEmployeeId,
            employee_name = secContext?.FullName,
            notifications_enabled = notificationsEnabled,
            digest_enabled = prefs.DigestEnabled,
            digest_time_msk = prefs.DigestTimeMsk,
            digest_weekdays_only = prefs.DigestWeekdaysOnly,
            pre_meeting_enabled = prefs.PreMeetingEnabled,
            pre_meeting_window_minutes = prefs.PreMeetingWindowMinutes,
            task_deadlines_enabled = prefs.TaskDeadlinesEnabled,
            task_due_soon_hours = prefs.TaskDueSoonHours,
            task_urgent_hours = prefs.TaskUrgentHours,
            quiet_hours_start_msk = prefs.QuietHoursStartMsk,
            quiet_hours_end_msk = prefs.QuietHoursEndMsk,
            preferred_channel = prefs.PreferredChannel
        };
    }

    [RequireConfirmation(false)]
    private async Task<object> Tool_update_notification_settings(
        JsonElement arguments,
        int defaultEmployeeId,
        EmployeeSecurityContext? secContext,
        CancellationToken ct)
    {
        if (defaultEmployeeId <= 0)
        {
            return new { status = "error", message = "Не удалось определить профиль сотрудника для сохранения настроек уведомлений." };
        }

        var digestTimeArg = TryGetStringArg(arguments, "digest_time_msk");
        if (!string.IsNullOrWhiteSpace(digestTimeArg) && !Notifications.NotificationTimeHelper.TryParseTimeMsk(digestTimeArg, out _))
        {
            return new
            {
                status = "validation_error",
                message = $"Некорректный формат времени утренней сводки: '{digestTimeArg}'. Укажите время в формате ЧЧ:ММ (например '08:30' или '10:00')."
            };
        }

        var quietStartArg = TryGetStringArg(arguments, "quiet_hours_start_msk");
        if (!string.IsNullOrWhiteSpace(quietStartArg) && !Notifications.NotificationTimeHelper.TryParseTimeMsk(quietStartArg, out _))
        {
            return new
            {
                status = "validation_error",
                message = $"Некорректный формат начала тихих часов: '{quietStartArg}'. Укажите время в формате ЧЧ:ММ (например '22:00')."
            };
        }

        var quietEndArg = TryGetStringArg(arguments, "quiet_hours_end_msk");
        if (!string.IsNullOrWhiteSpace(quietEndArg) && !Notifications.NotificationTimeHelper.TryParseTimeMsk(quietEndArg, out _))
        {
            return new
            {
                status = "validation_error",
                message = $"Некорректный формат окончания тихих часов: '{quietEndArg}'. Укажите время в формате ЧЧ:ММ (например '08:00')."
            };
        }

        var patch = new UpdateNotificationPreferencesPatch(
            NotificationsEnabled: TryGetBoolArg(arguments, "notifications_enabled"),
            DigestEnabled: TryGetBoolArg(arguments, "digest_enabled"),
            DigestTimeMsk: digestTimeArg,
            DigestWeekdaysOnly: TryGetBoolArg(arguments, "digest_weekdays_only"),
            PreMeetingEnabled: TryGetBoolArg(arguments, "pre_meeting_enabled"),
            PreMeetingWindowMinutes: TryGetIntArg(arguments, "pre_meeting_window_minutes"),
            TaskDeadlinesEnabled: TryGetBoolArg(arguments, "task_deadlines_enabled"),
            TaskDueSoonHours: TryGetIntArg(arguments, "task_due_soon_hours"),
            TaskUrgentHours: TryGetIntArg(arguments, "task_urgent_hours"),
            QuietHoursStartMsk: quietStartArg,
            QuietHoursEndMsk: quietEndArg,
            ClearQuietHours: TryGetBoolArg(arguments, "clear_quiet_hours") ?? false,
            PreferredChannel: TryGetStringArg(arguments, "preferred_channel"));

        var masterId = secContext?.MasterId ?? 0;
        var updated = await _repo.UpsertNotificationPreferencesAsync(defaultEmployeeId, masterId, patch, ct);
        var currentEmp = await _repo.GetEmployeeByIdAsync(defaultEmployeeId, ct);
        var effectiveGlobalEnabled = patch.NotificationsEnabled ?? (currentEmp?.NotificationsEnabled ?? true);

        bool resetTodayDigestDedup = false;
        if (!string.IsNullOrWhiteSpace(digestTimeArg))
        {
            var tz = Notifications.NotificationTimeHelper.ResolveTimeZone(_config.Environment.Timezone);
            var (_, _, mskDate) = Notifications.NotificationTimeHelper.GetDayBoundsUtc(DateTimeOffset.UtcNow, tz);
            var dedupKey = Notifications.NotificationTimeHelper.BuildMorningDigestDedupKey(mskDate, defaultEmployeeId);
            resetTodayDigestDedup = await _repo.ResetNotificationDeliveryByDedupKeyAsync(dedupKey, ct);
        }

        return new
        {
            status = "updated",
            employee_id = updated.EmployeeId,
            notifications_enabled = effectiveGlobalEnabled,
            digest_enabled = updated.DigestEnabled,
            digest_time_msk = updated.DigestTimeMsk,
            digest_weekdays_only = updated.DigestWeekdaysOnly,
            pre_meeting_enabled = updated.PreMeetingEnabled,
            pre_meeting_window_minutes = updated.PreMeetingWindowMinutes,
            task_deadlines_enabled = updated.TaskDeadlinesEnabled,
            task_due_soon_hours = updated.TaskDueSoonHours,
            task_urgent_hours = updated.TaskUrgentHours,
            quiet_hours_start_msk = updated.QuietHoursStartMsk,
            quiet_hours_end_msk = updated.QuietHoursEndMsk,
            preferred_channel = updated.PreferredChannel,
            today_digest_rescheduled = resetTodayDigestDedup,
            message = "Персональные настройки уведомлений успешно сохранены."
        };
    }

    [RequireConfirmation(false)]
    private async Task<object> Tool_search_typography_regulations(JsonElement arguments, long telegramId, string traceId, List<string> userAccessGroups, CancellationToken ct)
    {
        var query = TryGetStringArg(arguments, "query") ?? "";
        var topK = _config.Rag.TopK > 0 ? _config.Rag.TopK : 5;
        var url = $"{KnowledgeBaseMcpBaseUrl}/tools/search_typography_regulations?query={Uri.EscapeDataString(query)}&top_k={topK}";

        try
        {
            return await GetMcpJsonAsync(url, "MCP-KnowledgeBase", telegramId, traceId, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "MCP-KnowledgeBase недоступен по HTTP ({Url}), используется локальный fallback поиска по регламентам", url);
        }

        List<CorporateDocumentChunk>? chunks = null;
        if (_embeddingClient is { IsConfigured: true } && !FotoprintRepository.IsGeneralCatalogQuery(query))
        {
            var queryEmbedding = await _embeddingClient.EmbedTextAsync(query, "RETRIEVAL_QUERY", ct);
            if (queryEmbedding != null)
            {
                chunks = await _repo.SearchSimilarChunksAsync(queryEmbedding, userAccessGroups, topK, ct);
            }
        }

        if (chunks == null || chunks.Count == 0)
        {
            chunks = await _repo.SearchTextChunksAsync(query, userAccessGroups, topK, ct);
        }

        if (chunks.Count > 0)
        {
            return chunks.Select(c => new { chunk_id = c.Id, document_id = c.DocumentId, text = c.ChunkText }).ToList();
        }

        return new
        {
            status = "no_matches_or_restricted",
            query,
            user_access_groups = userAccessGroups,
            message = "В корпоративной базе знаний (RAG) не найдено фрагментов по данному запросу для ваших групп доступа."
        };
    }

    private static object GetAvailableTools()
    {
        return new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "get_order_status",
                    description = "Возвращает актуальный статус тиража/заказа из 1С:Управление нашей типографией (этап офсета, % готовности, дата отгрузки, наличие бумаги и краски).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            order_id = new { type = "string", description = "Номер заказа, например '44102' или 'ЗК-44102'" }
                        },
                        required = new[] { "order_id" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_production_kpis",
                    description = "Возвращает производственные показатели цехов из 1С: выработка оттисков, процент брака, загрузка офсетных машин Heidelberg, аварии и простои.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            department = new { type = "string", description = "Цех (например, 'Офсетный цех', 'Цех цифровой печати')" },
                            date_from = new { type = "string", description = "Дата начала периода (ГГГГ-ММ-ДД)" },
                            date_to = new { type = "string", description = "Дата окончания периода (ГГГГ-ММ-ДД)" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_user_tasks",
                    description = "Получает список активных и просроченных задач/поручений сотрудника из Битрикс24.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            employee_id = new { type = "integer", description = "ID сотрудника (если не указан — используется текущий сотрудник)" },
                            status_filter = new { type = "string", description = "Фильтр по статусу: pending, overdue, all" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_crm_deal_info",
                    description = "Получает информацию о CRM-сделке в Битрикс24 (клиент, сумма, стадия, оплата, ответственный менеджер).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            deal_query = new { type = "string", description = "Номер сделки, номер заказа или название клиента (например '44102' или 'Альфа-Маркет')" }
                        },
                        required = new[] { "deal_query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "create_task",
                    description = "Создает новую задачу/поручение в Битрикс24 (вызывать ТОЛЬКО после получения подтверждения плана действий от пользователя).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Название задачи" },
                            description = new { type = "string", description = "Подробное описание задачи" },
                            responsible_id = new { type = "integer", description = "ID ответственного сотрудника" },
                            deadline = new { type = "string", description = "Срок выполнения (ГГГГ-ММ-ДД)" }
                        },
                        required = new[] { "title" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "find_free_slots",
                    description = "Подобрать свободные слоты для встречи/совещания с сотрудниками (учитывает занятость в календаре, рабочий день 09:00-18:00, буфер между встречами 10 минут и отсекает прошедшее время). Возвращает массив доступных интервалов.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            date = new { type = "string", description = "Дата поиска в формате YYYY-MM-DD" },
                            duration_minutes = new { type = "integer", description = "Длительность встречи в минутах (по умолчанию 30)" },
                            participant_employee_ids = new
                            {
                                type = "array",
                                items = new { type = "integer" },
                                description = "Список master ID или employee ID участников встречи"
                            }
                        },
                        required = new[] { "date", "participant_employee_ids" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "schedule_meeting",
                    description = "Назначает (создает) новую встречу или совещание в корпоративном календаре (вызывать ТОЛЬКО после получения подтверждения плана действий от пользователя).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Тема встречи" },
                            description = new { type = "string", description = "Повестка и описание встречи" },
                            start_time = new { type = "string", description = "Дата и время начала (ГГГГ-ММ-ДД ЧЧ:ММ)" },
                            duration_minutes = new { type = "integer", description = "Длительность в минутах (по умолчанию 30)" },
                            participants = new { type = "string", description = "Участники встречи через запятую" }
                        },
                        required = new[] { "title" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_upcoming_meetings",
                    description = "Возвращает список предстоящих совещаний, планерок, тем и участников из корпоративного календаря.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            time_window_minutes = new { type = "integer", description = "Окно поиска встреч в минутах (например 60 или 1440)" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_meeting_history",
                    description = "Возвращает историю прошлых встреч, планерок, протоколов и поручений по ключевым словам темы.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            topic_keywords = new { type = "string", description = "Ключевые слова темы, например 'производство', 'каталог', '44102'" }
                        },
                        required = new[] { "topic_keywords" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "search_meeting_rag",
                    description = "Ищет в изолированном RAG-хранилище прошедших созвонов (Google Meet) саммари, решения и фрагменты стенограмм по смыслу вопроса пользователя. Возвращает только встречи, где текущий сотрудник был участником (или любые для Директора).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Вопрос пользователя о содержании или решениях прошедшего созвона" },
                            top_k = new { type = "integer", description = "Максимум релевантных фрагментов (по умолчанию 5)" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "search_typography_regulations",
                    description = "Ищет в корпоративной базе знаний (RAG регламентов) регламенты типографии, нормы брака оборудования Heidelberg, коммерческую политику и скидки, положение об оплате сверхурочных, правила внутреннего трудового распорядка и список доступных документов (передай query='*' или 'все' для просмотра всех документов в RAG).",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Поисковый запрос по базе знаний / регламентам (или '*' для обзора всех документов в RAG)" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_notification_settings",
                    description = "Возвращает текущие персональные настройки уведомлений и напоминаний сотрудника (время утренней сводки по МСК, за сколько минут напоминать о встречах, за сколько часов напоминать о дедлайнах задач, тихие часы, статус включения).",
                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "update_notification_settings",
                    description = "Изменяет персональные настройки времени и параметров уведомлений сотрудника по его просьбе в чате (не требует подтверждения, применяется сразу). Передавай только те поля, которые просит изменить пользователь.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            notifications_enabled = new { type = "boolean", description = "Включить (true) или полностью отключить (false) все автоматические уведомления" },
                            digest_enabled = new { type = "boolean", description = "Включить (true) или отключить (false) утреннюю сводку расписания и задач" },
                            digest_time_msk = new { type = "string", description = "Время отправки утренней сводки по МСК в формате ЧЧ:ММ (например '08:30', '09:00', '10:15')" },
                            digest_weekdays_only = new { type = "boolean", description = "true — присылать утреннюю сводку только по будням (пн–пт); false — присылать ежедневно (включая выходные)" },
                            pre_meeting_enabled = new { type = "boolean", description = "Включить (true) или отключить (false) напоминания перед началом встреч" },
                            pre_meeting_window_minutes = new { type = "integer", description = "За сколько минут до начала встречи присылать напоминание (от 5 до 240, например 15, 30, 60)" },
                            task_deadlines_enabled = new { type = "boolean", description = "Включить (true) или отключить (false) напоминания о дедлайнах задач" },
                            task_due_soon_hours = new { type = "integer", description = "За сколько часов до дедлайна присылать заблаговременное напоминание (по умолчанию 24)" },
                            task_urgent_hours = new { type = "integer", description = "За сколько часов до дедлайна присылать срочное напоминание (по умолчанию 2)" },
                            quiet_hours_start_msk = new { type = "string", description = "Начало тихих часов по МСК в формате ЧЧ:ММ (например '22:00')" },
                            quiet_hours_end_msk = new { type = "string", description = "Конец тихих часов по МСК в формате ЧЧ:ММ (например '08:00')" },
                            clear_quiet_hours = new { type = "boolean", description = "Установить true, если пользователь просит отключить/сбросить тихие часы" },
                            preferred_channel = new { type = "string", description = "Предпочтительный канал уведомлений ('telegram' или 'crm_webhook')" }
                        }
                    }
                }
            }
        };
    }
}
