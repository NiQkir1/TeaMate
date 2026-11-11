using System.Text;
using System.Text.Json;
using Fotoprint.Core.Agents;
using Fotoprint.Core.Audio;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Data;
using Fotoprint.Core.Documents;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Models;
using Fotoprint.Core.Protocols;
using Fotoprint.Core.Resilience;
using Fotoprint.Core.Security;
using Fotoprint.Core.Telemetry;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Подгрузка переменных окружения из .env (если файл присутствует в текущей или родительской папке)
try
{
    var dir = Directory.GetCurrentDirectory();
    for (int i = 0; i < 7; i++)
    {
        var envCandidate = Path.Combine(dir, ".env");
        if (File.Exists(envCandidate))
        {
            foreach (var rawLine in File.ReadAllLines(envCandidate))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                var eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = line[..eqIdx].Trim();
                var val = line[(eqIdx + 1)..].Trim().Trim('"', '\'');
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                {
                    Environment.SetEnvironmentVariable(key, val);
                }
            }
            break;
        }
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
}
catch { /* Игнорируем ошибки чтения .env */ }

// Загрузка единого конфигурационного манифеста config.yaml
var appConfig = FotoprintConfigLoader.Load();
builder.Services.AddSingleton(appConfig);

// Configuration
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5434;Database=fotoprint;Username=fotoprint;Password=fotoprint_secret";
var n8nWebhookBaseUrl = builder.Configuration["N8n:WebhookBaseUrl"] ?? "http://localhost:5678/webhook";
var langfuseBaseUrl = builder.Configuration["Langfuse:BaseUrl"] ?? "http://localhost:3000";
var langfusePubKey = builder.Configuration["Langfuse:PublicKey"] ?? "pk-lf-fotoprint-local";
var langfuseSecKey = builder.Configuration["Langfuse:SecretKey"] ?? "sk-lf-fotoprint-local";
var bitrixMcpUrl = builder.Configuration["McpUrls:Bitrix24"] ?? "http://localhost:8083";
var employeeReaderUrl = builder.Configuration["McpUrls:EmployeeReader"] ?? "http://localhost:8081";
var calendarMcpUrl = builder.Configuration["McpUrls:Calendar"] ?? appConfig.McpUrls.Calendar ?? "http://localhost:8084";

// Core Services
builder.Services.AddSingleton<IFotoprintRepository>(_ => new FotoprintRepository(connectionString));
builder.Services.AddSingleton<IDocumentParser, DocumentParser>();
builder.Services.AddSingleton<IMeetingProtocolGenerator, MeetingProtocolGenerator>();

// Resilience & Load Balancing (Параметры сглаживания и лимитов берутся из config.yaml, 15 RPM для Gemini)
var lbOptions = new LoadBalancerOptions
{
    MaxConcurrentRequests = appConfig.Llm.RateLimits.MaxConcurrentRequests,
    MaxRequestsPerMinute = appConfig.Llm.RateLimits.Rpm,
    MinIntervalBetweenRequestsMs = appConfig.LoadBalancer.MinIntervalBetweenRequestsMs,
    QueueCapacity = appConfig.LoadBalancer.QueueCapacity
};
builder.Services.AddSingleton<ILlmLoadBalancer>(_ => new LlmLoadBalancer(lbOptions));
builder.Services.AddSingleton<IRollbackManager, RollbackManager>();

// NVIDIA NIM LLM Client с контролем нагрузки, повторами, защитой от обрывов пула сокетов и трейсингом
var llmTimeout = TimeSpan.FromSeconds(Math.Max(90, appConfig.Llm.Parameters.TimeoutSeconds));
static SocketsHttpHandler CreateLlmSocketsHandler() => new()
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(25),
    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
    KeepAlivePingDelay = TimeSpan.FromSeconds(15),
    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
    ConnectTimeout = TimeSpan.FromSeconds(15)
};

builder.Services.AddHttpClient<ILlmClient, NvidiaClient>(c => c.Timeout = llmTimeout)
    .ConfigurePrimaryHttpMessageHandler(CreateLlmSocketsHandler);
builder.Services.AddHttpClient<INvidiaClient, NvidiaClient>(c => c.Timeout = llmTimeout)
    .ConfigurePrimaryHttpMessageHandler(CreateLlmSocketsHandler);
builder.Services.AddHttpClient<IGeminiClient, NvidiaClient>(c => c.Timeout = llmTimeout)
    .ConfigurePrimaryHttpMessageHandler(CreateLlmSocketsHandler);
builder.Services.AddHttpClient<NvidiaClient>(c => c.Timeout = llmTimeout)
    .ConfigurePrimaryHttpMessageHandler(CreateLlmSocketsHandler);

// 2-й этап: Детекция смены контекста через LLM перед передачей в n8n
builder.Services.AddSingleton<IContextSwitchDetector, ContextSwitchDetector>();

// Langfuse & FinOps Telemetry
builder.Services.AddHttpClient<ILangfuseClient, LangfuseClient>();
builder.Services.AddSingleton(new LangfuseOptions
{
    BaseUrl = langfuseBaseUrl,
    PublicKey = langfusePubKey,
    SecretKey = langfuseSecKey,
    Enabled = true,
    PromptTokenCostPer1k = appConfig.Telemetry.FinOps.PromptTokenCostPer1k,
    CompletionTokenCostPer1k = appConfig.Telemetry.FinOps.CompletionTokenCostPer1k,
    CachedTokenCostPer1k = appConfig.Telemetry.FinOps.CachedTokenCostPer1k
});

// Автоматический оценщик качества LLM-as-a-Judge (Faithfulness, Context Switch, Formatting)
builder.Services.AddSingleton<ILlmJudgeService, LlmJudgeService>();

// Автономный AI-агент с Tool Calling (автоматический выбор нужного MCP)
builder.Services.AddSingleton<IFotoprintAgent, FotoprintAiAgent>();

// Аудиотранскрибация (C++ Metal/Vulkan GPU + Gemini Fallback) и MoM-анализатор протоколов совещаний
builder.Services.AddSingleton<IVocabularyProvider, YamlVocabularyProvider>();
builder.Services.AddSingleton<IVocabularyPostProcessor, VocabularyPostProcessor>();
builder.Services.AddSingleton<IAudioEngineResolver, AudioEngineResolver>();
builder.Services.AddSingleton<LocalEngineAudioTranscriber>();
builder.Services.AddHttpClient<CloudGeminiAudioTranscriber>();
builder.Services.AddSingleton<IAudioTranscriber>(sp =>
    new CompositeAudioTranscriber(
        sp.GetRequiredService<LocalEngineAudioTranscriber>(),
        sp.GetRequiredService<CloudGeminiAudioTranscriber>(),
        sp.GetRequiredService<FotoprintConfig>(),
        sp.GetService<ILangfuseClient>(),
        sp.GetRequiredService<ILogger<CompositeAudioTranscriber>>()));
builder.Services.AddSingleton<IMeetingTranscriptAnalyzer, MeetingTranscriptAnalyzer>();

builder.Services.AddHttpClient("N8nClient", client =>
{
    client.BaseAddress = new Uri(n8nWebhookBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(180);
});

builder.Services.AddHttpClient("BitrixMcpClient", client =>
{
    client.BaseAddress = new Uri(bitrixMcpUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", appConfig.Security.InternalServiceSecretKey);
});

builder.Services.AddHttpClient("EmployeeReaderClient", client =>
{
    client.BaseAddress = new Uri(employeeReaderUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", appConfig.Security.InternalServiceSecretKey);
});

builder.Services.AddHttpClient("CalendarMcpClient", client =>
{
    client.BaseAddress = new Uri(calendarMcpUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", appConfig.Security.InternalServiceSecretKey);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

Fotoprint.AiGateway.Api.Telegram.TelegramEndpoints.AddTelegramIntegration(builder.Services, builder.Configuration);

var app = builder.Build();

// Защита периметра от сканеров портов, ботов и поиска уязвимых файлов
app.UseMiddleware<PerimeterDefenseMiddleware>();

// Health
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Fotoprint.AiGateway.Api",
    version = "1.0.0",
    timestamp = DateTime.UtcNow
}));

// Статистика балансировщика нагрузки, счетчика вызовов Gemini (15 RPM) и роллбэка
app.MapGet("/api/system/stats", (
    ILlmLoadBalancer loadBalancer,
    IRollbackManager rollbackManager,
    IGeminiClient gemini,
    FotoprintConfig config) =>
{
    return Results.Ok(new
    {
        service = "Fotoprint.AiGateway.Api",
        llm_config = new
        {
            provider = config.Llm.Provider,
            chat_model = config.Llm.Models.ChatModel,
            fast_model = config.Llm.Models.FastModel,
            temperature = config.Llm.Parameters.Temperature,
            max_tokens = config.Llm.Parameters.MaxTokens,
            rpm_limit = config.Llm.RateLimits.Rpm,
            tpm_limit = config.Llm.RateLimits.Tpm,
            proxy_enabled = config.Llm.Proxy.Enabled
        },
        gemini_rpm_tracker = gemini.GetStats(),
        load_balancer = loadBalancer.GetStats(),
        rollback_system = rollbackManager.GetStats(),
        timestamp = DateTime.UtcNow
    });
});

// Прямой вызов NVIDIA LLM с контролем нагрузки и повторами
app.MapPost("/api/llm/generate", async (
    [FromBody] GeminiPromptRequest req,
    INvidiaClient llm,
    ILangfuseClient langfuse,
    FotoprintConfig config) =>
{
    var traceId = langfuse.GenerateTraceId();
    await langfuse.CreateTraceAsync(
        traceId: traceId,
        name: "api-llm-generate",
        metadata: new { prompt_length = req.Prompt?.Length ?? 0, model = config.Llm.Models.ChatModel },
        tags: new[] { "channel:api", $"provider:{config.Llm.Provider}" });

    var response = await llm.GenerateContentAsync(
        prompt: req.Prompt ?? string.Empty,
        systemInstruction: req.SystemInstruction,
        temperature: req.Temperature,
        maxOutputTokens: req.MaxOutputTokens,
        traceId: traceId);

    return Results.Ok(new
    {
        response,
        llm_call_stats = llm.GetStats(),
        trace_id = traceId
    });
});

// Прямой вызов Gemini / NVIDIA (обратная совместимость)
app.MapPost("/api/gemini/generate", async (
    [FromBody] GeminiPromptRequest req,
    IGeminiClient gemini,
    ILangfuseClient langfuse,
    FotoprintConfig config) =>
{
    var traceId = langfuse.GenerateTraceId();
    await langfuse.CreateTraceAsync(
        traceId: traceId,
        name: "api-gemini-generate",
        metadata: new { prompt_length = req.Prompt?.Length ?? 0, model = config.Llm.Models.ChatModel },
        tags: new[] { "channel:api", $"provider:{config.Llm.Provider}" });

    var response = await gemini.GenerateContentAsync(
        prompt: req.Prompt ?? string.Empty,
        systemInstruction: req.SystemInstruction,
        temperature: req.Temperature,
        maxOutputTokens: req.MaxOutputTokens,
        traceId: traceId);

    return Results.Ok(new
    {
        response,
        gemini_call_stats = gemini.GetStats(),
        trace_id = traceId
    });
});

// Список доступных пользователей и их RBAC-ролей для переключателя в Web UI
app.MapGet("/api/agent/users", async (IFotoprintRepository repo, CancellationToken ct) =>
{
    var employees = await repo.GetAllEmployeesAsync(ct);
    var users = new List<object>();

    foreach (var emp in employees.Where(e => e.TelegramId.HasValue))
    {
        var secCtx = await repo.GetEmployeeSecurityContextAsync(emp.TelegramId!.Value, ct);
        users.Add(new
        {
            employee_id = emp.Id,
            telegram_id = emp.TelegramId.Value,
            full_name = secCtx?.FullName ?? $"{emp.LastName} {emp.FirstName}".Trim(),
            department = emp.Department,
            position = emp.Position,
            roles = secCtx?.Roles ?? new List<string>(),
            permissions = secCtx?.Permissions ?? new List<string>()
        });
    }

    return Results.Ok(new { users });
});

// Внутренний вызов AI-агента из оркестратора n8n (после проверки прав и роутинга в n8n)
app.MapPost("/api/agent/execute", async (
    [FromBody] AgentChatRequest req,
    IFotoprintAgent agent,
    ILangfuseClient langfuse,
    CancellationToken ct) =>
{
    var traceId = !string.IsNullOrWhiteSpace(req.TraceId) ? req.TraceId : langfuse.GenerateTraceId();
    var response = await agent.ProcessMessageAsync(
        telegramId: req.TelegramId,
        chatId: req.ChatId,
        messageText: req.Message,
        traceId: traceId,
        ct: ct);

    return Results.Ok(new
    {
        answer = response.AnswerText,
        reply = response.AnswerText,
        action_card = response.ActionCard,
        tools_invoked = response.ToolsInvoked,
        total_llm_calls = response.TotalLlmCalls,
        total_tokens = response.TotalTokensUsed,
        is_success = response.IsSuccess,
        trace_id = traceId
    });
});

// Диалог из Web UI: трейс + контекст -> оркестратор n8n (fotoprint-chat) -> AI-агент -> ответ
app.MapPost("/api/agent/chat", async (
    [FromBody] AgentChatRequest req,
    IFotoprintRepository repo,
    IHttpClientFactory httpFactory,
    IContextSwitchDetector switchDetector,
    ILlmLoadBalancer loadBalancer,
    IRollbackManager rollbackManager,
    ILangfuseClient langfuse,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var traceId = langfuse.GenerateTraceId();
    var masterId = await repo.ResolveMasterIdByMessengerAsync("telegram", req.TelegramId.ToString(), ct);

    string effectiveSessionId = $"web-chat-{req.ChatId}";
    bool isContextSwitched = false;
    string branch = "general_chat";
    string switchReason = "Новый веб-диалог";

    if (masterId.HasValue)
    {
        var activeSession = await repo.GetOrCreateActiveSessionByMasterIdAsync(masterId.Value, req.ChatId, "web", ct);
        var recentMsgs = await repo.GetMessagesBySessionIdAsync(activeSession.Id, limit: config.ContextSwitch.MaxHistoryMessages, ct: ct);
        var switchResult = await switchDetector.DetectSwitchAsync(recentMsgs, req.Message, traceId, ct);

        isContextSwitched = switchResult.IsSwitched;
        branch = switchResult.SuggestedBranch;
        switchReason = switchResult.Reason;

        if (switchResult.IsSwitched)
        {
            activeSession = await repo.SwitchToNewSessionAsync(activeSession.Id, masterId.Value, req.ChatId, branch, "web", ct);
        }

        effectiveSessionId = activeSession.Id.ToString();

        await repo.SaveMessageToSessionAsync(
            sessionId: activeSession.Id,
            masterId: masterId.Value,
            role: "user",
            content: req.Message,
            isFrozen: false,
            metadata: new { trace_id = traceId, branch, is_context_switched = isContextSwitched },
            ct: ct);
    }

    var requestStartTime = DateTime.UtcNow;
    await langfuse.CreateTraceAsync(
        traceId: traceId,
        name: "web-agent-chat",
        userId: req.TelegramId.ToString(),
        sessionId: effectiveSessionId,
        metadata: new { telegram_id = req.TelegramId, chat_id = req.ChatId, branch, is_context_switched = isContextSwitched, message = req.Message },
        tags: new[] { "channel:web-chat", "orchestrator:n8n", $"branch:{branch}", $"provider:{config.Llm.Provider}" },
        input: req.Message,
        ct: ct);

    var n8nPayload = new
    {
        trace_id = traceId,
        master_id = masterId,
        session_id = effectiveSessionId,
        is_context_switched = isContextSwitched,
        branch,
        switch_reason = switchReason,
        chat_id = req.ChatId,
        user_id = req.TelegramId,
        channel = "web-chat",
        text = req.Message
    };

    var n8nStart = DateTime.UtcNow;
    var n8nCallResult = await loadBalancer.ScheduleAsync(
        taskName: "WebChatToN8n",
        work: async lbToken =>
        {
            return await rollbackManager.ExecuteWithRollbackAsync<(bool Forbidden, JsonElement Body)>(
                nodeName: "N8n:WebChat",
                action: async (attempt, retryToken) =>
                {
                    var n8nClient = httpFactory.CreateClient("N8nClient");
                    n8nClient.DefaultRequestHeaders.Remove("X-Trace-Id");
                    n8nClient.DefaultRequestHeaders.Add("X-Trace-Id", traceId);
                    n8nClient.DefaultRequestHeaders.Remove("X-Telegram-Id");
                    n8nClient.DefaultRequestHeaders.Add("X-Telegram-Id", req.TelegramId.ToString());
                    n8nClient.DefaultRequestHeaders.Remove("X-Internal-Service-Key");
                    n8nClient.DefaultRequestHeaders.Add("X-Internal-Service-Key", config.Security.InternalServiceSecretKey);

                    var resp = await n8nClient.PostAsJsonAsync("fotoprint-chat", n8nPayload, retryToken);
                    if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        var errJson = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: retryToken);
                        return (true, errJson);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new NodeTransientException($"n8n вернул код {(int)resp.StatusCode}: {resp.ReasonPhrase}", (int)resp.StatusCode);
                    }

                    var okJson = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: retryToken);
                    return (false, okJson);
                },
                compensatingRollbackAction: (ex, token) => Task.CompletedTask,
                traceId: traceId,
                ct: lbToken);
        },
        traceId: traceId,
        ct: ct);
    var n8nEnd = DateTime.UtcNow;

    if (!n8nCallResult.IsSuccess)
    {
        var errAnswer = $"⚠️ Ошибка оркестратора n8n: {n8nCallResult.ErrorMessage}";
        await langfuse.CreateSpanAsync(traceId, "Span:N8n_Orchestrator", n8nStart, n8nEnd, input: n8nPayload, output: new { error = n8nCallResult.ErrorMessage }, ct: ct);
        await langfuse.CreateTraceAsync(
            traceId: traceId,
            name: "web-agent-chat",
            userId: req.TelegramId.ToString(),
            sessionId: effectiveSessionId,
            metadata: new { telegram_id = req.TelegramId, chat_id = req.ChatId, branch, is_context_switched = isContextSwitched, is_success = false },
            tags: new[] { "channel:web-chat", "orchestrator:n8n", $"branch:{branch}", "status:error" },
            input: req.Message,
            output: errAnswer,
            ct: ct);
        await langfuse.LogScoreAsync(traceId, "agent_success", 0.0, n8nCallResult.ErrorMessage, ct: ct);

        return Results.Ok(new
        {
            answer = errAnswer,
            tools_invoked = Array.Empty<string>(),
            total_llm_calls = 0,
            total_tokens = 0,
            is_success = false,
            trace_id = traceId,
            orchestrator = "n8n",
            branch
        });
    }

    var (forbidden, body) = n8nCallResult.Value;
    await langfuse.CreateSpanAsync(traceId, "Span:N8n_Orchestrator", n8nStart, n8nEnd, input: n8nPayload, output: body, metadata: new { forbidden, branch }, ct: ct);

    if (forbidden)
    {
        var denyMsg = body.TryGetProperty("message", out var msgProp)
            ? msgProp.GetString()
            : "🚫 Доступ запрещен оркестратором n8n.";

        await langfuse.CreateTraceAsync(
            traceId: traceId,
            name: "web-agent-chat",
            userId: req.TelegramId.ToString(),
            sessionId: effectiveSessionId,
            metadata: new { telegram_id = req.TelegramId, chat_id = req.ChatId, branch = "auth_rejected", is_success = false },
            tags: new[] { "channel:web-chat", "orchestrator:n8n", "branch:auth_rejected", "status:forbidden" },
            input: req.Message,
            output: denyMsg,
            ct: ct);
        await langfuse.LogScoreAsync(traceId, "agent_success", 0.0, "RBAC 403 Forbidden", ct: ct);

        return Results.Ok(new
        {
            answer = denyMsg,
            tools_invoked = Array.Empty<string>(),
            total_llm_calls = 0,
            total_tokens = 0,
            is_success = false,
            trace_id = traceId,
            orchestrator = "n8n",
            branch = "auth_rejected"
        });
    }

    var answerText = body.TryGetProperty("answer", out var ansProp) ? (ansProp.GetString() ?? "") : "";
    var isAgentSuccess = !body.TryGetProperty("is_success", out var succProp) || succProp.ValueKind != JsonValueKind.False;
    var toolsCount = body.TryGetProperty("tools_invoked", out var toolsProp) && toolsProp.ValueKind == JsonValueKind.Array
        ? toolsProp.GetArrayLength()
        : 0;
    var totalTokensUsed = body.TryGetProperty("total_tokens", out var tokProp) && tokProp.TryGetInt32(out var tv) ? tv : 0;
    var totalLatencySec = Math.Round((DateTime.UtcNow - requestStartTime).TotalSeconds, 2);

    if (masterId.HasValue && Guid.TryParse(effectiveSessionId, out var sid) && !string.IsNullOrEmpty(answerText))
    {
        await repo.SaveMessageToSessionAsync(
            sessionId: sid,
            masterId: masterId.Value,
            role: "assistant",
            content: answerText,
            isFrozen: false,
            metadata: new { trace_id = traceId, orchestrator = "n8n", branch },
            ct: ct);
    }

    // Обновляем корневой Trace (Input/Output + итоговые метрики) и записываем операционные Scores для дашборда Langfuse
    await langfuse.CreateTraceAsync(
        traceId: traceId,
        name: "web-agent-chat",
        userId: req.TelegramId.ToString(),
        sessionId: effectiveSessionId,
        metadata: new
        {
            telegram_id = req.TelegramId,
            chat_id = req.ChatId,
            branch,
            is_context_switched = isContextSwitched,
            tools_invoked_count = toolsCount,
            total_tokens = totalTokensUsed,
            latency_sec = totalLatencySec,
            is_success = isAgentSuccess
        },
        tags: new[] { "channel:web-chat", "orchestrator:n8n", $"branch:{branch}", $"provider:{config.Llm.Provider}", isAgentSuccess ? "status:ok" : "status:error" },
        input: req.Message,
        output: answerText,
        ct: ct);

    _ = Task.Run(async () =>
    {
        try
        {
            await langfuse.LogScoreAsync(traceId, "agent_success", isAgentSuccess ? 1.0 : 0.0, $"Branch: {branch}");
            await langfuse.LogScoreAsync(traceId, "tools_invoked_count", toolsCount, $"MCP tools used: {toolsCount}");
            await langfuse.LogScoreAsync(traceId, "context_switched", isContextSwitched ? 1.0 : 0.0, switchReason);
            await langfuse.LogScoreAsync(traceId, "response_latency_sec", totalLatencySec, $"End-to-end latency: {totalLatencySec}s");
        }
        catch { }
    });

    return Results.Ok(body);
});

// ============================================================================
// 0. АУДИОТРАНСКРИБАЦИЯ (SPEECH-TO-TEXT: TSUKIKO METAL GPU + GEMINI FALLBACK)
// ============================================================================
app.MapPost("/api/audio/transcribe", async (
    IFormFile file,
    [FromQuery] string? language,
    IAudioTranscriber transcriber,
    ILangfuseClient langfuse,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var traceId = langfuse.GenerateTraceId();
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new
        {
            success = false,
            text = string.Empty,
            segments = new List<AudioSegment>(),
            duration_ms = 0L,
            provider = "none",
            error = "Аудиофайл пуст или отсутствует."
        });
    }

    var maxBytes = Math.Max(1, config.Audio.MaxFileSizeMb) * 1024L * 1024L;
    if (file.Length > maxBytes)
    {
        return Results.BadRequest(new
        {
            success = false,
            text = string.Empty,
            segments = new List<AudioSegment>(),
            duration_ms = 0L,
            provider = "none",
            error = $"Размер файла превышает лимит {config.Audio.MaxFileSizeMb} МБ."
        });
    }

    var startSpan = DateTime.UtcNow;
    await using var stream = file.OpenReadStream();
    var result = await transcriber.TranscribeAsync(
        stream,
        string.IsNullOrWhiteSpace(file.FileName) ? "audio.wav" : file.FileName,
        string.IsNullOrWhiteSpace(language) ? "ru" : language,
        ct);
    var endSpan = DateTime.UtcNow;

    try
    {
        await langfuse.CreateSpanAsync(
            traceId: traceId,
            name: "Span:Audio_Transcribe",
            startTime: startSpan,
            endTime: endSpan,
            input: new { file_name = file.FileName, size_bytes = file.Length, language = language ?? "ru" },
            output: new { success = result.Success, provider = result.Provider, text_length = result.Text?.Length ?? 0 },
            metadata: new { duration_ms = result.DurationMs, error = result.ErrorMessage },
            ct: ct);
    }
    catch { }

    return Results.Ok(new
    {
        success = result.Success,
        text = result.Text,
        segments = result.Segments ?? new List<AudioSegment>(),
        duration_ms = result.DurationMs,
        provider = result.Provider,
        error = result.ErrorMessage
    });
}).DisableAntiforgery();

// Полный сквозной пайплайн аудиопротоколирования встречи (MoM):
// Аудиофайл -> Tsukiko/Gemini STT -> Структурированный разбор (Решения, Обсуждали, Риски, Поручения) -> БД -> .DOCX -> Кнопки Битрикс24
app.MapPost("/api/meetings/process-audio", async (
    IFormFile file,
    [FromForm] string? title,
    [FromForm] int? organizer_id,
    [FromForm] long? chat_id,
    [FromQuery] string? language,
    IAudioTranscriber transcriber,
    IMeetingTranscriptAnalyzer analyzer,
    IFotoprintRepository repo,
    IMeetingProtocolGenerator protocolGenerator,
    ILangfuseClient langfuse,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var traceId = langfuse.GenerateTraceId();
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new
        {
            success = false,
            trace_id = traceId,
            error = "Аудиофайл пуст или отсутствует."
        });
    }

    var maxBytes = Math.Max(1, config.Audio.MaxFileSizeMb) * 1024L * 1024L;
    if (file.Length > maxBytes)
    {
        return Results.BadRequest(new
        {
            success = false,
            trace_id = traceId,
            error = $"Размер файла превышает лимит {config.Audio.MaxFileSizeMb} МБ."
        });
    }

    // Шаг 1: Транскрибация аудиозаписи встречи (Tsukiko Metal GPU -> Gemini Fallback)
    await using var audioStream = file.OpenReadStream();
    var transcription = await transcriber.TranscribeAsync(
        audioStream,
        string.IsNullOrWhiteSpace(file.FileName) ? "meeting.m4a" : file.FileName,
        string.IsNullOrWhiteSpace(language) ? "ru" : language,
        ct);

    if (!transcription.Success || string.IsNullOrWhiteSpace(transcription.Text))
    {
        return Results.BadRequest(new
        {
            success = false,
            trace_id = traceId,
            provider = transcription.Provider,
            error = transcription.ErrorMessage ?? "Не удалось распознать речь из аудиофайла."
        });
    }

    // Шаг 2: Интеллектуальный анализ стенограммы (MoM) со строгим правилом «Без додумывания» ("требует уточнения")
    var analysis = await analyzer.AnalyzeTranscriptAsync(transcription.Text, title, traceId, ct);
    var effectiveTitle = !string.IsNullOrWhiteSpace(title)
        ? title!
        : (!string.IsNullOrWhiteSpace(analysis.Title)
            ? analysis.Title
            : $"Рабочее совещание от {DateTime.UtcNow:dd.MM.yyyy HH:mm}");

    // Пытаемся получить справочник сотрудников из БД (при автономном запуске без БД продолжаем без сбоя)
    var employees = new List<Employee>();
    try
    {
        employees = await repo.GetAllEmployeesAsync(ct);
    }
    catch { /* БД недоступна — используем автономный режим */ }

    var empDict = employees.ToDictionary(e => e.Id, e => e.FullName);
    var organizerName = organizer_id.HasValue && empDict.TryGetValue(organizer_id.Value, out var orgName)
        ? orgName
        : "Руководитель совещания";

    var normalizedItems = (analysis.ActionItems ?? new List<ExtractedActionItem>())
        .Select(MeetingTranscriptAnalyzer.NormalizeActionItem)
        .ToList();

    var participants = normalizedItems
        .Select(a => a.Responsible)
        .Where(r => !string.IsNullOrWhiteSpace(r) && !string.Equals(r, "требует уточнения", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (participants.Count == 0)
    {
        participants.Add("Участники рабочей группы");
    }

    // Шаг 3: Сохранение встречи в PostgreSQL (с безопасным fallback на случай отсутствия БД)
    var meetingDate = DateTime.UtcNow;
    Guid meetingId;
    try
    {
        meetingId = await repo.CreateMeetingAsync(
            title: effectiveTitle,
            description: analysis.Summary,
            startTime: meetingDate,
            endTime: meetingDate,
            status: "completed",
            organizerId: organizer_id,
            participantIds: organizer_id.HasValue ? new List<int> { organizer_id.Value } : new List<int>(),
            ct: ct);
    }
    catch
    {
        meetingId = Guid.NewGuid();
    }

    // Шаг 4: Генерация официального корпоративного документа .DOCX и сохранение во временное хранилище
    var docxSummaryBuilder = new StringBuilder();
    docxSummaryBuilder.AppendLine(analysis.Summary);
    if (analysis.Discussed.Count > 0)
    {
        docxSummaryBuilder.AppendLine();
        docxSummaryBuilder.AppendLine("Обсуждали: " + string.Join("; ", analysis.Discussed));
    }
    if (analysis.Risks.Count > 0)
    {
        docxSummaryBuilder.AppendLine();
        docxSummaryBuilder.AppendLine("Риски и блокеры: " + string.Join("; ", analysis.Risks));
    }
    if (analysis.RequiresClarification.Count > 0)
    {
        docxSummaryBuilder.AppendLine();
        docxSummaryBuilder.AppendLine("Требует уточнения: " + string.Join("; ", analysis.RequiresClarification));
    }

    var exportActionItems = normalizedItems.Select(item => new ActionItemExportData(
        Title: item.Title,
        ResponsibleName: string.IsNullOrWhiteSpace(item.Responsible) ? "требует уточнения" : item.Responsible,
        Deadline: item.ParsedDeadline,
        Status: "pending"
    )).ToList();

    var exportData = new ProtocolExportData(
        Title: effectiveTitle,
        MeetingDate: meetingDate,
        OrganizerName: organizerName,
        Participants: participants,
        Summary: docxSummaryBuilder.ToString().Trim(),
        Decisions: analysis.Decisions,
        ActionItems: exportActionItems
    );

    var startProto = DateTime.UtcNow;
    var docxBytes = protocolGenerator.GenerateDocx(exportData);
    var htmlPreview = protocolGenerator.GenerateHtml(exportData);
    var endProto = DateTime.UtcNow;

    var protocolsDir = Path.Combine(Path.GetTempPath(), "fotoprint_protocols");
    Directory.CreateDirectory(protocolsDir);
    var docxFilePath = Path.Combine(protocolsDir, $"Protocol_{meetingId}.docx");
    await File.WriteAllBytesAsync(docxFilePath, docxBytes, ct);

    try
    {
        var tmpDir = Path.Combine("/tmp", "fotoprint_protocols");
        if (!string.Equals(Path.GetFullPath(protocolsDir), Path.GetFullPath(tmpDir), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(tmpDir);
            await File.WriteAllBytesAsync(Path.Combine(tmpDir, $"Protocol_{meetingId}.docx"), docxBytes, ct);
        }
    }
    catch { }

    Guid protocolId;
    try
    {
        protocolId = await repo.SaveMeetingProtocolAsync(
            meetingId: meetingId,
            title: $"Протокол: {effectiveTitle}",
            summary: analysis.Summary,
            decisions: analysis.Decisions,
            rawTranscript: transcription.Text,
            docxPath: docxFilePath,
            ct: ct);
    }
    catch
    {
        protocolId = Guid.NewGuid();
    }

    // Шаг 5: Сохранение поручений (action_items) со статусом 'pending' и генерация кнопок подтверждения для Битрикс24
    var savedActionItems = new List<object>();
    var inlineButtons = new List<object[]>();

    foreach (var item in normalizedItems)
    {
        int? responsibleEmpId = null;
        if (!string.Equals(item.Responsible, "требует уточнения", StringComparison.OrdinalIgnoreCase) && employees.Count > 0)
        {
            var matchedEmp = employees.FirstOrDefault(e =>
                (!string.IsNullOrWhiteSpace(e.LastName) && item.Responsible.Contains(e.LastName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(e.FullName) && item.Responsible.Contains(e.FullName, StringComparison.OrdinalIgnoreCase)));
            if (matchedEmp != null)
            {
                responsibleEmpId = matchedEmp.Id;
            }
        }

        Guid actionItemId;
        try
        {
            actionItemId = await repo.CreateActionItemAsync(
                meetingId: meetingId,
                protocolId: protocolId,
                title: item.Title,
                description: item.Description ?? $"Ответственный: {item.Responsible}; Срок: {item.DeadlineText}",
                responsibleId: responsibleEmpId,
                deadline: item.ParsedDeadline,
                ct: ct);
        }
        catch
        {
            actionItemId = Guid.NewGuid();
        }

        var callbackData = $"create_bitrix_task:{actionItemId}";
        var deadlineDisplay = item.ParsedDeadline.HasValue
            ? item.ParsedDeadline.Value.ToString("yyyy-MM-dd")
            : (string.IsNullOrWhiteSpace(item.DeadlineText) ? "требует уточнения" : item.DeadlineText);

        savedActionItems.Add(new
        {
            id = actionItemId,
            title = item.Title,
            description = item.Description,
            responsible = string.IsNullOrWhiteSpace(item.Responsible) ? "требует уточнения" : item.Responsible,
            responsible_id = responsibleEmpId,
            deadline = deadlineDisplay,
            deadline_text = string.IsNullOrWhiteSpace(item.DeadlineText) ? "требует уточнения" : item.DeadlineText,
            parsed_deadline = item.ParsedDeadline,
            status = "pending",
            callback_data = callbackData
        });

        inlineButtons.Add(new object[]
        {
            new
            {
                text = $"✅ Поставить в Битрикс: \"{item.Title}\"",
                callback_data = callbackData
            }
        });
    }

    // Шаг 6: Формирование сообщения для Telegram с интерактивными кнопками (Human-in-the-Loop)
    var tgTextBuilder = new StringBuilder();
    tgTextBuilder.AppendLine($"📋 *Протокол встречи: {effectiveTitle}*");
    tgTextBuilder.AppendLine();
    tgTextBuilder.AppendLine($"📝 *Резюме:* {analysis.Summary}");

    if (analysis.Decisions.Count > 0)
    {
        tgTextBuilder.AppendLine();
        tgTextBuilder.AppendLine("✅ *Принятые решения:*");
        foreach (var dec in analysis.Decisions)
        {
            tgTextBuilder.AppendLine($"• {dec}");
        }
    }

    if (analysis.Discussed.Count > 0)
    {
        tgTextBuilder.AppendLine();
        tgTextBuilder.AppendLine("💬 *Обсуждали:*");
        foreach (var disc in analysis.Discussed)
        {
            tgTextBuilder.AppendLine($"• {disc}");
        }
    }

    if (analysis.Risks.Count > 0)
    {
        tgTextBuilder.AppendLine();
        tgTextBuilder.AppendLine("⚠️ *Риски и блокеры:*");
        foreach (var risk in analysis.Risks)
        {
            tgTextBuilder.AppendLine($"• {risk}");
        }
    }

    if (analysis.RequiresClarification.Count > 0)
    {
        tgTextBuilder.AppendLine();
        tgTextBuilder.AppendLine("❓ *Требует уточнения:*");
        foreach (var clar in analysis.RequiresClarification)
        {
            tgTextBuilder.AppendLine($"• {clar}");
        }
    }

    if (normalizedItems.Count > 0)
    {
        tgTextBuilder.AppendLine();
        tgTextBuilder.AppendLine("📌 *Поручения (нажмите кнопку для постановки задачи в Битрикс24):*");
        for (int i = 0; i < normalizedItems.Count; i++)
        {
            var ai = normalizedItems[i];
            tgTextBuilder.AppendLine($"{i + 1}. *{ai.Title}* — Отв.: _{ai.Responsible}_ | Срок: _{ai.DeadlineText}_");
        }
    }

    var telegramMessage = new
    {
        chat_id = chat_id,
        text = tgTextBuilder.ToString().Trim(),
        parse_mode = "Markdown",
        reply_markup = new
        {
            inline_keyboard = inlineButtons
        }
    };

    try
    {
        await langfuse.CreateGenerationAsync(
            traceId: traceId,
            name: "Gen:Audio_MoM_Protocol",
            model: config.Llm.Models.ChatModel,
            startTime: startProto,
            endTime: endProto,
            promptTokens: Math.Max(1, transcription.Text.Length / 4),
            completionTokens: Math.Max(1, htmlPreview.Length / 4),
            totalTokens: Math.Max(2, (transcription.Text.Length + htmlPreview.Length) / 4),
            input: transcription.Text,
            output: analysis.Summary,
            metadata: new
            {
                meeting_id = meetingId,
                protocol_id = protocolId,
                audio_provider = transcription.Provider,
                duration_ms = transcription.DurationMs,
                decisions_count = analysis.Decisions.Count,
                action_items_count = normalizedItems.Count
            },
            ct: ct);
    }
    catch { }

    return Results.Ok(new
    {
        success = true,
        meeting_id = meetingId,
        protocol_id = protocolId,
        trace_id = traceId,
        transcription = new
        {
            text = transcription.Text,
            provider = transcription.Provider,
            duration_ms = transcription.DurationMs,
            segments_count = transcription.Segments?.Count ?? 0,
            segments = transcription.Segments ?? new List<AudioSegment>()
        },
        protocol = new
        {
            title = effectiveTitle,
            summary = analysis.Summary,
            decisions = analysis.Decisions,
            discussed = analysis.Discussed,
            risks = analysis.Risks,
            requires_clarification = analysis.RequiresClarification,
            docx_file_path = docxFilePath,
            docx_base64 = Convert.ToBase64String(docxBytes),
            docx_size_bytes = docxBytes.Length,
            html_preview = htmlPreview
        },
        action_items = savedActionItems,
        telegram_message = telegramMessage
    });
}).DisableAntiforgery();

app.MapGet("/api/meetings/{id:guid}/export-docx", async (
    Guid id,
    IFotoprintRepository repo,
    IMeetingProtocolGenerator generator) =>
{
    var meeting = await repo.GetMeetingByIdAsync(id);
    if (meeting == null) return Results.NotFound();

    var notes = await repo.GetMeetingNotesAsync(id);
    var actionItems = await repo.GetActionItemsByMeetingAsync(id);
    var allEmps = await repo.GetAllEmployeesAsync();
    var empDict = allEmps.ToDictionary(e => e.Id, e => e.FullName);

    var exportData = new ProtocolExportData(
        Title: meeting.Title,
        MeetingDate: meeting.StartTime,
        OrganizerName: meeting.OrganizerId.HasValue && empDict.TryGetValue(meeting.OrganizerId.Value, out var org) ? org : "Организатор",
        Participants: meeting.ParticipantIds.Select(pid => empDict.TryGetValue(pid, out var p) ? p : $"Участник #{pid}").ToList(),
        Summary: "Сводка обсуждения и ключевые договоренности сторон.",
        Decisions: notes.Where(n => n.Category == "decision").Select(n => n.NoteText).ToList(),
        ActionItems: actionItems.Select(a => new ActionItemExportData(a.Title, a.ResponsibleId.HasValue && empDict.TryGetValue(a.ResponsibleId.Value, out var r) ? r : "—", a.Deadline, a.Status)).ToList()
    );

    var bytes = generator.GenerateDocx(exportData);
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"Protocol_{id}.docx");
});

// ============================================================================
// КОРПОРАТИВНЫЙ КАЛЕНДАРЬ (CALENDAR INTEGRATION & PROXY TO MCP-CALENDAR:8084)
// ============================================================================

// 1. Сетка событий календаря (поддержка scope=my и scope=all с RBAC)
app.MapGet("/api/calendar/events", async (
    HttpContext httpContext,
    IHttpClientFactory httpFactory,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("CalendarMcpClient");
    var queryString = httpContext.Request.QueryString.Value ?? "";
    var targetUrl = $"tools/get_calendar_events{queryString}";

    var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
    if (httpContext.Request.Headers.TryGetValue("X-Telegram-Id", out var tgId))
        req.Headers.TryAddWithoutValidation("X-Telegram-Id", tgId.ToString());
    if (httpContext.Request.Headers.TryGetValue("X-Trace-Id", out var trId))
        req.Headers.TryAddWithoutValidation("X-Trace-Id", trId.ToString());

    var resp = await client.SendAsync(req, ct);
    var content = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(content, "application/json", statusCode: (int)resp.StatusCode);
});

// 2. Подтверждение и создание встречи (Stateless Human-in-the-Loop Commit)
app.MapPost("/api/calendar/confirm", async (
    HttpContext httpContext,
    IHttpClientFactory httpFactory,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("CalendarMcpClient");
    var req = new HttpRequestMessage(HttpMethod.Post, "tools/create_meeting");
    if (httpContext.Request.Headers.TryGetValue("X-Telegram-Id", out var tgId))
        req.Headers.TryAddWithoutValidation("X-Telegram-Id", tgId.ToString());
    if (httpContext.Request.Headers.TryGetValue("X-Trace-Id", out var trId))
        req.Headers.TryAddWithoutValidation("X-Trace-Id", trId.ToString());

    using var stream = new StreamReader(httpContext.Request.Body);
    var body = await stream.ReadToEndAsync(ct);
    req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

    var resp = await client.SendAsync(req, ct);
    var content = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(content, "application/json", statusCode: (int)resp.StatusCode);
});

// 3. Подбор свободных слотов
app.MapPost("/api/calendar/slots", async (
    HttpContext httpContext,
    IHttpClientFactory httpFactory,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("CalendarMcpClient");
    var req = new HttpRequestMessage(HttpMethod.Post, "tools/find_free_slots");
    if (httpContext.Request.Headers.TryGetValue("X-Telegram-Id", out var tgId))
        req.Headers.TryAddWithoutValidation("X-Telegram-Id", tgId.ToString());
    if (httpContext.Request.Headers.TryGetValue("X-Trace-Id", out var trId))
        req.Headers.TryAddWithoutValidation("X-Trace-Id", trId.ToString());

    using var stream = new StreamReader(httpContext.Request.Body);
    var body = await stream.ReadToEndAsync(ct);
    req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

    var resp = await client.SendAsync(req, ct);
    var content = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(content, "application/json", statusCode: (int)resp.StatusCode);
});

// Прямой вызов детектора смены контекста через LLM (2-й этап)
app.MapPost("/api/context/detect-switch", async (
    [FromBody] ContextSwitchRequest req,
    IContextSwitchDetector detector,
    ILangfuseClient langfuse) =>
{
    var traceId = langfuse.GenerateTraceId();
    var messages = (req.History ?? new List<ContextMessageDto>())
        .Select(m => new ChatMessage { Role = m.Role, Content = m.Content })
        .ToList();

    var result = await detector.DetectSwitchAsync(messages, req.CurrentMessage, traceId);
    return Results.Ok(new
    {
        is_context_switched = result.IsSwitched,
        suggested_branch = result.SuggestedBranch,
        reason = result.Reason,
        trace_id = traceId
    });
});

// Эндпоинты запуска LLM-as-a-Judge оценок (Epic 6.4)
app.MapPost("/api/telemetry/evaluate-faithfulness", async (
    [FromBody] EvaluateFaithfulnessRequest req,
    ILlmJudgeService judgeService,
    ILangfuseClient langfuse) =>
{
    var traceId = req.TraceId ?? langfuse.GenerateTraceId();
    var score = await judgeService.EvaluateFaithfulnessAsync(traceId, req.UserQuestion, req.RawContextData, req.AgentAnswer);
    return Results.Ok(new { trace_id = traceId, metric = "faithfulness", score });
});

app.MapPost("/api/telemetry/evaluate-formatting-policy", async (
    [FromBody] EvaluatePolicyRequest req,
    ILlmJudgeService judgeService,
    ILangfuseClient langfuse) =>
{
    var traceId = req.TraceId ?? langfuse.GenerateTraceId();
    var score = await judgeService.EvaluateFormattingAndPolicyAsync(traceId, req.OutputText, req.IsProtocol);
    return Results.Ok(new { trace_id = traceId, metric = "formatting_and_policy", score });
});

// Telegram Webhook & Inline Handlers
Fotoprint.AiGateway.Api.Telegram.TelegramEndpoints.MapTelegramEndpoints(app);

// Web UI Single Page App
app.MapGet("/", () =>
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var target = Path.Combine(dir.FullName, "infra", "nginx", "html", "index.html");
        if (File.Exists(target))
        {
            return Results.File(target, "text/html; charset=utf-8");
        }
        dir = dir.Parent;
    }

    dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        var target = Path.Combine(dir.FullName, "infra", "nginx", "html", "index.html");
        if (File.Exists(target))
        {
            return Results.File(target, "text/html; charset=utf-8");
        }
        dir = dir.Parent;
    }

    return Results.Content("<h1>Fotoprint AI Gateway</h1><p>Web UI is served via Nginx in Docker.</p>", "text/html");
});

app.Run();

public record EvaluateFaithfulnessRequest(string? TraceId, string UserQuestion, string RawContextData, string AgentAnswer);
public record EvaluatePolicyRequest(string? TraceId, string OutputText, bool IsProtocol = false);
public record ContextSwitchRequest(List<ContextMessageDto>? History, string CurrentMessage);
public record ContextMessageDto(string Role, string Content);
public record RagSearchRequest(string Query, List<string> UserAccessGroups, int TopK = 5);
public record GeminiPromptRequest(string Prompt, string? SystemInstruction = null, double? Temperature = null, int? MaxOutputTokens = null);
public record AgentChatRequest(long TelegramId, long ChatId, string Message, string? TraceId = null);
