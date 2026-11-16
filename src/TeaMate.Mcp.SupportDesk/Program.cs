using System.Text.Json;
using TeaMate.Core.Configuration;
using TeaMate.Core.Data;
using TeaMate.Core.Mcp;
using TeaMate.Core.Models;
using TeaMate.Core.Security;
using TeaMate.Core.Telegram;
using TeaMate.Core.Telemetry;

var builder = WebApplication.CreateBuilder(args);

var appConfig = TeaMateConfigLoader.Load();
builder.Services.AddSingleton(appConfig);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=fotoprint;Username=fotoprint;Password=fotoprint_secret";
var employeeReaderUrl = builder.Configuration["Security:EmployeeReaderUrl"] ?? "http://localhost:8081";
var langfuseBaseUrl = builder.Configuration["Langfuse:BaseUrl"] ?? "http://localhost:3000";
var langfusePubKey = builder.Configuration["Langfuse:PublicKey"] ?? "pk-lf-fotoprint-local";
var langfuseSecKey = builder.Configuration["Langfuse:SecretKey"] ?? "sk-lf-fotoprint-local";

builder.Services.AddSingleton<ITeaMateRepository>(_ => new TeaMateRepository(connectionString));

builder.Services.AddHttpClient<ILangfuseClient, LangfuseClient>();
builder.Services.AddSingleton(new LangfuseOptions
{
    BaseUrl = langfuseBaseUrl,
    PublicKey = langfusePubKey,
    SecretKey = langfuseSecKey,
    Enabled = true
});

builder.Services.AddHttpClient<ISecurityValidator, SecurityValidator>(client =>
{
    client.BaseAddress = new Uri(employeeReaderUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Internal-Service-Key", appConfig.Security.InternalServiceSecretKey);
});

var botOptions = new TelegramBotOptions
{
    BotToken = builder.Configuration["Telegram:BotToken"]
        ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? string.Empty,
    WebhookSecret = builder.Configuration["Telegram:WebhookSecret"]
        ?? Environment.GetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET"),
    ApiBaseUrl = builder.Configuration["Telegram:ApiBaseUrl"] ?? "https://api.telegram.org"
};
builder.Services.AddSingleton(botOptions);
builder.Services.AddHttpClient<ITelegramBotClient, TelegramBotClient>(client =>
{
    client.BaseAddress = new Uri(botOptions.ApiBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

var app = builder.Build();

// 1. Строгий порядок Middleware периметра и RBAC
app.UseMiddleware<PerimeterDefenseMiddleware>();
app.UseMiddleware<InternalServiceAuthMiddleware>();
app.UseMiddleware<SecurityInterceptorMiddleware>();

// 2. Healthcheck
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "MCP-SupportDesk",
    operator_telegram_id = appConfig.Support.OperatorTelegramId,
    timestamp = DateTime.UtcNow
}));

// 3. Каталог инструментов (/tools и /tools/list)
var tools = new List<McpToolDefinition>
{
    new()
    {
        Name = "escalate_to_operator",
        Description = "Переключает текущий диалог на живого дежурного оператора поддержки (Telegram ID 2135203645). Вызывать, когда пользователь просит позвать человека/оператора/специалиста или вопрос требует вмешательства сотрудника поддержки.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                reason = new { type = "string", description = "Краткая причина обращения или суть вопроса пользователя для оператора" },
                urgency = new { type = "string", description = "Уровень срочности: low, normal, high" },
                session_id = new { type = "string", description = "UUID текущей сессии чата (опционально)" }
            },
            required = new[] { "reason" }
        }
    },
    new()
    {
        Name = "close_operator_session",
        Description = "Завершает активную сессию диалога с живым оператором и возвращает диалог в стандартный режим AI-ассистента.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                escalation_id = new { type = "string", description = "UUID эскалации поддержки" }
            },
            required = new[] { "escalation_id" }
        }
    }
};

app.MapGet("/tools", () => Results.Ok(new { tools }));
app.MapGet("/tools/list", () => Results.Ok(new { tools }));

// 4. Эскалация на оператора поддержки (TG: 2135203645)
app.MapPost("/tools/escalate_to_operator",
    [RequirePermission("support:escalate")]
    [RequireConfirmation(false)]
    async (
        EscalateToOperatorRequest req,
        ITeaMateRepository repo,
        ITelegramBotClient tg,
        ILangfuseClient langfuse,
        TeaMateConfig config,
        HttpContext ctx) =>
    {
        var traceId = ctx.Request.Headers.TryGetValue("X-Trace-Id", out var tr) && !string.IsNullOrWhiteSpace(tr)
            ? tr.ToString()
            : langfuse.GenerateTraceId();

        var secContext = ctx.Items["SecurityContext"] as EmployeeSecurityContext;
        long userTelegramId = req.UserTelegramId ?? secContext?.TelegramId ?? 0;
        if (userTelegramId == 0 && ctx.Request.Headers.TryGetValue("X-Telegram-Id", out var tgHeader) && long.TryParse(tgHeader, out var parsedTg))
        {
            userTelegramId = parsedTg;
        }

        if (secContext == null && userTelegramId > 0)
        {
            try
            {
                secContext = await repo.GetEmployeeSecurityContextAsync(userTelegramId, ctx.RequestAborted);
            }
            catch { }
        }

        long userMasterId = req.UserMasterId ?? secContext?.MasterId ?? userTelegramId;
        long operatorTgId = req.OperatorTelegramId is > 0
            ? req.OperatorTelegramId.Value
            : (config.Support.OperatorTelegramId > 0 ? config.Support.OperatorTelegramId : 2135203645);

        var reason = string.IsNullOrWhiteSpace(req.Reason)
            ? "Пользователь запросил подключение оператора поддержки"
            : req.Reason.Trim();
        var urgency = string.IsNullOrWhiteSpace(req.Urgency)
            ? "normal"
            : req.Urgency.Trim().ToLowerInvariant();
        if (urgency is not ("low" or "normal" or "high"))
        {
            urgency = "normal";
        }

        Guid sessionId = req.SessionId ?? Guid.Empty;
        try
        {
            if (sessionId == Guid.Empty && userMasterId > 0)
            {
                var session = await repo.GetOrCreateActiveSessionByMasterIdAsync(
                    userMasterId,
                    userTelegramId > 0 ? userTelegramId : userMasterId,
                    "telegram",
                    ctx.RequestAborted);
                sessionId = session.Id;
            }
        }
        catch
        {
            if (sessionId == Guid.Empty) sessionId = Guid.NewGuid();
        }

        Guid escalationId;
        try
        {
            var existing = sessionId != Guid.Empty
                ? await repo.GetActiveSupportEscalationBySessionIdAsync(sessionId, ctx.RequestAborted)
                : null;

            if (existing != null)
            {
                escalationId = existing.Id;
            }
            else
            {
                escalationId = await repo.CreateSupportEscalationAsync(
                    sessionId: sessionId,
                    userMasterId: userMasterId,
                    userTelegramId: userTelegramId > 0 ? userTelegramId : null,
                    operatorTelegramId: operatorTgId,
                    reason: reason,
                    urgency: urgency,
                    metadata: new
                    {
                        trace_id = traceId,
                        employee_name = secContext?.FullName,
                        department = secContext?.Department,
                        position = secContext?.Position
                    },
                    ct: ctx.RequestAborted);
            }

            if (sessionId != Guid.Empty)
            {
                await repo.UpdateSessionBranchAsync(sessionId, "operator_live", ctx.RequestAborted);
            }
        }
        catch
        {
            escalationId = Guid.NewGuid();
        }

        var userNameDisplay = !string.IsNullOrWhiteSpace(secContext?.FullName)
            ? $"{secContext.FullName} ({secContext.Department}, {secContext.Position})"
            : $"Пользователь TG:{userTelegramId} (MasterId:{userMasterId})";

        var urgencyLabel = urgency switch
        {
            "high" => "🔴 Высокая (HIGH)",
            "low" => "🟢 Низкая (LOW)",
            _ => "🟡 Обычная (NORMAL)"
        };

        var cardText =
            $"🚨 Вызов оператора поддержки!\n\n" +
            $"👤 Сотрудник: {userNameDisplay}\n" +
            $"🆔 Telegram ID: {userTelegramId}\n" +
            $"⚡ Срочность: {urgencyLabel}\n" +
            $"📝 Причина обращения: {reason}\n" +
            $"🔗 Тикет: {escalationId}\n\n" +
            $"💡 Ответьте (Reply) на это сообщение, чтобы отправить ответ сотруднику в чат.";

        var buttons = new[]
        {
            new[]
            {
                new TelegramInlineButton("🔴 Завершить диалог", $"support:close:{escalationId}")
            }
        };

        int? operatorMessageId = null;
        bool telegramDelivered = false;
        try
        {
            var tgRes = await tg.SendMessageAsync(operatorTgId, cardText, inlineKeyboard: buttons, ct: ctx.RequestAborted);
            telegramDelivered = tgRes.Ok;
            if (tgRes.Result is { ValueKind: JsonValueKind.Object } resElem &&
                resElem.TryGetProperty("message_id", out var midProp) &&
                midProp.TryGetInt32(out var mid))
            {
                operatorMessageId = mid;
                await repo.UpdateSupportEscalationOperatorMessageIdAsync(escalationId, mid, ctx.RequestAborted);
            }
        }
        catch { }

        await langfuse.LogEventAsync(
            traceId,
            "SupportDesk:Escalated",
            "INFO",
            $"Диалог переведен на оператора {operatorTgId}. Причина: {reason} (Urgency: {urgency}, EscalationId: {escalationId})",
            metadata: new
            {
                escalation_id = escalationId,
                session_id = sessionId,
                user_master_id = userMasterId,
                user_telegram_id = userTelegramId,
                operator_telegram_id = operatorTgId,
                operator_message_id = operatorMessageId,
                reason,
                urgency,
                telegram_delivered = telegramDelivered
            },
            ct: ctx.RequestAborted);

        return Results.Ok(new
        {
            status = "escalated",
            escalation_id = escalationId,
            session_id = sessionId,
            user_master_id = userMasterId,
            user_telegram_id = userTelegramId,
            operator_telegram_id = operatorTgId,
            operator_message_id = operatorMessageId,
            telegram_delivered = telegramDelivered,
            branch = "operator_live",
            reason,
            urgency,
            message = $"Заявка передана дежурному оператору поддержки (Telegram ID: {operatorTgId}). Сессия переведена в режим живого диалога с оператором."
        });
    });

// 5. Завершение сессии с оператором
app.MapPost("/tools/close_operator_session",
    [RequirePermission("support:escalate")]
    [RequireConfirmation(false)]
    async (
        CloseOperatorSessionRequest req,
        ITeaMateRepository repo,
        ITelegramBotClient tg,
        ILangfuseClient langfuse,
        HttpContext ctx) =>
    {
        var traceId = ctx.Request.Headers.TryGetValue("X-Trace-Id", out var tr) && !string.IsNullOrWhiteSpace(tr)
            ? tr.ToString()
            : langfuse.GenerateTraceId();

        SupportEscalation? esc = null;
        try
        {
            if (req.EscalationId != Guid.Empty)
            {
                esc = await repo.GetSupportEscalationByIdAsync(req.EscalationId, ctx.RequestAborted);
                await repo.CloseSupportEscalationAsync(req.EscalationId, req.Status ?? "resolved", ctx.RequestAborted);
            }

            if (esc != null && esc.SessionId != Guid.Empty)
            {
                await repo.UpdateSessionBranchAsync(esc.SessionId, "general", ctx.RequestAborted);
            }
        }
        catch { }

        if (esc != null)
        {
            try
            {
                if (esc.UserTelegramId is > 0)
                {
                    await tg.SendMessageAsync(
                        esc.UserTelegramId.Value,
                        "✅ Диалог с оператором поддержки завершен. AI-ассистент TeaMate снова на связи — чем еще я могу вам помочь?",
                        ct: ctx.RequestAborted);
                }

                if (esc.OperatorTelegramId > 0)
                {
                    await tg.SendMessageAsync(
                        esc.OperatorTelegramId,
                        $"✅ Сессия поддержки {esc.Id} закрыта. Диалог возвращен AI-ассистенту.",
                        ct: ctx.RequestAborted);
                }
            }
            catch { }
        }

        await langfuse.LogEventAsync(
            traceId,
            "SupportDesk:Closed",
            "INFO",
            $"Сессия поддержки {req.EscalationId} закрыта со статусом {req.Status ?? "resolved"}.",
            metadata: new { escalation_id = req.EscalationId, session_id = esc?.SessionId, status = req.Status ?? "resolved" },
            ct: ctx.RequestAborted);

        return Results.Ok(new
        {
            status = req.Status ?? "resolved",
            escalation_id = req.EscalationId,
            session_id = esc?.SessionId,
            branch = "general",
            message = "Сессия с оператором успешно завершена, диалог возвращен в стандартный режим."
        });
    });

app.Run();

public record EscalateToOperatorRequest(
    Guid? SessionId = null,
    long? UserMasterId = null,
    long? UserTelegramId = null,
    long? OperatorTelegramId = null,
    string? Reason = null,
    string? Urgency = "normal");

public record CloseOperatorSessionRequest(
    Guid EscalationId,
    string? Status = "resolved");

public partial class Program { }
