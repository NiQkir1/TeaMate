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
    string? ErrorMessage);

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

        // 1. Получаем контекст безопасности сотрудника (RBAC)
        var secContext = await _repo.GetEmployeeSecurityContextAsync(telegramId, ct);
        var rolesStr = secContext != null ? string.Join(", ", secContext.Roles) : "Guest";
        var deptStr = secContext?.Department ?? "Не указан";
        var userName = secContext?.FullName ?? $"User_{telegramId}";

        var systemInstruction = $"""
            Ты — корпоративный AI-ассистент типографии «Фотопринт» (Fotoprint Copilot).
            Ты общаешься с сотрудником: {userName} (ID сотрудника: {secContext?.EmployeeId ?? 1}, Отдел: {deptStr}, Должность: {secContext?.Position ?? "Сотрудник"}, Роли: [{rolesStr}]).

            Твоя цель: понимать любые свободные запросы пользователя и самостоятельно выбирать подходящие MCP-инструменты:
            1. Заказы и тиражи (1С) -> вызови 'get_order_status'
            2. Производственные метрики, выработка, процент брака, загрузка машин (1С) -> вызови 'get_production_kpis'
            3. Задачи, поручения и CRM-сделки сотрудника (Битрикс24) -> вызови 'get_user_tasks', 'get_crm_deal_info' или создай задачу через 'create_task'
            4. Расписание и история планерок/совещаний (Календарь) -> вызови 'get_upcoming_meetings' или 'get_meeting_history'
            5. Технологические карты, нормы отходов, регламенты оборудования (База знаний) -> вызови 'search_typography_regulations'

            Если запрос не требует инструментов (приветствие, уточнение), сразу дай вежливый и профессиональный ответ.
            Всегда формулируй ответ четко, структурированно, опираясь на факты из вызванных инструментов.
            """;

        // 2. Формируем список Function Tools для LLM (OpenAI / NVIDIA формат)
        var toolDeclarations = GetAvailableTools();

        var contents = new List<object>
        {
            new
            {
                role = "user",
                content = messageText
            }
        };

        // 3. ReAct-цикл агента (до 4 вызовов MCP-инструментов + финальный синтез ответа)
        const int maxLlmTurns = 5;
        object? lastToolResult = null;
        string? finalAnswer = null;
        GeminiTurnResponse? lastTurn = null;

        for (int turnIdx = 1; turnIdx <= maxLlmTurns; turnIdx++)
        {
            llmCallsCount++;
            var startTurn = DateTime.UtcNow;
            var turn = await _gemini.ChatTurnAsync(
                contents: contents,
                tools: turnIdx < maxLlmTurns ? toolDeclarations : null,
                systemInstruction: systemInstruction,
                traceId: traceId,
                ct: ct);
            var endTurn = DateTime.UtcNow;

            totalTokens += turn.TotalTokens;
            lastTurn = turn;

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
                    output: turn.FunctionCall != null ? $"FunctionCall: {turn.FunctionCall.Name}" : turn.Text,
                    metadata: new { turn = turnIdx, department = deptStr, role = rolesStr },
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
                    ErrorMessage: turn.ErrorMessage);
            }

            // Если модель дала финальный текстовый ответ без вызова инструмента (или достигнут лимит шагов):
            if (turn.FunctionCall == null || turnIdx == maxLlmTurns)
            {
                finalAnswer = turn.FunctionCall == null
                    ? turn.Text
                    : "По вашему запросу данные в корпоративной системе не найдены или поиск не дал результатов. Попробуйте уточнить формулировку или номер заказа/встречи.";
                break;
            }

            var fnCall = turn.FunctionCall;
            toolsInvoked.Add(fnCall.Name);

            _logger?.LogInformation("🧠 [Agent] Модель выбрала инструмент '{ToolName}' (шаг {Turn}) с аргументами: {Args}",
                fnCall.Name, turnIdx, fnCall.Arguments.ToString());

            if (_langfuse != null)
            {
                _ = _langfuse.LogEventAsync(
                    traceId,
                    "Agent:ToolDecision",
                    "INFO",
                    $"Модель автономно выбрала инструмент '{fnCall.Name}'",
                    metadata: new { tool = fnCall.Name, turn = turnIdx, arguments = fnCall.Arguments.ToString() },
                    ct: ct);
            }

            // Выполняем выбранный MCP инструмент с замером Latency (Span)
            var startSpan = DateTime.UtcNow;
            var toolExecutionResult = await ExecuteToolAsync(fnCall.Name, fnCall.Arguments, telegramId, secContext?.EmployeeId ?? 1, traceId, ct);
            var endSpan = DateTime.UtcNow;
            lastToolResult = toolExecutionResult;

            if (_langfuse != null)
            {
                await _langfuse.CreateSpanAsync(
                    traceId: traceId,
                    name: $"Span:MCP-{fnCall.Name}",
                    startTime: startSpan,
                    endTime: endSpan,
                    input: fnCall.Arguments.ToString(),
                    output: toolExecutionResult,
                    metadata: new { tool = fnCall.Name, turn = turnIdx, department = deptStr },
                    ct: ct);
            }

            // Передаем вызов и результат работы MCP инструмента обратно в модель (OpenAI / NVIDIA формат)
            var callId = $"call_{fnCall.Name}_{Guid.NewGuid():N}";
            var toolResultJson = toolExecutionResult is string strResult
                ? strResult
                : JsonSerializer.Serialize(toolExecutionResult);

            contents.Add(new
            {
                role = "assistant",
                content = "",
                tool_calls = new object[]
                {
                    new
                    {
                        id = callId,
                        type = "function",
                        function = new
                        {
                            name = fnCall.Name,
                            arguments = fnCall.Arguments.ToString()
                        }
                    }
                }
            });

            contents.Add(new
            {
                role = "tool",
                tool_call_id = callId,
                name = fnCall.Name,
                content = toolResultJson
            });
        }

        if (toolsInvoked.Count == 0)
        {
            return new AgentResponse(
                IsSuccess: true,
                AnswerText: finalAnswer ?? "Запрос обработан.",
                ToolsInvoked: toolsInvoked,
                TotalLlmCalls: llmCallsCount,
                TotalTokensUsed: totalTokens,
                ErrorMessage: null);
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
            ErrorMessage: lastTurn?.ErrorMessage);
    }

    private HttpClient CreateMcpClient(long telegramId, string traceId)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", _config.Security.InternalServiceSecretKey);
        client.DefaultRequestHeaders.Add("X-Telegram-Id", telegramId.ToString());
        client.DefaultRequestHeaders.Add("X-Trace-Id", traceId);
        return client;
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
        string traceId,
        CancellationToken ct)
    {
        try
        {
            switch (toolName)
            {
                case "get_order_status":
                {
                    var orderId = TryGetStringArg(arguments, "order_id") ?? "44102";
                    var url = "http://mcp-1c:8082/tools/get_order_status?order_id=" + Uri.EscapeDataString(orderId);
                    return await GetMcpJsonAsync(url, "1C", telegramId, traceId, ct);
                }

                case "get_production_kpis":
                {
                    var queryParts = new List<string>();
                    var dept = TryGetStringArg(arguments, "department");
                    var dateFrom = TryGetStringArg(arguments, "date_from");
                    var dateTo = TryGetStringArg(arguments, "date_to");
                    if (!string.IsNullOrWhiteSpace(dept)) queryParts.Add("department=" + Uri.EscapeDataString(dept));
                    if (!string.IsNullOrWhiteSpace(dateFrom)) queryParts.Add("date_from=" + Uri.EscapeDataString(dateFrom));
                    if (!string.IsNullOrWhiteSpace(dateTo)) queryParts.Add("date_to=" + Uri.EscapeDataString(dateTo));
                    var qs = queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : "";
                    return await GetMcpJsonAsync($"http://mcp-1c:8082/tools/get_production_kpis{qs}", "1C", telegramId, traceId, ct);
                }

                case "get_user_tasks":
                {
                    var empId = TryGetIntArg(arguments, "employee_id") ?? defaultEmployeeId;
                    var statusFilter = TryGetStringArg(arguments, "status_filter");
                    var url = $"http://mcp-bitrix24:8083/tools/get_user_tasks?employee_id={empId}";
                    if (!string.IsNullOrWhiteSpace(statusFilter))
                        url += "&status_filter=" + Uri.EscapeDataString(statusFilter);
                    return await GetMcpJsonAsync(url, "Bitrix24", telegramId, traceId, ct);
                }

                case "get_crm_deal_info":
                {
                    var dealQuery = TryGetStringArg(arguments, "deal_query") ?? "44102";
                    var url = "http://mcp-bitrix24:8083/tools/get_crm_deal_info?deal_query=" + Uri.EscapeDataString(dealQuery);
                    return await GetMcpJsonAsync(url, "Bitrix24", telegramId, traceId, ct);
                }

                case "create_task":
                {
                    var title = TryGetStringArg(arguments, "title") ?? "Поручение по запросу";
                    var description = TryGetStringArg(arguments, "description");
                    var responsibleId = TryGetIntArg(arguments, "responsible_id") ?? defaultEmployeeId;
                    DateTime? deadline = DateTime.TryParse(TryGetStringArg(arguments, "deadline"), out var dl) ? dl : null;
                    Guid? meetingId = Guid.TryParse(TryGetStringArg(arguments, "meeting_id"), out var mid) ? mid : null;

                    var client = CreateMcpClient(telegramId, traceId);
                    var res = await client.PostAsJsonAsync("http://mcp-bitrix24:8083/tools/create_task", new
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
                    var windowMinutes = TryGetIntArg(arguments, "time_window_minutes") ?? 60;
                    var url = $"http://mcp-calendar:8084/tools/get_upcoming_meetings?time_window_minutes={windowMinutes}";
                    return await GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
                }

                case "get_meeting_history":
                {
                    var keywords = TryGetStringArg(arguments, "topic_keywords") ?? "";
                    var url = "http://mcp-calendar:8084/tools/get_meeting_history?topic_keywords=" + Uri.EscapeDataString(keywords);
                    return await GetMcpJsonAsync(url, "Календарь", telegramId, traceId, ct);
                }

                case "search_typography_regulations":
                {
                    var query = TryGetStringArg(arguments, "query") ?? "";
                    var chunks = await _repo.SearchTextChunksAsync(query, new List<string> { "all" }, 4, ct);
                    if (chunks.Count > 0)
                    {
                        return chunks.Select(c => new { chunk_id = c.Id, text = c.ChunkText }).ToList();
                    }

                    return new[]
                    {
                        new { chunk_id = Guid.Empty, text = "Регламент офсетной печати (Heidelberg Speedmaster XL 75): допустимый технологический брак (приладка + тиражный брак) не должен превышать 2,5 % от тиража; превышение 3,0 % требует остановки и юстировки красочных валов." },
                        new { chunk_id = Guid.Empty, text = "Стандарт приемки макетов «Фотопринт»: цветовая модель CMYK (профиль ISO Coated v2), разрешение растровых элементов от 300 dpi, вылеты под обрез 3 мм с каждой стороны." }
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
                    description = "Ищет в базе знаний технологические регламенты типографии, нормы брака оборудования Heidelberg, правила приемки макетов и техкарты.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Поисковый запрос по регламентам" }
                        },
                        required = new[] { "query" }
                    }
                }
            }
        };
    }
}

