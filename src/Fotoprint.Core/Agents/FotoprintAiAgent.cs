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
            Ты общаешься с сотрудником: {userName} (Отдел: {deptStr}, Должность: {secContext?.Position ?? "Сотрудник"}, Роли: [{rolesStr}]).

            Твоя цель: понимать любые свободные запросы пользователя и самостоятельно выбирать подходящие MCP-инструменты:
            1. Заказы и тиражи -> вызови инструмент 'get_order_status'
            2. Производственные метрики, выработка, процент брака, отчеты для руководства -> вызови 'get_production_kpis'
            3. Задачи, поручения и CRM-сделки -> вызови 'get_user_tasks', 'get_crm_deal_info' или 'create_meeting_task'
            4. Расписание планерок и совещаний -> вызови 'get_upcoming_meetings'
            5. Технологические карты, нормы отходов, регламенты оборудования -> вызови 'search_typography_regulations'

            Если запрос не требует инструментов (приветствие, уточнение), сразу дай вежливый и профессиональный ответ.
            Всегда формулируй ответ четко, структурированно, опираясь на факты из вызванных инструментов.
            """;

        // 2. Формируем список Function Declarations для Gemini
        var toolDeclarations = GetAvailableTools();

        var contents = new List<object>
        {
            new
            {
                role = "user",
                parts = new object[]
                {
                    new { text = messageText }
                }
            }
        };

        // 3. Шаг 1: Первое обращение к Gemini (Определение намерения и выбор MCP инструмента)
        llmCallsCount++;
        var startTurn1 = DateTime.UtcNow;
        var turn1 = await _gemini.ChatTurnAsync(
            contents: contents,
            tools: toolDeclarations,
            systemInstruction: systemInstruction,
            traceId: traceId,
            ct: ct);
        var endTurn1 = DateTime.UtcNow;

        totalTokens += turn1.TotalTokens;

        if (_langfuse != null)
        {
            await _langfuse.CreateGenerationAsync(
                traceId: traceId,
                name: "Gen:Main_Agent",
                model: _config.Llm.Models.ChatModel,
                startTime: startTurn1,
                endTime: endTurn1,
                promptTokens: turn1.PromptTokens,
                completionTokens: turn1.CandidatesTokens,
                totalTokens: turn1.TotalTokens,
                input: messageText,
                output: turn1.FunctionCall != null ? $"FunctionCall: {turn1.FunctionCall.Name}" : turn1.Text,
                metadata: new { turn = 1, department = deptStr, role = rolesStr },
                ct: ct);
        }

        if (!turn1.IsSuccess)
        {
            return new AgentResponse(
                IsSuccess: false,
                AnswerText: "Извините, произошла ошибка при обращении к нейросети.",
                ToolsInvoked: toolsInvoked,
                TotalLlmCalls: llmCallsCount,
                TotalTokensUsed: totalTokens,
                ErrorMessage: turn1.ErrorMessage);
        }

        // Если модель сразу ответила текстом без вызова инструментов:
        if (turn1.FunctionCall == null)
        {
            return new AgentResponse(
                IsSuccess: true,
                AnswerText: turn1.Text ?? "Запрос обработан.",
                ToolsInvoked: toolsInvoked,
                TotalLlmCalls: llmCallsCount,
                TotalTokensUsed: totalTokens,
                ErrorMessage: null);
        }

        // 4. Шаг 2: Модель запросила вызов инструмента (Function Calling)
        var fnCall = turn1.FunctionCall;
        toolsInvoked.Add(fnCall.Name);

        _logger?.LogInformation("🧠 [Agent] Gemini выбрала инструмент '{ToolName}' с аргументами: {Args}",
            fnCall.Name, fnCall.Arguments.ToString());

        if (_langfuse != null)
        {
            _ = _langfuse.LogEventAsync(
                traceId,
                "Agent:ToolDecision",
                "INFO",
                $"Модель автономно выбрала инструмент '{fnCall.Name}'",
                metadata: new { tool = fnCall.Name, arguments = fnCall.Arguments.ToString() },
                ct: ct);
        }

        // Выполняем выбранный MCP инструмент с замером Latency (Span)
        var startSpan = DateTime.UtcNow;
        var toolExecutionResult = await ExecuteToolAsync(fnCall.Name, fnCall.Arguments, telegramId, traceId, ct);
        var endSpan = DateTime.UtcNow;

        if (_langfuse != null)
        {
            await _langfuse.CreateSpanAsync(
                traceId: traceId,
                name: $"Span:MCP-{fnCall.Name}",
                startTime: startSpan,
                endTime: endSpan,
                input: fnCall.Arguments.ToString(),
                output: toolExecutionResult,
                metadata: new { tool = fnCall.Name, department = deptStr },
                ct: ct);
        }

        // 5. Шаг 3: Передаем результат работы MCP обратно в модель
        contents.Add(new
        {
            role = "model",
            parts = new object[]
            {
                new
                {
                    functionCall = new
                    {
                        name = fnCall.Name,
                        args = fnCall.Arguments
                    }
                }
            }
        });

        contents.Add(new
        {
            role = "function",
            parts = new object[]
            {
                new
                {
                    functionResponse = new
                    {
                        name = fnCall.Name,
                        response = new
                        {
                            output = toolExecutionResult
                        }
                    }
                }
            }
        });

        // Второе обращение к Gemini: синтез обоснованного ответа на основе данных MCP
        llmCallsCount++;
        var startTurn2 = DateTime.UtcNow;
        var turn2 = await _gemini.ChatTurnAsync(
            contents: contents,
            tools: toolDeclarations,
            systemInstruction: systemInstruction,
            traceId: traceId,
            ct: ct);
        var endTurn2 = DateTime.UtcNow;

        totalTokens += turn2.TotalTokens;

        var finalText = turn2.Text ?? JsonSerializer.Serialize(toolExecutionResult);

        if (_langfuse != null)
        {
            await _langfuse.CreateGenerationAsync(
                traceId: traceId,
                name: "Gen:Main_Agent",
                model: _config.Llm.Models.ChatModel,
                startTime: startTurn2,
                endTime: endTurn2,
                promptTokens: turn2.PromptTokens,
                completionTokens: turn2.CandidatesTokens,
                totalTokens: turn2.TotalTokens,
                input: toolExecutionResult,
                output: finalText,
                metadata: new { turn = 2, department = deptStr, role = rolesStr },
                ct: ct);
        }

        // 6. Шаг 4: Фоновая оценка адекватности и качества (LLM-as-a-Judge)
        if (_judgeService != null && _config.Telemetry.Evaluations.EnableLlmAsJudge)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _judgeService.EvaluateFaithfulnessAsync(
                        traceId: traceId,
                        userQuestion: messageText,
                        rawContextData: JsonSerializer.Serialize(toolExecutionResult),
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
            IsSuccess: turn2.IsSuccess,
            AnswerText: finalText,
            ToolsInvoked: toolsInvoked,
            TotalLlmCalls: llmCallsCount,
            TotalTokensUsed: totalTokens,
            ErrorMessage: turn2.ErrorMessage);
    }

    private async Task<object> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        long telegramId,
        string traceId,
        CancellationToken ct)
    {
        var secretKey = _config.Security.InternalServiceSecretKey;

        try
        {
            switch (toolName)
            {
                case "get_order_status":
                {
                    var orderId = arguments.TryGetProperty("order_id", out var oid) ? oid.GetString() ?? "44102" : "44102";
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", secretKey);
                    client.DefaultRequestHeaders.Add("X-Telegram-Id", telegramId.ToString());
                    client.DefaultRequestHeaders.Add("X-Trace-Id", traceId);

                    var mcpUrl = "http://mcp-1c:8082/tools/get_order_status?order_id=" + Uri.EscapeDataString(orderId);
                    var res = await client.GetAsync(mcpUrl, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    }
                    return new { status = "error", message = $"1C вернула код {res.StatusCode}" };
                }

                case "get_production_kpis":
                {
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", secretKey);
                    client.DefaultRequestHeaders.Add("X-Telegram-Id", telegramId.ToString());
                    client.DefaultRequestHeaders.Add("X-Trace-Id", traceId);

                    var mcpUrl = "http://mcp-1c:8082/tools/get_production_kpis";
                    var res = await client.GetAsync(mcpUrl, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    }
                    return new { status = "error", message = $"1C вернула код {res.StatusCode}" };
                }

                case "get_upcoming_meetings":
                {
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", secretKey);
                    client.DefaultRequestHeaders.Add("X-Telegram-Id", telegramId.ToString());
                    client.DefaultRequestHeaders.Add("X-Trace-Id", traceId);

                    var mcpUrl = "http://mcp-calendar:8084/tools/get_upcoming_meetings?time_window_minutes=60";
                    var res = await client.GetAsync(mcpUrl, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        return await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    }
                    return new { status = "error", message = "Ошибка календаря" };
                }

                case "search_typography_regulations":
                {
                    var query = arguments.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                    var chunks = await _repo.SearchTextChunksAsync(query, new List<string> { "all" }, 4, ct);
                    return chunks.Select(c => new { chunk_id = c.Id, text = c.ChunkText }).ToList();
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
                functionDeclarations = new object[]
                {
                    new
                    {
                        name = "get_order_status",
                        description = "Возвращает актуальный статус тиража/заказа из 1С:Управление нашей типографией (этап офсета, % готовности, дата отгрузки, наличие бумаги и краски).",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                order_id = new { type = "STRING", description = "Номер заказа, например '44102' или 'ЗК-44102'" }
                            },
                            required = new[] { "order_id" }
                        }
                    },
                    new
                    {
                        name = "get_production_kpis",
                        description = "Возвращает производственные показатели цехов из 1С: выработка оттисков, процент брака, загрузка офсетных машин Heidelberg, аварии и простои. Используется для управленческих отчетов директора.",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                department = new { type = "STRING", description = "Цех (например, 'Офсетный цех', 'Цех цифровой печати')" }
                            }
                        }
                    },
                    new
                    {
                        name = "get_upcoming_meetings",
                        description = "Возвращает список предстоящих совещаний, планерок, тем и участников из корпоративного календаря.",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                time_window_minutes = new { type = "INTEGER", description = "Окно поиска встреч в минутах (например 60)" }
                            }
                        }
                    },
                    new
                    {
                        name = "search_typography_regulations",
                        description = "Ищет в базе знаний технологические регламенты типографии, нормы брака оборудования Heidelberg, правила приемки макетов и техкарты.",
                        parameters = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                query = new { type = "STRING", description = "Поисковый запрос по регламентам" }
                            },
                            required = new[] { "query" }
                        }
                    }
                }
            }
        };
    }
}

