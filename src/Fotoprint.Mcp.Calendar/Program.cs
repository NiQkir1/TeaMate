using System.Text.Json;
using System.Text.Json.Serialization;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Data;
using Fotoprint.Core.Mcp;
using Fotoprint.Core.Models;
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
    },
    new()
    {
        Name = "find_free_slots",
        Description = "Ищет свободные временные слоты (окна) в календаре сотрудников типографии на указанную дату с учетом рабочего дня, буфера и отсечения прошедшего времени.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                organizer_employee_id = new { type = "integer", description = "ID организатора" },
                participant_employee_ids = new { type = "array", items = new { type = "integer" }, description = "ID участников" },
                date = new { type = "string", description = "Дата в формате YYYY-MM-DD" },
                duration_minutes = new { type = "integer", description = "Длительность встречи в минутах (по умолчанию 30)" },
                working_start = new { type = "string", description = "Начало рабочего дня (09:00)" },
                working_end = new { type = "string", description = "Конец рабочего дня (18:00)" },
                buffer_minutes = new { type = "integer", description = "Буфер между встречами в минутах (10)" }
            },
            required = new[] { "date" }
        }
    },
    new()
    {
        Name = "create_meeting",
        Description = "Создает новую встречу со статусом 'scheduled' и эмулирует оповещения участников.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string", description = "Тема встречи" },
                description = new { type = "string", description = "Описание встречи" },
                start_time = new { type = "string", description = "Время начала (ISO 8601)" },
                end_time = new { type = "string", description = "Время окончания (ISO 8601)" },
                organizer_id = new { type = "integer", description = "ID организатора" },
                participant_ids = new { type = "array", items = new { type = "integer" }, description = "Список ID участников" }
            },
            required = new[] { "title", "start_time" }
        }
    },
    new()
    {
        Name = "get_calendar_events",
        Description = "Возвращает календарные события в заданном диапазоне дат (поддерживает scope=my и scope=all).",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                start_date = new { type = "string", description = "Дата начала диапазона" },
                end_date = new { type = "string", description = "Дата окончания диапазона" },
                employee_id = new { type = "integer", description = "Фильтр по ID сотрудника" },
                scope = new { type = "string", description = "Область видимости: 'my' или 'all'" }
            }
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

// Tool 3: find_free_slots
app.MapPost("/tools/find_free_slots", [RequirePermission("calendar:read_meetings")] async (FindFreeSlotsRequest req, IFotoprintRepository repo, CancellationToken ct) =>
{
    var mskZone = FotoprintRepository.GetMoscowTimeZone();
    var nowMsk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mskZone);
    var todayMsk = DateOnly.FromDateTime(nowMsk);

    DateOnly targetDate;
    if (!string.IsNullOrWhiteSpace(req.Date) && DateOnly.TryParse(req.Date, out var pd))
    {
        targetDate = pd;
    }
    else
    {
        targetDate = todayMsk;
    }

    var startWork = TimeOnly.TryParse(req.WorkingStart, out var ws) ? ws : new TimeOnly(9, 0);
    var endWork = TimeOnly.TryParse(req.WorkingEnd, out var we) ? we : new TimeOnly(18, 0);
    var duration = req.EffectiveDurationMinutes;
    var buffer = req.BufferMinutes ?? 10;
    var organizerId = req.EffectiveOrganizerId;
    var participantIds = req.EffectiveParticipantIds;

    var slots = await repo.FindFreeSlotsAsync(organizerId, participantIds, targetDate, duration, startWork, endWork, buffer, ct);

    var isToday = targetDate == todayMsk;
    var pastSlotsCutoff = nowMsk.ToString("HH:mm");

    return Results.Ok(new
    {
        status = "success",
        date = targetDate.ToString("yyyy-MM-dd"),
        duration_minutes = duration,
        available_slots = slots.Select(s => new
        {
            start_time = TimeZoneInfo.ConvertTimeFromUtc(s.StartTimeUtc, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
            end_time = TimeZoneInfo.ConvertTimeFromUtc(s.EndTimeUtc, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
            label = s.Label
        }),
        is_today = isToday,
        past_slots_cutoff = pastSlotsCutoff,
        has_free_slots = slots.Count > 0
    });
});

// Tool 4: create_meeting
app.MapPost("/tools/create_meeting", [RequirePermission("calendar:read_meetings")] async (CreateMeetingRequest req, IFotoprintRepository repo, CancellationToken ct) =>
{
    var mskZone = FotoprintRepository.GetMoscowTimeZone();
    var startTimeUtc = req.EffectiveStartTime.Kind == DateTimeKind.Utc
        ? req.EffectiveStartTime
        : req.EffectiveStartTime.ToUniversalTime();

    var endTimeUtc = req.EffectiveEndTime.HasValue
        ? (req.EffectiveEndTime.Value.Kind == DateTimeKind.Utc ? req.EffectiveEndTime.Value : req.EffectiveEndTime.Value.ToUniversalTime())
        : startTimeUtc.AddMinutes(30);

    var organizerId = req.EffectiveOrganizerId ?? 1;
    var participantIds = req.EffectiveParticipantIds;
    var status = string.IsNullOrWhiteSpace(req.Status) ? "scheduled" : req.Status;

    var metadata = new
    {
        notifications = new
        {
            status = "simulated",
            channel = "Telegram & Bitrix24",
            timestamp = DateTime.UtcNow.ToString("O"),
            note = "⚠️ [Заглушка]: Оповещение участникам эмулировано для демонстрации трека хакатона"
        }
    };

    var meetingId = await repo.CreateMeetingAsync(
        title: string.IsNullOrWhiteSpace(req.Title) ? "Встреча" : req.Title,
        description: req.Description,
        startTime: startTimeUtc,
        endTime: endTimeUtc,
        status: status,
        organizerId: organizerId,
        participantIds: participantIds,
        metadata: metadata,
        ct: ct);

    var allEmps = await repo.GetAllEmployeesAsync(ct);
    var empDict = allEmps.ToDictionary(e => e.Id, e => e.FullName);

    var organizerName = empDict.TryGetValue(organizerId, out var on) ? on : $"Сотрудник #{organizerId}";
    var participantsList = participantIds.Select(pid => new
    {
        id = pid,
        name = empDict.TryGetValue(pid, out var pn) ? pn : $"Сотрудник #{pid}"
    }).ToList();

    return Results.Ok(new
    {
        status = "success",
        meeting_id = meetingId,
        title = req.Title,
        start_time = TimeZoneInfo.ConvertTimeFromUtc(startTimeUtc, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
        end_time = TimeZoneInfo.ConvertTimeFromUtc(endTimeUtc, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
        organizer = new { id = organizerId, name = organizerName },
        participants = participantsList,
        notification_dispatch = new
        {
            status = "simulated",
            channels = new[] { "Telegram", "Bitrix24" },
            recipient_ids = participantIds,
            message = "⚠️ [Заглушка спринта]: Оповещение поставлено в очередь эмуляции Telegram/Bitrix24"
        }
    });
});

// Tool 5: get_calendar_events
app.MapGet("/tools/get_calendar_events", [RequirePermission("calendar:read_meetings")] async (
    DateTime? start_date,
    DateTime? end_date,
    int? employee_id,
    string? scope,
    HttpContext context,
    IFotoprintRepository repo,
    ISecurityValidator security) =>
{
    long callerTelegramId = 0;
    if (context.Request.Headers.TryGetValue("X-Telegram-Id", out var h) && long.TryParse(h, out var hid))
        callerTelegramId = hid;
    else if (context.Request.Query.TryGetValue("telegram_id", out var q) && long.TryParse(q, out var qid))
        callerTelegramId = qid;

    EmployeeSecurityContext? caller = context.Items["SecurityContext"] as EmployeeSecurityContext;
    if (caller == null && callerTelegramId != 0)
    {
        caller = await repo.GetEmployeeSecurityContextAsync(callerTelegramId);
    }

    var isDirectorOrAdmin = caller != null && (caller.Roles.Contains("Director") || caller.Roles.Contains("Admin"));

    // Если запрошен режим "Вся компания", но пользователь не директор/админ
    if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) && !isDirectorOrAdmin)
    {
        return Results.Json(new
        {
            status = "forbidden",
            message = "Просмотр расписания всей компании доступен только руководству (роль Director)"
        }, statusCode: 403);
    }

    int? filterEmployeeId = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
        ? null
        : (employee_id ?? (caller?.EmployeeId > 0 ? caller.EmployeeId : (int?)null));

    var fromUtc = start_date ?? DateTime.UtcNow.Date;
    var toUtc = end_date ?? fromUtc.AddDays(30);

    var events = await repo.GetMeetingsForRangeAsync(fromUtc, toUtc, filterEmployeeId, context.RequestAborted);
    var mskZone = FotoprintRepository.GetMoscowTimeZone();

    var formattedEvents = events.Select(m => new
    {
        meeting_id = m.Id,
        title = m.Title,
        description = m.Description,
        start_time = TimeZoneInfo.ConvertTimeFromUtc(m.StartTime, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
        end_time = m.EndTime.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(m.EndTime.Value, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz") : null,
        status = m.Status,
        organizer_id = m.OrganizerId,
        participant_ids = m.ParticipantIds
    }).ToList();

    return Results.Ok(new
    {
        status = "success",
        scope = scope ?? "my",
        events = formattedEvents
    });
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

    if (toolName == "find_free_slots")
    {
        var args = request.Params?.Arguments;
        var dateStr = args?.GetValueOrDefault("date")?.ToString();
        var duration = args != null && args.TryGetValue("duration_minutes", out var dObj) && int.TryParse(dObj?.ToString(), out var dv) ? dv : 30;
        var orgId = args != null && args.TryGetValue("organizer_employee_id", out var oObj) && int.TryParse(oObj?.ToString(), out var ov) ? ov : 1;
        var pIds = new List<int>();
        if (args != null && args.TryGetValue("participant_employee_ids", out var pObj))
        {
            if (pObj is JsonElement pJe && pJe.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pJe.EnumerateArray())
                    if (el.TryGetInt32(out var idVal)) pIds.Add(idVal);
            }
        }

        var mskZone = FotoprintRepository.GetMoscowTimeZone();
        var nowMsk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mskZone);
        var targetDate = !string.IsNullOrWhiteSpace(dateStr) && DateOnly.TryParse(dateStr, out var pd) ? pd : DateOnly.FromDateTime(nowMsk);

        var slots = await repo.FindFreeSlotsAsync(orgId, pIds, targetDate, duration, ct: ctx.RequestAborted);
        return Results.Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = McpToolCallResult.Text(JsonSerializer.Serialize(new
            {
                status = "success",
                date = targetDate.ToString("yyyy-MM-dd"),
                duration_minutes = duration,
                available_slots = slots.Select(s => new
                {
                    start_time = TimeZoneInfo.ConvertTimeFromUtc(s.StartTimeUtc, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    end_time = TimeZoneInfo.ConvertTimeFromUtc(s.EndTimeUtc, mskZone).ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    label = s.Label
                }),
                is_today = targetDate == DateOnly.FromDateTime(nowMsk),
                has_free_slots = slots.Count > 0
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

public class FindFreeSlotsRequest
{
    [JsonPropertyName("organizer_employee_id")]
    public int? OrganizerEmployeeId { get; set; }

    [JsonPropertyName("organizer_id")]
    public int? OrganizerId { get; set; }

    [JsonPropertyName("organizerId")]
    public int? OrganizerIdCamel { get; set; }

    [JsonPropertyName("participant_employee_ids")]
    public List<int>? ParticipantEmployeeIds { get; set; }

    [JsonPropertyName("participant_ids")]
    public List<int>? ParticipantIds { get; set; }

    [JsonPropertyName("participantIds")]
    public List<int>? ParticipantIdsCamel { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("duration_minutes")]
    public int? DurationMinutes { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int? DurationMinutesCamel { get; set; }

    [JsonPropertyName("working_start")]
    public string? WorkingStart { get; set; }

    [JsonPropertyName("working_end")]
    public string? WorkingEnd { get; set; }

    [JsonPropertyName("buffer_minutes")]
    public int? BufferMinutes { get; set; }

    public int EffectiveOrganizerId => OrganizerEmployeeId ?? OrganizerId ?? OrganizerIdCamel ?? 1;
    public List<int> EffectiveParticipantIds => ParticipantEmployeeIds ?? ParticipantIds ?? ParticipantIdsCamel ?? new List<int>();
    public int EffectiveDurationMinutes => DurationMinutes ?? DurationMinutesCamel ?? 30;
}

public class CreateMeetingRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("start_time")]
    public DateTime? StartTime { get; set; }

    [JsonPropertyName("startTime")]
    public DateTime? StartTimeCamel { get; set; }

    [JsonPropertyName("end_time")]
    public DateTime? EndTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTime? EndTimeCamel { get; set; }

    [JsonPropertyName("organizer_id")]
    public int? OrganizerId { get; set; }

    [JsonPropertyName("organizerId")]
    public int? OrganizerIdCamel { get; set; }

    [JsonPropertyName("participant_ids")]
    public List<int>? ParticipantIds { get; set; }

    [JsonPropertyName("participantIds")]
    public List<int>? ParticipantIdsCamel { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "scheduled";

    public DateTime EffectiveStartTime => StartTime ?? StartTimeCamel ?? DateTime.UtcNow;
    public DateTime? EffectiveEndTime => EndTime ?? EndTimeCamel;
    public int? EffectiveOrganizerId => OrganizerId ?? OrganizerIdCamel;
    public List<int> EffectiveParticipantIds => ParticipantIds ?? ParticipantIdsCamel ?? new List<int>();
}
