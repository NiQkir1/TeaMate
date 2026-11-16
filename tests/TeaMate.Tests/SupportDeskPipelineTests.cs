using System.Net;
using System.Text;
using System.Text.Json;
using TeaMate.AiGateway.Api.Telegram;
using TeaMate.Core.Agents;
using TeaMate.Core.Configuration;
using TeaMate.Core.Data;
using TeaMate.Core.Llm;
using TeaMate.Core.Mcp;
using TeaMate.Core.Models;
using TeaMate.Core.Resilience;
using TeaMate.Core.Security;
using TeaMate.Core.Telegram;
using TeaMate.Core.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace TeaMate.Tests;

public class SupportDeskPipelineTests
{
    private readonly ITestOutputHelper _output;

    public SupportDeskPipelineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task FullPipeline_Question_And_OperatorEscalation_RecordsTraces_And_RoutesLiveMessages()
    {
        var repo = new InMemorySupportRepository();
        var config = new TeaMateConfig();
        config.Support.OperatorTelegramId = 2135203645;
        var langfuse = new RecordingLangfuseClient();
        var tgBot = new RecordingTelegramBotClient();
        var validator = new SupportEmployeeValidator();

        var mcpHandler = new McpSupportDeskPipelineHandler(repo, config, validator, tgBot, langfuse);
        var httpFactory = new SupportPipelineHttpClientFactory(mcpHandler);
        var gemini = new EscalateOperatorFakeGeminiClient();

        var agent = new TeaMateAiAgent(
            gemini,
            repo,
            httpFactory,
            config,
            langfuse,
            null,
            NullLogger<TeaMateAiAgent>.Instance);

        // 1. Сотрудник задает вопрос и просит позвать живого оператора
        const long userTelegramId = 100002; // Кузнецов Иван Петрович (Производство)
        const string userQuestion = "Срочно позови живого оператора! По заказу 44102 проблема с цветопробой на Heidelberg, нужна помощь человека.";
        var traceId = "trace-support-escalation-001";

        var agentResponse = await agent.ProcessMessageAsync(
            telegramId: userTelegramId,
            chatId: userTelegramId,
            messageText: userQuestion,
            traceId: traceId);

        _output.WriteLine("=== 1. ОТВЕТ АГЕНТА НА ВЫЗОВ ОПЕРАТОРА ===");
        _output.WriteLine($"IsSuccess: {agentResponse.IsSuccess}");
        _output.WriteLine($"ToolsInvoked: [{string.Join(", ", agentResponse.ToolsInvoked)}]");
        _output.WriteLine($"TotalLlmCalls: {agentResponse.TotalLlmCalls}, TotalTokensUsed: {agentResponse.TotalTokensUsed}");
        _output.WriteLine($"AnswerText: {agentResponse.AnswerText}");

        Assert.True(agentResponse.IsSuccess);
        Assert.Contains("escalate_to_operator", agentResponse.ToolsInvoked);
        Assert.Equal(2, agentResponse.TotalLlmCalls);
        Assert.Equal(1, mcpHandler.RequestCount);
        Assert.Equal(HttpStatusCode.OK, mcpHandler.LastStatusCode);

        // Проверяем, что в БД создалась активная эскалация на оператора 2135203645 и ветка сессии переключилась в operator_live
        var escalation = Assert.Single(repo.Escalations);
        Assert.Equal(2135203645, escalation.OperatorTelegramId);
        Assert.Equal(userTelegramId, escalation.UserTelegramId);
        Assert.Equal("active", escalation.Status);
        Assert.Equal("high", escalation.Urgency);
        Assert.Equal(701, escalation.OperatorMessageId);
        Assert.Equal("operator_live", repo.ActiveSession.CurrentBranch);

        // Проверяем, что оператору 2135203645 ушла карточка в Telegram с кнопкой завершения
        var operatorCardMsg = Assert.Single(tgBot.SentMessages, m => m.ChatId == 2135203645);
        Assert.Contains("Вызов оператора поддержки", operatorCardMsg.Text);
        Assert.Contains("44102", operatorCardMsg.Text);
        Assert.NotNull(operatorCardMsg.Buttons);
        Assert.Contains(operatorCardMsg.Buttons!.SelectMany(r => r), b => b.CallbackData == $"support:close:{escalation.Id}");

        // 2. Проверяем записанные трейсы в Langfuse (Generations, Spans, Events)
        _output.WriteLine("\n=== 2. ЗАПИСАННЫЕ ТРЕЙСЫ LANGFUSE (ВЫЗОВ ОПЕРАТОРА) ===");
        foreach (var gen in langfuse.Generations)
        {
            _output.WriteLine($"[GENERATION] Trace={gen.TraceId} | Name={gen.Name} | Model={gen.Model} | Tokens={gen.TotalTokens} | Output={gen.Output}");
        }
        foreach (var span in langfuse.Spans)
        {
            _output.WriteLine($"[SPAN] Trace={span.TraceId} | Name={span.Name} | Input={span.Input} | Output={JsonSerializer.Serialize(span.Output)}");
        }
        foreach (var evt in langfuse.Events)
        {
            _output.WriteLine($"[EVENT] Trace={evt.TraceId} | Name={evt.Name} | Level={evt.Level} | Message={evt.Message}");
        }

        Assert.Equal(2, langfuse.Generations.Count);
        Assert.Contains(langfuse.Spans, s => s.Name == "Span:MCP-escalate_to_operator" && s.TraceId == traceId);
        Assert.Contains(langfuse.Events, e => e.Name == "Agent:ToolDecision" && e.TraceId == traceId);
        Assert.Contains(langfuse.Events, e => e.Name == "SupportDesk:Escalated" && e.TraceId == traceId);

        // 3. Тестируем двусторонний мост в TelegramWebhookProcessor:
        // 3.1 Пользователь пишет сообщение в режиме operator_live -> пересылается оператору 2135203645 БЕЗ вызова LLM
        var services = new ServiceCollection();
        services.AddSingleton<ITeaMateRepository>(repo);
        services.AddSingleton<IContextSwitchDetector>(new FakeNeverSwitchDetector());
        services.AddSingleton<ILlmJudgeService>(new FakeNullJudgeService());
        services.AddSingleton<ILlmLoadBalancer>(new LlmLoadBalancer(new LoadBalancerOptions()));
        services.AddSingleton<IRollbackManager, RollbackManager>();
        var sp = services.BuildServiceProvider();

        var processor = new TelegramWebhookProcessor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            tgBot,
            httpFactory,
            langfuse,
            new TelegramBotOptions { BotToken = "123456:PROD-TOKEN", WebhookSecret = "secret-2026" },
            new TelegramUpdateDeduplicator(TimeSpan.FromMinutes(5)),
            config,
            new FakeHostApplicationLifetime(),
            NullLogger<TelegramWebhookProcessor>.Instance);

        var userFollowUpJson = JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "update_id": 9001,
            "message": {
                "message_id": 501,
                "from": { "id": {{userTelegramId}} },
                "chat": { "id": {{userTelegramId}} },
                "text": "А можно перепечатать тираж до 18:00 сегодня?"
            }
        }
        """);

        var httpCtx1 = new DefaultHttpContext();
        httpCtx1.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret-2026";
        await processor.HandleUpdateAsync(httpCtx1, userFollowUpJson);

        // Ждем завершения фоновой обработки сообщения в TelegramWebhookProcessor
        await WaitForConditionAsync(() => tgBot.SentMessages.Count(m => m.ChatId == 2135203645) >= 2);

        var forwardedToOperator = tgBot.SentMessages.Last(m => m.ChatId == 2135203645);
        _output.WriteLine($"\n=== 3. ПЕРЕСЫЛКА СООБЩЕНИЯ ПОЛЬЗОВАТЕЛЯ ОПЕРАТОРУ (2135203645) ===\n{forwardedToOperator.Text}");
        Assert.Contains("А можно перепечатать тираж до 18:00 сегодня?", forwardedToOperator.Text);
        Assert.Contains(langfuse.Traces, t => t.Name == "telegram-support-live-forward");

        // 3.2 Оператор (2135203645) отвечает через Reply -> ответ уходит пользователю (100002)
        var operatorReplyJson = JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "update_id": 9002,
            "message": {
                "message_id": 801,
                "from": { "id": 2135203645 },
                "chat": { "id": 2135203645 },
                "reply_to_message": { "message_id": {{escalation.OperatorMessageId}} },
                "text": "Да, Иван! Уже поставили пластины на Heidelberg XL 75, тираж будет готов к 17:30."
            }
        }
        """);

        var httpCtx2 = new DefaultHttpContext();
        httpCtx2.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret-2026";
        await processor.HandleUpdateAsync(httpCtx2, operatorReplyJson);

        await WaitForConditionAsync(() => tgBot.SentMessages.Any(m => m.ChatId == userTelegramId && m.Text.Contains("Оператор поддержки:")));

        var replyToUser = tgBot.SentMessages.Last(m => m.ChatId == userTelegramId);
        _output.WriteLine($"\n=== 4. ОТВЕТ ОПЕРАТОРА ПОЛЬЗОВАТЕЛЮ ===\n{replyToUser.Text}");
        Assert.Contains("👨‍💼 Оператор поддержки: Да, Иван! Уже поставили пластины на Heidelberg XL 75, тираж будет готов к 17:30.", replyToUser.Text);
        Assert.Contains(langfuse.Traces, t => t.Name == "telegram-operator-reply");

        // 3.3 Оператор нажимает кнопку [🔴 Завершить диалог] (support:close:<escalationId>)
        var closeCallbackJson = JsonSerializer.Deserialize<JsonElement>($$"""
        {
            "update_id": 9003,
            "callback_query": {
                "id": "cb-close-1",
                "from": { "id": 2135203645 },
                "message": {
                    "message_id": 701,
                    "chat": { "id": 2135203645 }
                },
                "data": "support:close:{{escalation.Id}}"
            }
        }
        """);

        var httpCtx3 = new DefaultHttpContext();
        httpCtx3.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret-2026";
        await processor.HandleUpdateAsync(httpCtx3, closeCallbackJson);

        Assert.Equal("resolved", escalation.Status);
        Assert.Equal("general", repo.ActiveSession.CurrentBranch);
        Assert.Contains(langfuse.Events, e => e.Name == "SupportDesk:ClosedByOperator");

        _output.WriteLine("\n=== 5. ИТОГОВЫЕ ТРЕЙСЫ СЕССИИ ПОДДЕРЖКИ ===");
        foreach (var tr in langfuse.Traces)
        {
            _output.WriteLine($"[TRACE] TraceId={tr.TraceId} | Name={tr.Name} | SessionId={tr.SessionId} | Tags=[{string.Join(", ", tr.Tags ?? Array.Empty<string>())}] | Input={tr.Input} | Output={tr.Output}");
        }
    }

    [Fact]
    public async Task Mcp_SupportDesk_Blocks_Missing_InternalServiceKey_And_Unauthorized_User()
    {
        var repo = new InMemorySupportRepository();
        var config = new TeaMateConfig();
        var langfuse = new RecordingLangfuseClient();

        // 1. Проверка блокировки без X-Internal-Service-Key
        var authMiddleware = new InternalServiceAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            NullLogger<InternalServiceAuthMiddleware>.Instance);

        var ctxNoKey = new DefaultHttpContext();
        ctxNoKey.Request.Method = "POST";
        ctxNoKey.Request.Path = "/tools/escalate_to_operator";

        await authMiddleware.InvokeAsync(ctxNoKey, langfuse);
        Assert.Equal(StatusCodes.Status403Forbidden, ctxNoKey.Response.StatusCode);

        // 2. Проверка блокировки без права support:escalate
        var handlerCalled = false;
        var interceptor = new SecurityInterceptorMiddleware(
            _ => { handlerCalled = true; return Task.CompletedTask; },
            NullLogger<SecurityInterceptorMiddleware>.Instance);

        var ctxNoPerm = new DefaultHttpContext();
        ctxNoPerm.Response.Body = new MemoryStream();
        ctxNoPerm.Request.Headers["X-Telegram-Id"] = "999999";
        ctxNoPerm.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new RequirePermissionAttribute("support:escalate"),
                new RequireConfirmationAttribute(false)),
            "EscalateToOperatorEndpoint"));

        await interceptor.InvokeAsync(ctxNoPerm, new SupportEmployeeValidator(denyTelegramId: 999999), langfuse);
        Assert.False(handlerCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctxNoPerm.Response.StatusCode);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(30);
        }
        Assert.True(condition(), "Condition was not met within timeout.");
    }

    private sealed class McpSupportDeskPipelineHandler : HttpMessageHandler
    {
        private readonly InMemorySupportRepository _repo;
        private readonly TeaMateConfig _config;
        private readonly ISecurityValidator _validator;
        private readonly ITelegramBotClient _tg;
        private readonly ILangfuseClient _langfuse;

        public int RequestCount { get; private set; }
        public HttpStatusCode LastStatusCode { get; private set; }

        public McpSupportDeskPipelineHandler(
            InMemorySupportRepository repo,
            TeaMateConfig config,
            ISecurityValidator validator,
            ITelegramBotClient tg,
            ILangfuseClient langfuse)
        {
            _repo = repo;
            _config = config;
            _validator = validator;
            _tg = tg;
            _langfuse = langfuse;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ITeaMateRepository>(_repo);
            services.AddSingleton(_langfuse);
            var sp = services.BuildServiceProvider();

            var ctx = new DefaultHttpContext { RequestServices = sp };
            ctx.Response.Body = new MemoryStream();
            ctx.Request.Method = request.Method.Method;
            ctx.Request.Path = request.RequestUri?.AbsolutePath ?? "/tools/escalate_to_operator";

            foreach (var h in request.Headers)
            {
                ctx.Request.Headers[h.Key] = h.Value.ToArray();
            }

            string rawBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "{}";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
            ctx.Request.ContentType = "application/json";
            ctx.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(
                    new RequirePermissionAttribute("support:escalate"),
                    new RequireConfirmationAttribute(false)),
                "EscalateToOperatorEndpoint"));

            var authMiddleware = new InternalServiceAuthMiddleware(async c1 =>
            {
                var interceptor = new SecurityInterceptorMiddleware(async c2 =>
                {
                    var req = JsonSerializer.Deserialize<EscalateToOperatorRequest>(
                        rawBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

                    var traceId = c2.Request.Headers.TryGetValue("X-Trace-Id", out var tr) ? tr.ToString() : _langfuse.GenerateTraceId();
                    var secCtx = c2.Items["SecurityContext"] as EmployeeSecurityContext;
                    var userTgId = req.UserTelegramId ?? secCtx?.TelegramId ?? 0;
                    var userMasterId = req.UserMasterId ?? secCtx?.MasterId ?? userTgId;
                    var operatorTgId = _config.Support.OperatorTelegramId > 0 ? _config.Support.OperatorTelegramId : 2135203645;
                    var reason = req.Reason ?? "Помощь оператора";
                    var urgency = req.Urgency ?? "normal";

                    var session = await _repo.GetOrCreateActiveSessionByMasterIdAsync(userMasterId, userTgId, "telegram", cancellationToken);
                    var escId = await _repo.CreateSupportEscalationAsync(session.Id, userMasterId, userTgId, operatorTgId, reason, urgency, null, cancellationToken);
                    await _repo.UpdateSessionBranchAsync(session.Id, "operator_live", cancellationToken);

                    var buttons = new[]
                    {
                        new[] { new TelegramInlineButton("🔴 Завершить диалог", $"support:close:{escId}") }
                    };
                    var tgRes = await _tg.SendMessageAsync(
                        operatorTgId,
                        $"🚨 Вызов оператора поддержки!\n👤 Сотрудник: {secCtx?.FullName}\n📝 Причина обращения: {reason}\n🔗 Тикет: {escId}",
                        inlineKeyboard: buttons,
                        ct: cancellationToken);

                    if (tgRes.Result is { ValueKind: JsonValueKind.Object } rObj &&
                        rObj.TryGetProperty("message_id", out var midProp) &&
                        midProp.TryGetInt32(out var mid))
                    {
                        await _repo.UpdateSupportEscalationOperatorMessageIdAsync(escId, mid, cancellationToken);
                    }

                    await _langfuse.LogEventAsync(
                        traceId,
                        "SupportDesk:Escalated",
                        "INFO",
                        $"Диалог переведен на оператора {operatorTgId}. Причина: {reason} (EscalationId: {escId})",
                        metadata: new { escalation_id = escId, operator_telegram_id = operatorTgId, reason, urgency },
                        ct: cancellationToken);

                    c2.Response.StatusCode = 200;
                    c2.Response.ContentType = "application/json";
                    await c2.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        status = "escalated",
                        escalation_id = escId,
                        session_id = session.Id,
                        operator_telegram_id = operatorTgId,
                        branch = "operator_live",
                        reason,
                        urgency
                    }), cancellationToken);
                }, NullLogger<SecurityInterceptorMiddleware>.Instance);

                await interceptor.InvokeAsync(c1, _validator, _langfuse);
            }, _config, NullLogger<InternalServiceAuthMiddleware>.Instance);

            await authMiddleware.InvokeAsync(ctx, _langfuse);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(ctx.Response.Body).ReadToEndAsync(cancellationToken);
            LastStatusCode = (HttpStatusCode)ctx.Response.StatusCode;

            return new HttpResponseMessage(LastStatusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SupportPipelineHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SupportPipelineHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class SupportEmployeeValidator : ISecurityValidator
    {
        private readonly long? _denyTelegramId;
        public SupportEmployeeValidator(long? denyTelegramId = null) => _denyTelegramId = denyTelegramId;

        public Task<(bool IsAllowed, EmployeeSecurityContext? Context, string? Reason)> ValidateAccessAsync(
            long telegramId, string requiredPermission, string actionName, string? traceId = null, CancellationToken ct = default)
        {
            if (_denyTelegramId.HasValue && telegramId == _denyTelegramId.Value)
            {
                return Task.FromResult<(bool, EmployeeSecurityContext?, string?)>((false, null, "Access Denied"));
            }

            var ctx = new EmployeeSecurityContext
            {
                EmployeeId = 2,
                MasterId = 100002,
                FullName = "Кузнецов Иван Петрович",
                TelegramId = telegramId,
                Department = "Производство",
                Position = "Начальник производства",
                Roles = new() { "Production" },
                Permissions = new() { "onec:read_orders", "support:escalate" }
            };
            return Task.FromResult<(bool, EmployeeSecurityContext?, string?)>((true, ctx, null));
        }
    }

    private sealed class EscalateOperatorFakeGeminiClient : IGeminiClient
    {
        private int _turnCount;

        public Task<GeminiGenerateResponse> GenerateContentAsync(string prompt, string? systemInstruction = null, double? temperature = null, int? maxOutputTokens = null, string? traceId = null, CancellationToken ct = default)
            => Task.FromResult(new GeminiGenerateResponse(true, "ok", "STOP", 10, 5, 15, 1, null, "nvidia/nemotron-3-ultra-550b-a55b"));

        public Task<GeminiTurnResponse> ChatTurnAsync(List<object> contents, object? tools = null, string? systemInstruction = null, double? temperature = null, int? maxOutputTokens = null, string? traceId = null, CancellationToken ct = default)
        {
            _turnCount++;
            if (_turnCount == 1)
            {
                var argsJson = JsonSerializer.SerializeToElement(new
                {
                    reason = "Проблема с цветопробой по заказу 44102 на Heidelberg, требуется помощь живого специалиста",
                    urgency = "high"
                });

                return Task.FromResult(new GeminiTurnResponse(
                    IsSuccess: true,
                    Text: null,
                    FunctionCall: new GeminiFunctionCall("escalate_to_operator", argsJson, "call_escalate_001"),
                    FinishReason: "STOP",
                    PromptTokens: 420,
                    CandidatesTokens: 35,
                    TotalTokens: 455,
                    AttemptsMade: 1,
                    ErrorMessage: null,
                    Model: "nvidia/nemotron-3-ultra-550b-a55b"));
            }

            return Task.FromResult(new GeminiTurnResponse(
                IsSuccess: true,
                Text: "Я передал ваш запрос по заказу 44102 и цветопробе дежурному оператору поддержки. Все ваши следующие сообщения в этом чате будут направляться напрямую специалисту.",
                FunctionCall: null,
                FinishReason: "STOP",
                PromptTokens: 510,
                CandidatesTokens: 42,
                TotalTokens: 552,
                AttemptsMade: 1,
                ErrorMessage: null,
                Model: "nvidia/nemotron-3.5-bolt-e4b"));
        }

        public Task<GeminiTurnResponse> ChatTurnNoThinkingAsync(List<object> contents, object? tools = null, string? systemInstruction = null, double? temperature = null, int? maxOutputTokens = null, string? traceId = null, CancellationToken ct = default)
            => ChatTurnAsync(contents, tools, systemInstruction, temperature, maxOutputTokens, traceId, ct);

        public GeminiCallStats GetStats() => new(2, 0, 40, 40, 0, null, 0);
    }

    private sealed class RecordingTelegramBotClient : ITelegramBotClient
    {
        private int _nextMsgId = 700;
        public record SentMessage(long ChatId, string Text, IReadOnlyList<IReadOnlyList<TelegramInlineButton>>? Buttons, int MessageId);
        public List<SentMessage> SentMessages { get; } = new();

        public bool IsConfigured => true;

        public Task<TelegramApiResponse> SendMessageAsync(
            long chatId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramInlineButton>>? inlineKeyboard = null,
            string? parseMode = null,
            CancellationToken ct = default)
        {
            var mid = Interlocked.Increment(ref _nextMsgId);
            lock (SentMessages)
            {
                SentMessages.Add(new SentMessage(chatId, text, inlineKeyboard, mid));
            }
            var resElem = JsonSerializer.SerializeToElement(new { message_id = mid });
            return Task.FromResult(new TelegramApiResponse(true, Result: resElem));
        }

        public Task<TelegramApiResponse> SendChatActionAsync(long chatId, string action = "typing", CancellationToken ct = default)
            => Task.FromResult(new TelegramApiResponse(true));

        public Task<TelegramApiResponse> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken ct = default)
            => Task.FromResult(new TelegramApiResponse(true));

        public Task<TelegramApiResponse> SetWebhookAsync(string webhookUrl, string? secretToken = null, bool dropPendingUpdates = false, CancellationToken ct = default)
            => Task.FromResult(new TelegramApiResponse(true));

        public Task<TelegramApiResponse> GetWebhookInfoAsync(CancellationToken ct = default)
            => Task.FromResult(new TelegramApiResponse(true));
    }

    private sealed class RecordingLangfuseClient : ILangfuseClient
    {
        public record TraceRecord(string TraceId, string Name, string? UserId, string? SessionId, string[]? Tags, object? Input, object? Output);
        public record SpanRecord(string TraceId, string Name, object? Input, object? Output);
        public record GenerationRecord(string TraceId, string Name, string Model, int TotalTokens, object? Input, object? Output);
        public record EventRecord(string TraceId, string Name, string Level, string? Message, object? Metadata);

        public List<TraceRecord> Traces { get; } = new();
        public List<SpanRecord> Spans { get; } = new();
        public List<GenerationRecord> Generations { get; } = new();
        public List<EventRecord> Events { get; } = new();

        public string GenerateTraceId() => $"trace-{Guid.NewGuid():N}"[..16];

        public Task CreateTraceAsync(string traceId, string name, string? userId = null, string? sessionId = null, object? metadata = null, string[]? tags = null, CancellationToken ct = default)
            => CreateTraceAsync(traceId, name, userId, sessionId, metadata, tags, null, null, ct);

        public Task CreateTraceAsync(string traceId, string name, string? userId, string? sessionId, object? metadata, string[]? tags, object? input, object? output = null, CancellationToken ct = default)
        {
            lock (Traces)
            {
                Traces.Add(new TraceRecord(traceId, name, userId, sessionId, tags, input, output));
            }
            return Task.CompletedTask;
        }

        public Task CreateSpanAsync(string traceId, string name, DateTime startTime, DateTime endTime, object? input = null, object? output = null, object? metadata = null, string? parentObservationId = null, string? spanId = null, CancellationToken ct = default)
        {
            lock (Spans)
            {
                Spans.Add(new SpanRecord(traceId, name, input, output));
            }
            return Task.CompletedTask;
        }

        public Task CreateGenerationAsync(string traceId, string name, string model, DateTime startTime, DateTime endTime, int promptTokens, int completionTokens, int totalTokens, int cachedTokens = 0, object? input = null, object? output = null, object? metadata = null, double? calculatedCost = null, string? parentObservationId = null, string? generationId = null, CancellationToken ct = default)
        {
            lock (Generations)
            {
                Generations.Add(new GenerationRecord(traceId, name, model, totalTokens, input, output));
            }
            return Task.CompletedTask;
        }

        public Task LogEventAsync(string traceId, string name, string level = "DEFAULT", string? message = null, object? metadata = null, CancellationToken ct = default)
        {
            lock (Events)
            {
                Events.Add(new EventRecord(traceId, name, level, message, metadata));
            }
            return Task.CompletedTask;
        }

        public Task LogSecurityAlertAsync(string traceId, long telegramId, string toolOrAction, string requiredPermission, string reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogScoreAsync(string traceId, string name, double value, string? comment = null, string? observationId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class InMemorySupportRepository : ITeaMateRepository
    {
        public ChatSession ActiveSession { get; } = new()
        {
            Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            MasterId = 100002,
            EmployeeId = 2,
            MessengerType = "telegram",
            MessengerChatId = 100002,
            TelegramChatId = 100002,
            IsActive = true,
            CurrentBranch = "general"
        };

        public List<SupportEscalation> Escalations { get; } = new();
        public List<ChatMessage> Messages { get; } = new();

        public Task<EmployeeSecurityContext?> GetEmployeeSecurityContextAsync(long telegramId, CancellationToken ct = default)
            => Task.FromResult<EmployeeSecurityContext?>(new EmployeeSecurityContext
            {
                EmployeeId = 2,
                MasterId = 100002,
                TelegramId = telegramId,
                FullName = "Кузнецов Иван Петрович",
                Department = "Производство",
                Position = "Начальник производства",
                Roles = new() { "Production" },
                Permissions = new() { "onec:read_orders", "support:escalate" }
            });

        public Task<EmployeeSecurityContext?> GetEmployeeSecurityContextByMasterIdAsync(long masterId, CancellationToken ct = default)
            => GetEmployeeSecurityContextAsync(masterId, ct);

        public Task<long?> ResolveMasterIdByMessengerAsync(string messengerType, string messengerUserId, CancellationToken ct = default)
            => Task.FromResult<long?>(100002);

        public Task<ChatSession> GetOrCreateActiveSessionByMasterIdAsync(long masterId, long messengerChatId, string messengerType = "telegram", CancellationToken ct = default)
            => Task.FromResult(ActiveSession);

        public Task<ChatSession> SwitchToNewSessionAsync(Guid currentSessionId, long masterId, long messengerChatId, string? newBranch = null, string messengerType = "telegram", CancellationToken ct = default)
        {
            ActiveSession.CurrentBranch = newBranch ?? "general";
            return Task.FromResult(ActiveSession);
        }

        public Task UpdateSessionBranchAsync(Guid sessionId, string branch, CancellationToken ct = default)
        {
            ActiveSession.CurrentBranch = branch;
            return Task.CompletedTask;
        }

        public Task<Guid> CreateSupportEscalationAsync(Guid sessionId, long userMasterId, long? userTelegramId, long operatorTelegramId, string? reason, string urgency = "normal", object? metadata = null, CancellationToken ct = default)
        {
            var esc = new SupportEscalation
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                UserMasterId = userMasterId,
                UserTelegramId = userTelegramId,
                OperatorTelegramId = operatorTelegramId,
                Reason = reason,
                Urgency = urgency,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };
            Escalations.Add(esc);
            return Task.FromResult(esc.Id);
        }

        public Task<SupportEscalation?> GetActiveSupportEscalationBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Escalations.LastOrDefault(e => e.SessionId == sessionId && e.Status == "active"));

        public Task<SupportEscalation?> GetActiveSupportEscalationByOperatorReplyAsync(long operatorTgId, int operatorMessageId, CancellationToken ct = default)
            => Task.FromResult(Escalations.LastOrDefault(e => e.OperatorTelegramId == operatorTgId && e.Status == "active"));

        public Task UpdateSupportEscalationOperatorMessageIdAsync(Guid escalationId, int operatorMessageId, CancellationToken ct = default)
        {
            var esc = Escalations.FirstOrDefault(e => e.Id == escalationId);
            if (esc != null) esc.OperatorMessageId = operatorMessageId;
            return Task.CompletedTask;
        }

        public Task CloseSupportEscalationAsync(Guid escalationId, string status = "resolved", CancellationToken ct = default)
        {
            var esc = Escalations.FirstOrDefault(e => e.Id == escalationId);
            if (esc != null)
            {
                esc.Status = status;
                esc.ClosedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<SupportEscalation?> GetSupportEscalationByIdAsync(Guid escalationId, CancellationToken ct = default)
            => Task.FromResult(Escalations.FirstOrDefault(e => e.Id == escalationId));

        public Task<List<ChatMessage>> GetMessagesBySessionIdAsync(Guid sessionId, int limit = 20, CancellationToken ct = default)
            => Task.FromResult(Messages.ToList());

        public Task SaveMessageToSessionAsync(Guid sessionId, long masterId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default)
        {
            Messages.Add(new ChatMessage { Id = Guid.NewGuid(), SessionId = sessionId, MasterId = masterId, Role = role, Content = content });
            return Task.CompletedTask;
        }

        public Task<Employee?> GetEmployeeByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<Employee?>(null);
        public Task<List<Employee>> GetAllEmployeesAsync(CancellationToken ct = default) => Task.FromResult(new List<Employee>());
        public Task<ChatSession> GetOrCreateSessionAsync(long telegramChatId, long? telegramUserId = null, CancellationToken ct = default) => Task.FromResult(ActiveSession);
        public Task SaveMessageAsync(Guid sessionId, string contextId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int limit = 20, CancellationToken ct = default) => Task.FromResult(Messages.ToList());
        public Task FreezeContextAsync(Guid sessionId, string contextId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid> SaveDocumentAsync(string title, string sourceType, string? filePath, string? department, List<string> allowedAccessGroups, object? metadata = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task SaveDocumentChunksAsync(Guid documentId, List<string> chunkTexts, List<float[]?> embeddings, List<string> allowedAccessGroups, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<CorporateDocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default) => Task.FromResult(new List<CorporateDocumentChunk>());
        public Task<List<CorporateDocumentChunk>> SearchTextChunksAsync(string queryText, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default) => Task.FromResult(new List<CorporateDocumentChunk>());
        public Task<List<Meeting>> GetUpcomingMeetingsAsync(int timeWindowMinutes = 30, CancellationToken ct = default) => Task.FromResult(new List<Meeting>());
        public Task<Meeting?> GetMeetingByIdAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult<Meeting?>(null);
        public Task<List<MeetingNote>> GetMeetingNotesAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult(new List<MeetingNote>());
        public Task SaveMeetingNoteAsync(Guid meetingId, int? authorId, string noteText, string category = "general", CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid> SaveMeetingProtocolAsync(Guid meetingId, string title, string summary, List<string> decisions, string? rawTranscript = null, string? docxPath = null, string? pdfPath = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<MeetingProtocol?> GetMeetingProtocolAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult<MeetingProtocol?>(null);
        public Task<Guid> CreateActionItemAsync(Guid meetingId, Guid? protocolId, string title, string? description, int? responsibleId, DateTime? deadline, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<List<ActionItem>> GetActionItemsByMeetingAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult(new List<ActionItem>());
        public Task<List<ActionItem>> GetActionItemsByResponsibleAsync(int employeeId, string? statusFilter = null, CancellationToken ct = default) => Task.FromResult(new List<ActionItem>());
        public Task UpdateActionItemStatusAsync(Guid actionItemId, string status, int? bitrix24TaskId = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeNeverSwitchDetector : IContextSwitchDetector
    {
        public Task<ContextSwitchResult> DetectSwitchAsync(IReadOnlyList<ChatMessage> recentMessages, string newMessage, string? traceId = null, CancellationToken ct = default)
            => Task.FromResult(new ContextSwitchResult(false, "general", "No switch"));
    }

    private sealed class FakeNullJudgeService : ILlmJudgeService
    {
        public Task<double> EvaluateFaithfulnessAsync(string traceId, string userQuestion, string rawContextData, string agentAnswer, CancellationToken ct = default) => Task.FromResult(1.0);
        public Task<double> EvaluateContextSwitchAccuracyAsync(string traceId, string historyText, string incomingMessage, bool detectedSwitch, string? suggestedBranch = null, CancellationToken ct = default) => Task.FromResult(1.0);
        public Task<double> EvaluateFormattingAndPolicyAsync(string traceId, string outputText, bool isProtocol = false, CancellationToken ct = default) => Task.FromResult(1.0);
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
