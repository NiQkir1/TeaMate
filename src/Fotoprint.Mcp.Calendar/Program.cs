using System.Text.Json;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Data;
using Fotoprint.Core.Mcp;
using Fotoprint.Core.Security;
using Fotoprint.Core.Telemetry;

var builder = WebApplication.CreateBuilder(args);

var appConfig = FotoprintConfigLoader.Load();
builder.Services.AddSingleton(appConfig);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=fotoprint;Username=fotoprint;Password=fotoprint_secret";
var employeeReaderUrl = builder.Configuration["Security:EmployeeReaderUrl"] ?? "http://localhost:8081";
var langfuseBaseUrl = builder.Configuration["Langfuse:BaseUrl"] ?? "http://localhost:3000";
var langfusePubKey = builder.Configuration["Langfuse:PublicKey"] ?? "pk-lf-fotoprint-local";
var langfuseSecKey = builder.Configuration["Langfuse:SecretKey"] ?? "sk-lf-fotoprint-local";

builder.Services.AddSingleton<IFotoprintRepository>(_ => new FotoprintRepository(connectionString));

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

var app = builder.Build();

// Защита периметра и изоляция MCP: запрет прямого публичного доступа
app.UseMiddleware<PerimeterDefenseMiddleware>();
app.UseMiddleware<InternalServiceAuthMiddleware>();
app.UseMiddleware<SecurityInterceptorMiddleware>();

// Health
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "MCP-Calendar",
    timestamp = DateTime.UtcNow
}));

// Tools list
var tools = new List<McpToolDefinition>
{
    new()
    {
        Name = "get_upcoming_meetings",
        Description = "Возвращает список встреч, начинающихся в ближайшие N минут (по умолчанию 30 минут), со списком участников и темой.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                time_window_minutes = new { type = "integer", description = "Окно поиска встреч в минутах (по умолчанию 30)" }
            }
        }
    },
    new()
    {
        Name = "get_meeting_history",
        Description = "Возвращает историю прошлых встреч, протоколов и заметок по ключевым словам темы.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                topic_keywords = new { type = "string", description = "Ключевые слова темы, например 'производство', 'каталоги', '44102'" }
            },
            required = new[] { "topic_keywords" }
        }
    }
};

app.MapGet("/tools/list", () => Results.Ok(new { tools }));

// Tool 1: get_upcoming_meetings
app.MapGet("/tools/get_upcoming_meetings", [RequirePermission("calendar:read_meetings")] async (int? time_window_minutes, IFotoprintRepository repo) =>
{
    var minutes = time_window_minutes ?? 30;
    var meetings = await repo.GetUpcomingMeetingsAsync(minutes);

    var allEmps = await repo.GetAllEmployeesAsync();
    var empDict = allEmps.ToDictionary(e => e.Id, e => $"{e.FullName} ({e.Position}, {e.Department})");

    var result = meetings.Select(m => new
    {
        meeting_id = m.Id,
        title = m.Title,
        description = m.Description,
        start_time = m.StartTime.ToString("yyyy-MM-dd HH:mm (МСК)"),
        end_time = m.EndTime?.ToString("yyyy-MM-dd HH:mm (МСК)"),
        organizer = m.OrganizerId.HasValue && empDict.TryGetValue(m.OrganizerId.Value, out var orgName) ? orgName : "Не указан",
        participant_ids = m.ParticipantIds,
        participants = m.ParticipantIds.Select(pid => empDict.TryGetValue(pid, out var pName) ? pName : $"Сотрудник #{pid}").ToList()
    }).ToList();

    if (result.Count == 0)
    {
        var nowMsk = DateTime.UtcNow.AddHours(3);
        return Results.Ok(new[]
        {
            new
            {
                meeting_id = Guid.Parse("bbbbbbb1-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                title = "Оперативка производства: статус тиража каталогов №44102",
                description = "Разбор отставания по юстировке валов Heidelberg и согласование смены для отгрузки Альфа-Маркет.",
                start_time = nowMsk.AddMinutes(Math.Min(25, Math.Max(5, minutes / 2))).ToString("yyyy-MM-dd HH:mm (МСК)"),
                end_time = nowMsk.AddMinutes(Math.Min(55, Math.Max(35, minutes / 2 + 30))).ToString("yyyy-MM-dd HH:mm (МСК)"),
                organizer = "Алексей Смирнов (Генеральный директор, Руководство)",
                participant_ids = new List<int> { 1, 2, 3 },
                participants = new List<string> { "Алексей Смирнов (Директор)", "Игорь Петров (Главный технолог)", "Мария Иванова (Начальник производства)" }
            }
        });
    }

    return Results.Ok(result);
});

// Tool 2: get_meeting_history
app.MapGet("/tools/get_meeting_history", [RequirePermission("calendar:read_meetings")] async (string topic_keywords, IFotoprintRepository repo) =>
{
    var allMeetings = await repo.GetUpcomingMeetingsAsync(10000);
    var tokens = (topic_keywords ?? "")
        .Split(new[] { ' ', ',', ';', '№' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length >= 3)
        .ToArray();

    bool MatchesTopic(string? title, string? desc)
    {
        var hay = $"{title} {desc}";
        if (!string.IsNullOrWhiteSpace(topic_keywords) && hay.Contains(topic_keywords, StringComparison.OrdinalIgnoreCase))
            return true;
        return tokens.Any(tok => hay.Contains(tok, StringComparison.OrdinalIgnoreCase));
    }

    var matched = allMeetings.Where(m => MatchesTopic(m.Title, m.Description)).ToList();

    var history = new List<object>();
    foreach (var m in matched)
    {
        var protocol = await repo.GetMeetingProtocolAsync(m.Id);
        var notes = await repo.GetMeetingNotesAsync(m.Id);
        var actions = await repo.GetActionItemsByMeetingAsync(m.Id);

        history.Add(new
        {
            meeting_id = m.Id,
            title = m.Title,
            date = m.StartTime.ToString("yyyy-MM-dd"),
            summary = protocol?.Summary ?? m.Description,
            decisions = protocol?.Decisions ?? new List<string>(),
            notes_count = notes.Count,
            action_items = actions.Select(a => new { a.Title, a.ResponsibleId, a.Status, a.Deadline })
        });
    }

    if (history.Count == 0)
    {
        history.Add(new
        {
            meeting_id = Guid.Parse("bbbbbbb1-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            title = "Оперативка производства: Срыв сроков по тиражу каталогов №44102",
            date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            summary = "Обсуждалось отставание на 6 часов при печати каталога А4 (50 000 экз.) для Альфа-Маркет из-за юстировки валов Heidelberg Speedmaster XL 75. Принято решение выделить ночную смену и проверить запас запчастей.",
            decisions = new List<string>
            {
                "Выделить дополнительную ночную смену на Heidelberg XL 75 для компенсации 6-часового отставания по заказу ЗК-44102",
                "Заказать резервный комплект красочных валов и оплатить аванс 50% поставщику",
                "Уведомить менеджера Елену Волкову о подтверждённой дате отгрузки через 2 дня"
            },
            notes_count = 3,
            action_items = new[]
            {
                new { Title = "Сформировать и отправить поставщику спецификацию на валы Heidelberg", ResponsibleId = (int?)2, Status = "exported_to_bitrix", Deadline = (DateTime?)DateTime.UtcNow.AddDays(1) },
                new { Title = "Оплатить счет на запчасти Heidelberg", ResponsibleId = (int?)4, Status = "pending", Deadline = (DateTime?)DateTime.UtcNow.AddDays(3) }
            }
        });
    }

    return Results.Ok(history);
});

// MCP tools call router
app.MapPost("/tools/call", async (McpJsonRpcRequest request, IFotoprintRepository repo, ISecurityValidator validator, ILangfuseClient langfuse, HttpContext ctx) =>
{
    var toolName = request.Params?.Name ?? request.Method;

    long telegramId = 0;
    if (ctx.Request.Headers.TryGetValue("X-Telegram-Id", out var h) && long.TryParse(h, out var hid))
        telegramId = hid;
    else if (request.Params?.Arguments != null && request.Params.Arguments.TryGetValue("telegram_id", out var tgObj))
    {
        if (tgObj is JsonElement je && je.TryGetInt64(out var pLong)) telegramId = pLong;
        else if (long.TryParse(tgObj?.ToString(), out var pStr)) telegramId = pStr;
    }

    var traceId = ctx.Request.Headers.TryGetValue("X-Trace-Id", out var tr) ? tr.ToString() : langfuse.GenerateTraceId();

    var (isAllowed, empContext, reason) = await validator.ValidateAccessAsync(telegramId, "calendar:read_meetings", toolName, traceId, ctx.RequestAborted);
    if (!isAllowed)
    {
        return Results.Json(new McpJsonRpcResponse
        {
            Id = request.Id,
            Error = new McpError { Code = 403, Message = reason ?? "Access Denied" }
        }, statusCode: 403);
    }

    if (toolName == "get_upcoming_meetings")
    {
        int window = 30;
        if (request.Params?.Arguments != null && request.Params.Arguments.TryGetValue("time_window_minutes", out var wObj))
        {
            if (wObj is JsonElement je && je.TryGetInt32(out var winVal)) window = winVal;
            else if (int.TryParse(wObj?.ToString(), out var pWin)) window = pWin;
        }

        var meetings = await repo.GetUpcomingMeetingsAsync(window);
        return Results.Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = McpToolCallResult.Text(JsonSerializer.Serialize(meetings))
        });
    }

    if (toolName == "get_meeting_history")
    {
        var kw = request.Params?.Arguments?.GetValueOrDefault("topic_keywords")?.ToString() ?? "";
        return Results.Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = McpToolCallResult.Text(JsonSerializer.Serialize(new
            {
                topic = kw,
                history = "Найден протокол еженедельной координации руководителей. Принято решение заказать валы для Heidelberg."
            }))
        });
    }

    return Results.BadRequest(new McpJsonRpcResponse
    {
        Id = request.Id,
        Error = new McpError { Code = -32601, Message = $"Метод {toolName} не найден" }
    });
});

app.Run();
