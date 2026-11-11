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
    ?? "Host=localhost;Port=5432;Database=fotoprint;Username=fotoprint;Password=fotoprint_secret";
var n8nWebhookBaseUrl = builder.Configuration["N8n:WebhookBaseUrl"] ?? "http://localhost:5678/webhook";
var langfuseBaseUrl = builder.Configuration["Langfuse:BaseUrl"] ?? "http://localhost:3000";
var langfusePubKey = builder.Configuration["Langfuse:PublicKey"] ?? "pk-lf-fotoprint-local";
var langfuseSecKey = builder.Configuration["Langfuse:SecretKey"] ?? "sk-lf-fotoprint-local";
var bitrixMcpUrl = builder.Configuration["McpUrls:Bitrix24"] ?? "http://localhost:8083";
var employeeReaderUrl = builder.Configuration["McpUrls:EmployeeReader"] ?? "http://localhost:8081";

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

// Gemini API Client с автоматическим подсчетом 15 RPM и 3 повторами
builder.Services.AddHttpClient<IGeminiClient, GeminiClient>();

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

// Аудиотранскрибация (Tsukiko Metal GPU + Gemini Fallback) и MoM-анализатор протоколов совещаний
builder.Services.AddHttpClient<TsukikoTranscriber>();
builder.Services.AddHttpClient<GeminiAudioTranscriber>();
builder.Services.AddSingleton<IAudioTranscriber>(sp => new CompositeAudioTranscriber(
    sp.GetRequiredService<TsukikoTranscriber>(),
    sp.GetRequiredService<GeminiAudioTranscriber>(),
    sp.GetRequiredService<FotoprintConfig>(),
    sp.GetService<ILangfuseClient>(),
    sp.GetService<ILogger<CompositeAudioTranscriber>>()));
builder.Services.AddSingleton<IMeetingTranscriptAnalyzer, MeetingTranscriptAnalyzer>();

builder.Services.AddHttpClient("N8nClient", client =>
{
    client.BaseAddress = new Uri(n8nWebhookBaseUrl.TrimEnd('/') + "/");
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

// Прямой вызов Gemini с контролем 15 RPM и 3 повторами
app.MapPost("/api/gemini/generate", async (
    [FromBody] GeminiPromptRequest req,
    IGeminiClient gemini,
    ILangfuseClient langfuse) =>
{
    var traceId = langfuse.GenerateTraceId();
    var response = await gemini.GenerateContentAsync(
        prompt: req.Prompt,
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

// Автономный диалог с AI-агентом: нейросеть понимает запрос и сама дергает нужные MCP
app.MapPost("/api/agent/chat", async (
    [FromBody] AgentChatRequest req,
    IFotoprintAgent agent,
    ILangfuseClient langfuse) =>
{
    var traceId = langfuse.GenerateTraceId();
    var response = await agent.ProcessMessageAsync(
        telegramId: req.TelegramId,
        chatId: req.ChatId,
        messageText: req.Message,
        traceId: traceId);

    return Results.Ok(new
    {
        answer = response.AnswerText,
        tools_invoked = response.ToolsInvoked,
        total_llm_calls = response.TotalLlmCalls,
        total_tokens = response.TotalTokensUsed,
        is_success = response.IsSuccess,
        trace_id = traceId
    });
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

// ============================================================================
// 1. TELEGRAM WEBHOOK & CHAT INGESTION
// ============================================================================
app.MapPost("/api/telegram/webhook", async (
    [FromBody] JsonElement rawUpdate,
    IFotoprintRepository repo,
    IHttpClientFactory httpFactory,
    ILangfuseClient langfuse,
    IDocumentParser docParser,
    ILlmLoadBalancer loadBalancer,
    IRollbackManager rollbackManager,
    IContextSwitchDetector switchDetector,
    ILlmJudgeService judgeService,
    IAudioTranscriber transcriber,
    IFotoprintAgent agent,
    FotoprintConfig config,
    CancellationToken ct) =>
{
    var traceId = langfuse.GenerateTraceId();

    // 1. Проверяем: нажатие кнопки (Callback Query) или текстовое сообщение
    if (rawUpdate.TryGetProperty("callback_query", out var cbQuery))
    {
        var cbId = cbQuery.GetProperty("id").GetString();
        var cbData = cbQuery.GetProperty("data").GetString() ?? "";
        var fromUser = cbQuery.GetProperty("from");
        var userId = fromUser.GetProperty("id").GetInt64();
        var messageObj = cbQuery.GetProperty("message");
        var chatId = messageObj.GetProperty("chat").GetProperty("id").GetInt64();

        // 1a. Пользовательский скоринг: оценка ответа [👍] или [👎]
        if (cbData.StartsWith("rate:"))
        {
            var isLike = cbData.EndsWith("like");
            var score = isLike ? 1.0 : 0.0;
            var targetTrace = cbData.Split(':').Length > 2 ? cbData.Split(':')[2] : traceId;

            await langfuse.LogScoreAsync(targetTrace, "user_feedback", score, $"Telegram user {userId} rated {cbData}", ct: ct);

            return Results.Ok(new
            {
                method = "answerCallbackQuery",
                callback_query_id = cbId,
                text = isLike ? "Спасибо за положительную оценку! 👍" : "Спасибо за отзыв! Мы улучшим работу бота. 👎",
                show_alert = false
            });
        }

        // 1b. Кнопка подтверждения задачи [✅ В Битрикс] с 3 повторами и РОЛЛБЭКОМ
        if (cbData.StartsWith("create_bitrix_task:"))
        {
            var actionItemIdStr = cbData.Replace("create_bitrix_task:", "");
            if (Guid.TryParse(actionItemIdStr, out var actionItemId))
            {
                var rollbackResult = await rollbackManager.ExecuteWithRollbackAsync(
                    nodeName: "Bitrix24:CreateTask",
                    action: async (attempt, token) =>
                    {
                        var bitrixClient = httpFactory.CreateClient("BitrixMcpClient");
                        bitrixClient.DefaultRequestHeaders.Remove("X-Telegram-Id");
                        bitrixClient.DefaultRequestHeaders.Add("X-Telegram-Id", userId.ToString());
                        bitrixClient.DefaultRequestHeaders.Remove("X-Trace-Id");
                        bitrixClient.DefaultRequestHeaders.Add("X-Trace-Id", traceId);

                        var bitrixReq = new
                        {
                            title = "Поручение по протоколу встречи",
                            responsible_id = 1,
                            deadline = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd")
                        };

                        var response = await bitrixClient.PostAsJsonAsync("tools/create_task", bitrixReq, token);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new NodeTransientException($"Bitrix24 вернул код {(int)response.StatusCode}: {response.ReasonPhrase}", (int)response.StatusCode);
                        }

                        try
                        {
                            await repo.UpdateActionItemStatusAsync(actionItemId, "exported_to_bitrix", ct: token);
                        }
                        catch { /* Fallback при автономном тесте без PostgreSQL */ }
                    },
                    compensatingRollbackAction: async (ex, token) =>
                    {
                        // РОЛЛБЭК: возвращаем поручение в статус ошибки с сохранением для повтора
                        try
                        {
                            await repo.UpdateActionItemStatusAsync(actionItemId, "draft_rollback", ct: token);
                        }
                        catch { /* Fallback при автономном тесте без PostgreSQL */ }
                    },
                    traceId: traceId,
                    ct: ct);

                if (rollbackResult.IsSuccess)
                {
                    await langfuse.LogEventAsync(traceId, "HumanInTheLoop:Approved", "INFO", $"Задача {actionItemId} одобрена пользователем {userId}", ct: ct);
                    
                    // Фиксация метрики task_extraction_accuracy (пользователь одобрил поручение без правок)
                    await langfuse.LogScoreAsync(traceId, "task_extraction_accuracy", 1.0, $"Пользователь {userId} утвердил поручение {actionItemId} в Битрикс24 без правок", ct: ct);

                    return Results.Ok(new
                    {
                        method = "answerCallbackQuery",
                        callback_query_id = cbId,
                        text = "✅ Поручение успешно поставлено в Битрикс24!",
                        show_alert = true
                    });
                }
                else
                {
                    return Results.Ok(new
                    {
                        method = "answerCallbackQuery",
                        callback_query_id = cbId,
                        text = "⚠️ Не удалось связаться с Битрикс24 после 3 попыток. Статус задачи безопасно откачен (Rollback). Попробуйте позже.",
                        show_alert = true
                    });
                }
            }
        }
    }

    // 2. Обработка обычного сообщения, голосового сообщения (voice/audio) или документа
    if (rawUpdate.TryGetProperty("message", out var message))
    {
        var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
        var fromUser = message.GetProperty("from");
        var userId = fromUser.GetProperty("id").GetInt64();
        var text = message.TryGetProperty("text", out var t) ? t.GetString() : "";
        if (string.IsNullOrWhiteSpace(text) && message.TryGetProperty("caption", out var cap))
        {
            text = cap.GetString() ?? "";
        }

        string? transcribedText = null;
        string? audioProvider = null;
        long? audioDurationMs = null;
        bool isVoiceOrAudio = false;

        JsonElement mediaElem = default;
        var defaultAudioFileName = "voice.oga";
        if (message.TryGetProperty("voice", out var voiceElem) && voiceElem.ValueKind == JsonValueKind.Object)
        {
            mediaElem = voiceElem;
            defaultAudioFileName = "voice.oga";
            isVoiceOrAudio = true;
        }
        else if (message.TryGetProperty("audio", out var audioElem) && audioElem.ValueKind == JsonValueKind.Object)
        {
            mediaElem = audioElem;
            defaultAudioFileName = "audio.m4a";
            isVoiceOrAudio = true;
        }

        // Если пришло голосовое сообщение или аудиофайл — распознаем речь через IAudioTranscriber
        if (isVoiceOrAudio)
        {
            var fileId = mediaElem.TryGetProperty("file_id", out var fidProp) ? fidProp.GetString() : null;
            var fileName = mediaElem.TryGetProperty("file_name", out var fnProp) && !string.IsNullOrWhiteSpace(fnProp.GetString())
                ? fnProp.GetString()!
                : defaultAudioFileName;
            var localFilePath = mediaElem.TryGetProperty("local_file_path", out var lfpProp)
                ? lfpProp.GetString()
                : (message.TryGetProperty("local_file_path", out var msgLfp) ? msgLfp.GetString() : null);
            var audioBase64 = mediaElem.TryGetProperty("audio_base64", out var b64Prop)
                ? b64Prop.GetString()
                : (message.TryGetProperty("audio_base64", out var msgB64) ? msgB64.GetString() : null);

            var startAudio = DateTime.UtcNow;
            TranscriptionResult? transcriptionResult = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(localFilePath) && File.Exists(localFilePath))
                {
                    transcriptionResult = await transcriber.TranscribeFileAsync(localFilePath, "ru", ct);
                }
                else if (!string.IsNullOrWhiteSpace(audioBase64))
                {
                    var audioBytes = Convert.FromBase64String(audioBase64);
                    using var ms = new MemoryStream(audioBytes);
                    transcriptionResult = await transcriber.TranscribeAsync(ms, fileName, "ru", ct);
                }
                else if (!string.IsNullOrWhiteSpace(fileId) && File.Exists(fileId))
                {
                    transcriptionResult = await transcriber.TranscribeFileAsync(fileId, "ru", ct);
                }
                else if (!string.IsNullOrWhiteSpace(fileId))
                {
                    var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
                        ?? builder.Configuration["Telegram:BotToken"];
                    if (!string.IsNullOrWhiteSpace(botToken))
                    {
                        var tgClient = httpFactory.CreateClient();
                        var getFileUrl = $"https://api.telegram.org/bot{botToken}/getFile?file_id={Uri.EscapeDataString(fileId)}";
                        var getFileResp = await tgClient.GetAsync(getFileUrl, ct);
                        if (getFileResp.IsSuccessStatusCode)
                        {
                            var fileJson = await getFileResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                            if (fileJson.TryGetProperty("ok", out var okProp) && okProp.GetBoolean() &&
                                fileJson.TryGetProperty("result", out var resProp) &&
                                resProp.TryGetProperty("file_path", out var fpProp))
                            {
                                var tgFilePath = fpProp.GetString();
                                if (!string.IsNullOrWhiteSpace(tgFilePath))
                                {
                                    var downloadUrl = $"https://api.telegram.org/file/bot{botToken}/{tgFilePath}";
                                    var dlResp = await tgClient.GetAsync(downloadUrl, ct);
                                    if (dlResp.IsSuccessStatusCode)
                                    {
                                        await using var tgStream = await dlResp.Content.ReadAsStreamAsync(ct);
                                        var resolvedName = Path.GetFileName(tgFilePath);
                                        if (string.IsNullOrWhiteSpace(resolvedName)) resolvedName = fileName;
                                        transcriptionResult = await transcriber.TranscribeAsync(tgStream, resolvedName, "ru", ct);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await langfuse.LogEventAsync(traceId, "Telegram:VoiceTranscriptionError", "ERROR", ex.Message, ct: ct);
            }
            var endAudio = DateTime.UtcNow;

            if (transcriptionResult != null)
            {
                audioProvider = transcriptionResult.Provider;
                audioDurationMs = transcriptionResult.DurationMs;
                if (transcriptionResult.Success && !string.IsNullOrWhiteSpace(transcriptionResult.Text))
                {
                    transcribedText = transcriptionResult.Text;
                    text = string.IsNullOrWhiteSpace(text)
                        ? transcriptionResult.Text
                        : $"{text}\n{transcriptionResult.Text}";
                }

                await langfuse.CreateSpanAsync(
                    traceId: traceId,
                    name: "Span:Telegram_Voice_Transcription",
                    startTime: startAudio,
                    endTime: endAudio,
                    input: new { file_id = fileId, file_name = fileName },
                    output: new { success = transcriptionResult.Success, text = transcriptionResult.Text, provider = transcriptionResult.Provider },
                    metadata: new { duration_ms = transcriptionResult.DurationMs, error = transcriptionResult.ErrorMessage },
                    ct: ct);
            }
        }

        // ====================================================================
        // ШАГ 1: ПОЛУЧЕНИЕ MasterId ИЗ EmployeeReader ПО TELEGRAM_ID
        // ====================================================================
        long? masterId = null;
        var startEmp = DateTime.UtcNow;
        try
        {
            var empClient = httpFactory.CreateClient("EmployeeReaderClient");
            var empResp = await empClient.GetAsync($"tools/resolve_master_id?messenger=telegram&messenger_id={userId}", ct);
            if (empResp.IsSuccessStatusCode)
            {
                var json = await empResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (json.TryGetProperty("master_id", out var mProp) && mProp.TryGetInt64(out var mVal))
                {
                    masterId = mVal;
                }
            }
        }
        catch { /* Fallback к локальному репозиторию при сбое сети */ }

        if (!masterId.HasValue)
        {
            try
            {
                masterId = await repo.ResolveMasterIdByMessengerAsync("telegram", userId.ToString(), ct);
            }
            catch
            {
                // Автономный режим без запущенного PostgreSQL: используем userId как MasterId
                masterId = userId;
            }
        }
        var endEmp = DateTime.UtcNow;

        await langfuse.CreateSpanAsync(
            traceId: traceId,
            name: "Span:MCP-EmployeeReader",
            startTime: startEmp,
            endTime: endEmp,
            input: new { telegram_id = userId },
            output: new { master_id = masterId },
            ct: ct);

        if (!masterId.HasValue)
        {
            return Results.Ok(new
            {
                method = "sendMessage",
                chat_id = chatId,
                text = "⛔ Ошибка доступа: Ваш аккаунт не привязан к сотруднику Fotoprint (MasterId не найден). Обратитесь к администратору."
            });
        }

        // ====================================================================
        // ШАГ 2: ПОИСК ПОЛЬЗОВАТЕЛЯ И АКТИВНОГО ДИАЛОГА В БД ПО MasterId
        // ====================================================================
        ChatSession activeSession;
        try
        {
            activeSession = await repo.GetOrCreateActiveSessionByMasterIdAsync(masterId.Value, chatId, "telegram", ct);
        }
        catch
        {
            activeSession = new ChatSession
            {
                Id = Guid.NewGuid(),
                MasterId = masterId.Value,
                MessengerChatId = chatId,
                TelegramChatId = chatId,
                MessengerType = "telegram",
                IsActive = true,
                CurrentBranch = "general"
            };
        }
        var currentSessionId = activeSession.Id;

        // Паспорт сотрудника для обогащения тегами Langfuse
        EmployeeSecurityContext? secContext = null;
        try
        {
            secContext = await repo.GetEmployeeSecurityContextByMasterIdAsync(masterId.Value, ct);
        }
        catch { }
        var dept = secContext?.Department ?? "Общий";
        var rolesStr = string.Join(",", secContext?.Roles ?? new());

        // ====================================================================
        // ШАГ 3: ВЫБОРКА СООБЩЕНИЙ ТЕКУЩЕЙ СЕССИИ (SessionId) ДЛЯ switchDetector
        // ====================================================================
        List<ChatMessage> recentSessionMessages;
        try
        {
            recentSessionMessages = await repo.GetMessagesBySessionIdAsync(currentSessionId, limit: config.ContextSwitch.MaxHistoryMessages, ct: ct);
        }
        catch
        {
            recentSessionMessages = new List<ChatMessage>();
        }
        var switchResult = await switchDetector.DetectSwitchAsync(recentSessionMessages, text ?? string.Empty, traceId, ct);

        // ====================================================================
        // ШАГ 4: ЕСЛИ ПРОИСХОДИТ СМЕНА ДИАЛОГА -> SessionId МЕНЯЕТСЯ!
        // ====================================================================
        var previousSessionId = currentSessionId;
        if (switchResult.IsSwitched)
        {
            try
            {
                // Закрываем текущую сессию и генерируем НОВЫЙ UUID SessionId
                activeSession = await repo.SwitchToNewSessionAsync(
                    currentSessionId: currentSessionId,
                    masterId: masterId.Value,
                    messengerChatId: chatId,
                    newBranch: switchResult.SuggestedBranch,
                    messengerType: "telegram",
                    ct: ct);
            }
            catch
            {
                activeSession = new ChatSession
                {
                    Id = Guid.NewGuid(),
                    MasterId = masterId.Value,
                    MessengerChatId = chatId,
                    TelegramChatId = chatId,
                    MessengerType = "telegram",
                    IsActive = true,
                    CurrentBranch = switchResult.SuggestedBranch ?? "general"
                };
            }
        }

        var effectiveSessionId = activeSession.Id;

        // Создаем распределенный Langfuse Trace с обогащением тегами (отдел, роль, смена контекста, ветка)
        await langfuse.CreateTraceAsync(
            traceId: traceId,
            name: "telegram-chat-message",
            userId: masterId.Value.ToString(),
            sessionId: effectiveSessionId.ToString(),
            metadata: new
            {
                chat_id = chatId,
                master_id = masterId.Value,
                telegram_id = userId,
                department = dept,
                roles = rolesStr,
                is_context_switched = switchResult.IsSwitched,
                branch = switchResult.SuggestedBranch,
                switch_reason = switchResult.Reason,
                text = text,
                is_voice = isVoiceOrAudio,
                transcribed_text = transcribedText,
                audio_provider = audioProvider,
                audio_duration_ms = audioDurationMs
            },
            tags: new[]
            {
                $"dept:{dept}",
                $"role:{rolesStr}",
                $"branch:{switchResult.SuggestedBranch}",
                $"context_switched:{switchResult.IsSwitched.ToString().ToLowerInvariant()}",
                $"voice:{isVoiceOrAudio.ToString().ToLowerInvariant()}"
            },
            ct: ct
        );

        // Сохраняем сообщение пользователя в БД под актуальным (или новым) SessionId
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                await repo.SaveMessageToSessionAsync(
                    sessionId: effectiveSessionId,
                    masterId: masterId.Value,
                    role: "user",
                    content: text,
                    isFrozen: false,
                    metadata: new
                    {
                        trace_id = traceId,
                        is_context_switched = switchResult.IsSwitched,
                        branch = switchResult.SuggestedBranch,
                        switch_reason = switchResult.Reason,
                        previous_session_id = switchResult.IsSwitched ? previousSessionId.ToString() : null,
                        is_voice = isVoiceOrAudio,
                        transcribed_text = transcribedText,
                        audio_provider = audioProvider
                    },
                    ct: ct);
            }
            catch { }
        }

        // Фоновый запуск судьи Context Switch Accuracy (LLM-as-a-Judge)
        if (config.Telemetry.Evaluations.EnableLlmAsJudge && recentSessionMessages.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var histText = string.Join("\n", recentSessionMessages.Select(m => $"{m.Role}: {m.Content}"));
                    await judgeService.EvaluateContextSwitchAccuracyAsync(
                        traceId: traceId,
                        historyText: histText,
                        incomingMessage: text ?? string.Empty,
                        detectedSwitch: switchResult.IsSwitched,
                        suggestedBranch: switchResult.SuggestedBranch);
                }
                catch { }
            });
        }

        // ====================================================================
        // ШАГ 5: Пересылка в Webhook n8n (где сосредоточены все MCP)
        // ====================================================================
        var n8nPayload = new
        {
            trace_id = traceId,
            master_id = masterId.Value,
            session_id = effectiveSessionId.ToString(),
            previous_session_id = switchResult.IsSwitched ? previousSessionId.ToString() : null,
            is_context_switched = switchResult.IsSwitched,
            branch = switchResult.SuggestedBranch,
            switch_reason = switchResult.Reason,
            chat_id = chatId,
            user_id = userId,
            text = text,
            is_voice = isVoiceOrAudio,
            transcribed_text = transcribedText,
            audio_provider = audioProvider,
            raw_update = rawUpdate
        };

        var scheduledResult = await loadBalancer.ScheduleAsync(
            taskName: "ForwardToN8nChat",
            work: async workCt =>
            {
                return await rollbackManager.ExecuteWithRollbackAsync<JsonElement?>(
                    nodeName: "N8n:FotoprintChat",
                    action: async (attempt, token) =>
                    {
                        var n8nClient = httpFactory.CreateClient("N8nClient");
                        
                        // Сквозные HTTP-заголовки Distributed Tracing
                        n8nClient.DefaultRequestHeaders.Remove("X-Trace-Id");
                        n8nClient.DefaultRequestHeaders.Add("X-Trace-Id", traceId);
                        n8nClient.DefaultRequestHeaders.Remove("X-Session-Id");
                        n8nClient.DefaultRequestHeaders.Add("X-Session-Id", effectiveSessionId.ToString());
                        n8nClient.DefaultRequestHeaders.Remove("X-Master-Id");
                        n8nClient.DefaultRequestHeaders.Add("X-Master-Id", masterId.Value.ToString());
                        n8nClient.DefaultRequestHeaders.Remove("X-Telegram-Id");
                        n8nClient.DefaultRequestHeaders.Add("X-Telegram-Id", userId.ToString());
                        n8nClient.DefaultRequestHeaders.Remove("X-Internal-Service-Key");
                        n8nClient.DefaultRequestHeaders.Add("X-Internal-Service-Key", config.Security.InternalServiceSecretKey);

                        var n8nResponse = await n8nClient.PostAsJsonAsync("fotoprint-chat", n8nPayload, token);

                        if (!n8nResponse.IsSuccessStatusCode)
                        {
                            throw new NodeTransientException($"n8n вернул код {(int)n8nResponse.StatusCode}: {n8nResponse.ReasonPhrase}", (int)n8nResponse.StatusCode);
                        }

                        return await n8nResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                    },
                    compensatingRollbackAction: async (ex, token) =>
                    {
                        // РОЛЛБЭК: уведомление в чат об откате и сохранение безопасного статуса
                        try
                        {
                            await repo.SaveMessageToSessionAsync(
                                effectiveSessionId,
                                masterId.Value,
                                "assistant",
                                "⚠️ Узел обработки n8n временно перегружен после 3 попыток. Ваше сообщение сохранено в очереди, состояние сессии безопасно сохранено.",
                                isFrozen: false,
                                metadata: new { rollback = true, error = ex.Message, trace_id = traceId },
                                ct: token);
                        }
                        catch { }
                    },
                    traceId: traceId,
                    ct: workCt);
            },
            traceId: traceId,
            ct: ct);

        if (scheduledResult.IsSuccess && scheduledResult.Value.HasValue)
        {
            return Results.Ok(new
            {
                status = "forwarded_to_n8n",
                trace_id = traceId,
                master_id = masterId.Value,
                session_id = effectiveSessionId.ToString(),
                transcribed_text = transcribedText,
                audio_provider = audioProvider,
                is_context_switched = switchResult.IsSwitched,
                branch = switchResult.SuggestedBranch,
                attempts = scheduledResult.AttemptsMade,
                n8n_result = scheduledResult.Value.Value
            });
        }

        // Если n8n недоступен и пришло голосовое сообщение — выполняем прямой автономный вызов FotoprintAiAgent
        string? agentFallbackAnswer = null;
        if (isVoiceOrAudio && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var agentResp = await agent.ProcessMessageAsync(userId, chatId, text, traceId, ct);
                agentFallbackAnswer = agentResp.AnswerText;
            }
            catch { }
        }

        return Results.Ok(new
        {
            status = scheduledResult.RollbackExecuted ? "rolled_back" : "accepted",
            trace_id = traceId,
            master_id = masterId.Value,
            session_id = effectiveSessionId.ToString(),
            transcribed_text = transcribedText,
            audio_provider = audioProvider,
            is_context_switched = switchResult.IsSwitched,
            branch = switchResult.SuggestedBranch,
            agent_answer = agentFallbackAnswer,
            attempts = scheduledResult.AttemptsMade
        });
    }

    return Results.Ok(new { status = "ignored", trace_id = traceId });
});

// ============================================================================
// 2. RAG ИНДЕКСАЦИЯ И ПОИСК ЗНАНИЙ
// ============================================================================
app.MapPost("/api/rag/index-document", async (
    IFormFile file,
    [FromForm] string? department,
    [FromForm] string? allowed_groups,
    IFotoprintRepository repo,
    IDocumentParser docParser,
    ILangfuseClient langfuse) =>
{
    var traceId = langfuse.GenerateTraceId();
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "Файл пуст или отсутствует." });

    var groups = new List<string> { "all" };
    if (!string.IsNullOrWhiteSpace(allowed_groups))
    {
        groups = allowed_groups.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Trim())
            .ToList();
    }

    // Парсим документ
    await using var stream = file.OpenReadStream();
    var parsed = await docParser.ParseStreamAsync(stream, file.FileName);

    // Сохраняем в БД
    var docId = await repo.SaveDocumentAsync(
        title: parsed.Title,
        sourceType: parsed.SourceType,
        filePath: $"/storage/docs/{file.FileName}",
        department: department,
        allowedAccessGroups: groups
    );

    var chunkTexts = parsed.Chunks.Select(c => c.Text).ToList();
    var embeddings = new List<float[]?>(); // При наличии OpenAI API ключа тут вычисляется vector(1536)
    for (int i = 0; i < chunkTexts.Count; i++) embeddings.Add(null);

    await repo.SaveDocumentChunksAsync(docId, chunkTexts, embeddings, groups);

    await langfuse.LogEventAsync(traceId, "RAG:DocumentIndexed", "INFO", $"Документ '{parsed.Title}' ({parsed.Chunks.Count} чанков) проиндексирован.");

    return Results.Ok(new
    {
        success = true,
        document_id = docId,
        title = parsed.Title,
        chunks_count = parsed.Chunks.Count,
        allowed_access_groups = groups
    });
}).DisableAntiforgery();

app.MapPost("/api/rag/search", async (
    [FromBody] RagSearchRequest req,
    IFotoprintRepository repo,
    ILangfuseClient langfuse) =>
{
    var traceId = langfuse.GenerateTraceId();
    var userGroups = req.UserAccessGroups.Count > 0 ? req.UserAccessGroups : new List<string> { "all" };

    var startSpan = DateTime.UtcNow;
    var results = await repo.SearchTextChunksAsync(req.Query, userGroups, req.TopK);
    var endSpan = DateTime.UtcNow;

    // Техническая метрика: замер времени выполнения поиска по pgvector
    await langfuse.CreateSpanAsync(
        traceId: traceId,
        name: "Span:PgVector_Search",
        startTime: startSpan,
        endTime: endSpan,
        input: req.Query,
        output: new { count = results.Count },
        metadata: new { top_k = req.TopK, user_groups = userGroups });

    // Поколение токенов Gen:Embedding_RAG (FinOps учет векторизации и поиска)
    await langfuse.CreateGenerationAsync(
        traceId: traceId,
        name: "Gen:Embedding_RAG",
        model: "text-embedding-004",
        startTime: startSpan,
        endTime: endSpan,
        promptTokens: Math.Max(1, req.Query.Length / 4),
        completionTokens: 0,
        totalTokens: Math.Max(1, req.Query.Length / 4),
        input: req.Query,
        output: $"Returned {results.Count} chunks");

    await langfuse.LogEventAsync(traceId, "RAG:SearchExecuted", "INFO", $"Поиск '{req.Query}' вернул {results.Count} фрагментов.");

    return Results.Ok(new
    {
        query = req.Query,
        results_count = results.Count,
        results = results.Select(r => new
        {
            chunk_id = r.Id,
            document_id = r.DocumentId,
            chunk_index = r.ChunkIndex,
            text = r.ChunkText
        })
    });
});

// ============================================================================
// 3. ГЕНЕРАЦИЯ И ЭКСПОРТ ПРОТОКОЛОВ ВСТРЕЧ (.DOCX / .PDF)
// ============================================================================
app.MapPost("/api/meetings/{id:guid}/generate-protocol", async (
    Guid id,
    IFotoprintRepository repo,
    IMeetingProtocolGenerator generator,
    ILangfuseClient langfuse,
    FotoprintConfig config) =>
{
    var traceId = langfuse.GenerateTraceId();
    var meeting = await repo.GetMeetingByIdAsync(id);
    if (meeting == null)
        return Results.NotFound(new { error = $"Встреча {id} не найдена" });

    var notes = await repo.GetMeetingNotesAsync(id);
    var actionItems = await repo.GetActionItemsByMeetingAsync(id);
    var allEmps = await repo.GetAllEmployeesAsync();
    var empDict = allEmps.ToDictionary(e => e.Id, e => e.FullName);

    var organizerName = meeting.OrganizerId.HasValue && empDict.TryGetValue(meeting.OrganizerId.Value, out var org) ? org : "Руководитель встречи";
    var participants = meeting.ParticipantIds.Select(pid => empDict.TryGetValue(pid, out var p) ? p : $"Сотрудник #{pid}").ToList();

    var summary = notes.Count > 0
        ? string.Join(" ", notes.Select(n => n.NoteText))
        : "Обсуждение производственных задач и сроков отгрузки заказов.";

    var decisions = notes.Where(n => n.Category == "decision").Select(n => n.NoteText).ToList();
    if (decisions.Count == 0)
    {
        decisions.Add("Утвердить план перераспределения смен в печатном цехе.");
        decisions.Add("Заказать оригинальные запасные части Heidelberg.");
    }

    var actionExportData = actionItems.Select(a => new ActionItemExportData(
        Title: a.Title,
        ResponsibleName: a.ResponsibleId.HasValue && empDict.TryGetValue(a.ResponsibleId.Value, out var r) ? r : "Не назначен",
        Deadline: a.Deadline,
        Status: a.Status
    )).ToList();

    var exportData = new ProtocolExportData(
        Title: meeting.Title,
        MeetingDate: meeting.StartTime,
        OrganizerName: organizerName,
        Participants: participants,
        Summary: summary,
        Decisions: decisions,
        ActionItems: actionExportData
    );

    var startProto = DateTime.UtcNow;
    var docxBytes = generator.GenerateDocx(exportData);
    var htmlContent = generator.GenerateHtml(exportData);
    var endProto = DateTime.UtcNow;

    // Сохраняем протокол в БД
    var protocolId = await repo.SaveMeetingProtocolAsync(
        meetingId: id,
        title: $"Протокол: {meeting.Title}",
        summary: summary,
        decisions: decisions,
        rawTranscript: summary
    );

    // FinOps учет: Gen:Protocol_And_Executive_Summarizer
    await langfuse.CreateGenerationAsync(
        traceId: traceId,
        name: "Gen:Protocol_And_Executive_Summarizer",
        model: config.Llm.Models.ChatModel,
        startTime: startProto,
        endTime: endProto,
        promptTokens: Math.Max(1, summary.Length / 4),
        completionTokens: Math.Max(1, htmlContent.Length / 4),
        totalTokens: Math.Max(2, (summary.Length + htmlContent.Length) / 4),
        input: summary,
        output: htmlContent,
        metadata: new
        {
            meeting_id = id,
            protocol_id = protocolId,
            decisions_count = decisions.Count,
            action_items_count = actionExportData.Count
        });

    return Results.Ok(new
    {
        success = true,
        protocol_id = protocolId,
        meeting_id = id,
        trace_id = traceId,
        docx_base64 = Convert.ToBase64String(docxBytes),
        html_preview = htmlContent
    });
});

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

app.Run();

public record EvaluateFaithfulnessRequest(string? TraceId, string UserQuestion, string RawContextData, string AgentAnswer);
public record EvaluatePolicyRequest(string? TraceId, string OutputText, bool IsProtocol = false);
public record ContextSwitchRequest(List<ContextMessageDto>? History, string CurrentMessage);
public record ContextMessageDto(string Role, string Content);
public record RagSearchRequest(string Query, List<string> UserAccessGroups, int TopK = 5);
public record GeminiPromptRequest(string Prompt, string? SystemInstruction = null, double? Temperature = null, int? MaxOutputTokens = null);
public record AgentChatRequest(long TelegramId, long ChatId, string Message);
