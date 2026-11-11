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
    private readonly IGeminiClient _gemini;
    private readonly IFotoprintRepository _repo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FotoprintConfig _config;
    private readonly ILangfuseClient? _langfuse;
    private readonly ILlmJudgeService? _judgeService;
    private readonly ILogger<FotoprintAiAgent>? _logger;

    public FotoprintAiAgent(
        IGeminiClient gemini,
        IFotoprintRepository repo,
        IHttpClientFactory httpClientFactory,
        FotoprintConfig config,
        ILangfuseClient? langfuse = null,
        ILlmJudgeService? judgeService = null,
        ILogger<FotoprintAiAgent>? logger = null)
    {
        _gemini = gemini;
        _repo = repo;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _langfuse = langfuse;
        _judgeService = judgeService;
        _logger = logger;
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
        int llmCallsCount = 0;
        int totalTokens = 0;
        object? capturedActionCard = null;

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

        var systemInstruction = $"""
            Ты — корпоративный AI-ассистент типографии «Фотопринт» (Fotoprint Copilot).
            Ты общаешься с сотрудником: {userName} (ID сотрудника: {secContext?.EmployeeId ?? 0}, Отдел: {deptStr}, Должность: {secContext?.Position ?? "Сотрудник"}, Роли: [{rolesStr}]).

            Твоя цель: понимать любые свободные запросы пользователя и самостоятельно выбирать подходящие MCP-инструменты:
            1. Заказы и тиражи (1С) -> вызови 'get_order_status'
            2. Производственные метрики, выработка, процент брака, загрузка машин (1С) -> вызови 'get_production_kpis'
            3. Задачи, поручения и CRM-сделки сотрудника (Битрикс24) -> вызови 'get_user_tasks', 'get_crm_deal_info' или создай задачу через 'create_task'
            4. Расписание, подбор окон и история планерок/совещаний (Календарь) -> вызови 'find_free_slots' для подбора времени встречи, 'get_upcoming_meetings' или 'get_meeting_history'
            5. Технологические карты, нормы отходов, регламенты оборудования, коммерческая политика/скидки, оплата сверхурочных, правила внутреннего трудового распорядка, а также любые вопросы о том, какие документы/данные лежат в RAG (базе знаний) -> вызови 'search_typography_regulations'

            Если запрос требует данных из нескольких систем (например, заказ в 1С, задачи в Битрикс24 и встречи в календаре), вызывай все нужные инструменты сразу за один шаг.
            Если запрос не требует инструментов (приветствие, уточнение), сразу дай вежливый и профессиональный ответ.
            Всегда формулируй ответ четко, структурированно, опираясь на факты из вызванных инструментов.
            """;

        // 2. Формируем список Function Tools для LLM (OpenAI / NVIDIA формат)
        var toolDeclarations = GetAvailableTools();

        var contents = new List<object>();
        const int maxHistoryMessagesInContext = 10;
        int loadedHistoryCount = 0;

        // Подгружаем историю текущей активной сессии (до 10 последних незамороженных сообщений: если 9 — берем 9, если 11+ — берем 10 последних)
        if (secContext != null && secContext.MasterId > 0)
        {
            try
            {
                var activeSession = await _repo.GetOrCreateActiveSessionByMasterIdAsync(secContext.MasterId, chatId, "web", ct);
                var sessionMessages = await _repo.GetMessagesBySessionIdAsync(activeSession.Id, limit: maxHistoryMessagesInContext + 5, ct: ct);

                var validHistory = sessionMessages
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
            var toolsForTurn = turnIdx < maxLlmTurns ? toolDeclarations : null;
            var turn = toolsInvoked.Count > 0
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
                    model: _config.Llm.Models.ChatModel,
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
                        ? JsonSerializer.Serialize(lastToolResult)
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

            foreach (var fnCall in calls)
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
            var execTasks = calls.Select(async fnCall =>
            {
                var callId = !string.IsNullOrWhiteSpace(fnCall.CallId)
                    ? fnCall.CallId!
                    : $"call_{fnCall.Name}_{Guid.NewGuid():N}";
                var startSpan = DateTime.UtcNow;
                var result = await ExecuteToolAsync(fnCall.Name, fnCall.Arguments, telegramId, empId, userAccessGroups, traceId, ct);
                var endSpan = DateTime.UtcNow;
                return (Call: fnCall, CallId: callId, Result: result, Start: startSpan, End: endSpan);
            });
            var execResults = await Task.WhenAll(execTasks);

            foreach (var r in execResults)
            {
                if (r.Call.Name == "find_free_slots" && r.Result != null)
                {
                    capturedActionCard = await BuildSlotSelectionActionCardAsync(r.Call.Arguments, r.Result, ct);
                }
            }

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
                var toolResultJson = r.Result is string strResult
                    ? strResult
                    : JsonSerializer.Serialize(r.Result);

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
            return new AgentResponse(
                IsSuccess: true,
                AnswerText: finalAnswer ?? "Запрос обработан.",
                ToolsInvoked: toolsInvoked,
                TotalLlmCalls: llmCallsCount,
                TotalTokensUsed: totalTokens,
                ErrorMessage: null,
                ActionCard: capturedActionCard);
        }

        var finalText = finalAnswer ?? (lastToolResult != null ? JsonSerializer.Serialize(lastToolResult) : "Запрос обработан.");

        // 4. Фоновая оценка адекватности и качества (LLM-as-a-Judge)
        if (_judgeService != null && _config.Telemetry.Evaluations.EnableLlmAsJudge)
        {
            var rawContextJson = JsonSerializer.Serialize(lastToolResult);
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

        return new AgentResponse(
            IsSuccess: lastTurn?.IsSuccess ?? true,
            AnswerText: finalText,
            ToolsInvoked: toolsInvoked,
            TotalLlmCalls: llmCallsCount,
            TotalTokensUsed: totalTokens,
            ErrorMessage: lastTurn?.ErrorMessage,
            ActionCard: capturedActionCard);
    }

    private HttpClient CreateMcpClient(long telegramId, string traceId)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", _config.Security.InternalServiceSecretKey);
        client.DefaultRequestHeaders.Add("X-Telegram-Id", telegramId.ToString());
        client.DefaultRequestHeaders.Add("X-Trace-Id", traceId);
        return client;
    }

    private static string ResolveMcpUrl(string? rawUrl, string localFallback)
    {
        var url = string.IsNullOrWhiteSpace(rawUrl) ? localFallback : rawUrl;
        if (!File.Exists("/.dockerenv") && Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
        {
            url = url.Replace("mcp-calendar", "127.0.0.1")
                     .Replace("mcp-employee-reader", "127.0.0.1")
                     .Replace("mcp-1c", "127.0.0.1")
                     .Replace("mcp-bitrix24", "127.0.0.1");
        }
        return url.TrimEnd('/');
    }

    private async Task<object> GetMcpJsonAsync(string mcpUrl, string serviceName, long telegramId, string traceId, CancellationToken ct)
    {
        var client = CreateMcpClient(telegramId, traceId);
        var res = await client.GetAsync(mcpUrl, ct);
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

    private async Task<object> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        long telegramId,
        int defaultEmployeeId,
        List<string> userAccessGroups,
        string traceId,
        CancellationToken ct)
    {
        try
        {
            switch (toolName)
            {
                case "get_order_status":
                {
                    var oneCBaseUrl = ResolveMcpUrl(_config.McpUrls?.OneC, "http://localhost:8082");
                    var orderId = TryGetStringArg(arguments, "order_id") ?? "44102";
                    var url = $"{oneCBaseUrl}/tools/get_order_status?order_id=" + Uri.EscapeDataString(orderId);
                    return await GetMcpJsonAsync(url, "1C", telegramId, traceId, ct);
                }

                case "get_production_kpis":
                {
                    var oneCBaseUrl = ResolveMcpUrl(_config.McpUrls?.OneC, "http://localhost:8082");
                    var queryParts = new List<string>();
                    var dept = TryGetStringArg(arguments, "department");
                    var dateFrom = TryGetStringArg(arguments, "date_from");
                    var dateTo = TryGetStringArg(arguments, "date_to");
                    if (!string.IsNullOrWhiteSpace(dept)) queryParts.Add("department=" + Uri.EscapeDataString(dept));
                    if (!string.IsNullOrWhiteSpace(dateFrom)) queryParts.Add("date_from=" + Uri.EscapeDataString(dateFrom));
                    if (!string.IsNullOrWhiteSpace(dateTo)) queryParts.Add("date_to=" + Uri.EscapeDataString(dateTo));
                    var qs = queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : "";
                    return await GetMcpJsonAsync($"{oneCBaseUrl}/tools/get_production_kpis{qs}", "1C", telegramId, traceId, ct);
                }

                case "get_user_tasks":
                {
                    var bitrixBaseUrl = ResolveMcpUrl(_config.McpUrls?.Bitrix24, "http://localhost:8083");
                    var empId = TryGetIntArg(arguments, "employee_id") ?? defaultEmployeeId;
                    var statusFilter = TryGetStringArg(arguments, "status_filter");
                    var url = $"{bitrixBaseUrl}/tools/get_user_tasks?employee_id={empId}";
                    if (!string.IsNullOrWhiteSpace(statusFilter))
                        url += "&status_filter=" + Uri.EscapeDataString(statusFilter);
                    return await GetMcpJsonAsync(url, "Bitrix24", telegramId, traceId, ct);
                }

                case "get_crm_deal_info":
                {
                    var bitrixBaseUrl = ResolveMcpUrl(_config.McpUrls?.Bitrix24, "http://localhost:8083");
                    var dealQuery = TryGetStringArg(arguments, "deal_query") ?? "44102";
                    var url = $"{bitrixBaseUrl}/tools/get_crm_deal_info?deal_query=" + Uri.EscapeDataString(dealQuery);
                    return await GetMcpJsonAsync(url, "Bitrix24", telegramId, traceId, ct);
                }

                case "create_task":
                {
                    var bitrixBaseUrl = ResolveMcpUrl(_config.McpUrls?.Bitrix24, "http://localhost:8083");
                    var title = TryGetStringArg(arguments, "title") ?? "Поручение по запросу";
                    var description = TryGetStringArg(arguments, "description");
                    var responsibleId = TryGetIntArg(arguments, "responsible_id") ?? defaultEmployeeId;
                    DateTime? deadline = DateTime.TryParse(TryGetStringArg(arguments, "deadline"), out var dl) ? dl : null;
                    Guid? meetingId = Guid.TryParse(TryGetStringArg(arguments, "meeting_id"), out var mid) ? mid : null;

                    var client = CreateMcpClient(telegramId, traceId);
                    var res = await client.PostAsJsonAsync($"{bitrixBaseUrl}/tools/create_task", new
                    {
                        title,
                        description,
                        responsibleId,
                        deadline,
                        meetingId
                    }, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    }
                    return new { status = "error", message = $"Bitrix24 вернул код {res.StatusCode}" };
                }

                case "get_upcoming_meetings":
                {
                    var calendarBaseUrl = ResolveMcpUrl(_config.McpUrls?.Calendar, "http://localhost:8084");
                    var windowMinutes = TryGetIntArg(arguments, "time_window_minutes") ?? 60;
                    var url = $"{calendarBaseUrl}/tools/get_upcoming_meetings?time_window_minutes={windowMinutes}";
                    return await GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
                }

                case "get_meeting_history":
                {
                    var calendarBaseUrl = ResolveMcpUrl(_config.McpUrls?.Calendar, "http://localhost:8084");
                    var keywords = TryGetStringArg(arguments, "topic_keywords") ?? "";
                    var url = $"{calendarBaseUrl}/tools/get_meeting_history?topic_keywords=" + Uri.EscapeDataString(keywords);
                    return await GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
                }

                case "find_free_slots":
                {
                    var calendarBaseUrl = ResolveMcpUrl(_config.McpUrls?.Calendar, "http://localhost:8084");
                    var orgId = TryGetIntArg(arguments, "organizer_employee_id") ?? defaultEmployeeId;
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

                    var dateStr = TryGetStringArg(arguments, "date") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
                    var duration = TryGetIntArg(arguments, "duration_minutes") ?? 30;

                    var client = CreateMcpClient(telegramId, traceId);
                    var res = await client.PostAsJsonAsync($"{calendarBaseUrl}/tools/find_free_slots", new
                    {
                        organizer_employee_id = orgId,
                        participant_employee_ids = participantIds,
                        date = dateStr,
                        duration_minutes = duration,
                        working_start = _config.Calendar.WorkingHoursStart ?? "09:00",
                        working_end = _config.Calendar.WorkingHoursEnd ?? "18:00",
                        buffer_minutes = _config.Calendar.DefaultBufferMinutes
                    }, ct);

                    if (res.IsSuccessStatusCode)
                    {
                        return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    }
                    return new { status = "error", message = $"MCP-Calendar вернул код {res.StatusCode}" };
                }

                case "search_typography_regulations":
                {
                    var query = TryGetStringArg(arguments, "query") ?? "";
                    var topK = _config.Rag.TopK > 0 ? _config.Rag.TopK : 5;
                    var chunks = await _repo.SearchTextChunksAsync(query, userAccessGroups, topK, ct);
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

                default:
                    return new { status = "unknown_tool", tool = toolName };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка исполнения инструмента {ToolName}", toolName);
            return new { error = ex.Message };
        }
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
                    description = "Создает новую задачу/поручение в Битрикс24.",
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
                    description = "Ищет свободные временные слоты (окна) в календаре сотрудников типографии на указанную дату с учетом рабочего дня (09:00–18:00 МСК), 10-минутного буфера и отсечения прошедшего времени.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            organizer_employee_id = new { type = "integer", description = "ID сотрудника-организатора (по умолчанию текущий пользователь)" },
                            participant_employee_ids = new { type = "array", items = new { type = "integer" }, description = "Список ID сотрудников-участников встречи" },
                            date = new { type = "string", description = "Целевая дата в формате YYYY-MM-DD" },
                            duration_minutes = new { type = "integer", description = "Длительность встречи в минутах (по умолчанию 30)" }
                        },
                        required = new[] { "date" }
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
                    name = "search_typography_regulations",
                    description = "Ищет в корпоративной базе знаний (RAG) регламенты типографии, нормы брака оборудования Heidelberg, коммерческую политику и скидки, положение об оплате сверхурочных, правила внутреннего трудового распорядка и список доступных документов (передай query='*' или 'все' для просмотра всех документов в RAG).",
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
            }
        };
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
}

