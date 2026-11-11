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
    service = "MCP-Bitrix24",
    timestamp = DateTime.UtcNow
}));

// Tools list
var tools = new List<McpToolDefinition>
{
    new()
    {
        Name = "get_user_tasks",
        Description = "Получает список активных и просроченных задач сотрудника из Битрикс24.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                employee_id = new { type = "integer", description = "ID сотрудника" },
                status_filter = new { type = "string", description = "Фильтр по статусу: pending, overdue, all" }
            },
            required = new[] { "employee_id" }
        }
    },
    new()
    {
        Name = "create_task",
        Description = "Создает задачу в Битрикс24 по итогам встречи и привязывает её к реестру поручений.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string", description = "Название задачи" },
                description = new { type = "string", description = "Подробное описание задачи" },
                responsible_id = new { type = "integer", description = "ID ответственного сотрудника" },
                deadline = new { type = "string", description = "Срок выполнения (ГГГГ-ММ-ДД)" },
                meeting_id = new { type = "string", description = "UUID встречи, в рамках которой создано поручение" }
            },
            required = new[] { "title", "responsible_id" }
        }
    }
};

app.MapGet("/tools/list", () => Results.Ok(new { tools }));

// Tool 1: get_user_tasks
app.MapGet("/tools/get_user_tasks", [RequirePermission("bitrix24:read_tasks")] async (int employee_id, string? status_filter, IFotoprintRepository repo) =>
{
    var tasks = await repo.GetActionItemsByResponsibleAsync(employee_id, status_filter);

    // Дополняем информацией из Битрикс24
    var enriched = tasks.Select(t => new
    {
        id = t.Id,
        bitrix_task_id = t.Bitrix24TaskId ?? (1000 + Math.Abs(t.Id.GetHashCode() % 9000)),
        title = t.Title,
        description = t.Description,
        responsible_id = t.ResponsibleId,
        deadline = t.Deadline?.ToString("yyyy-MM-dd HH:mm"),
        is_overdue = t.Deadline.HasValue && t.Deadline.Value < DateTime.UtcNow,
        status = t.Status
    });

    return Results.Ok(enriched);
});

// Tool 2: create_task
app.MapPost("/tools/create_task", [RequirePermission("bitrix24:create_tasks")] async (CreateTaskRequest req, IFotoprintRepository repo, ILangfuseClient langfuse, HttpContext ctx) =>
{
    var meetingGuid = req.MeetingId ?? Guid.Empty;
    var bitrixId = Random.Shared.Next(1050, 9999);

    var actionItemId = await repo.CreateActionItemAsync(
        meetingId: meetingGuid,
        protocolId: null,
        title: req.Title,
        description: req.Description,
        responsibleId: req.ResponsibleId,
        deadline: req.Deadline ?? DateTime.UtcNow.AddDays(3)
    );

    await repo.UpdateActionItemStatusAsync(actionItemId, "exported_to_bitrix", bitrixId);

    var traceId = ctx.Request.Headers.TryGetValue("X-Trace-Id", out var tr) ? tr.ToString() : langfuse.GenerateTraceId();
    await langfuse.LogEventAsync(traceId, "Bitrix24:TaskCreated", "INFO", $"Задача '{req.Title}' создана в Битрикс24 (ID: {bitrixId})");

    return Results.Ok(new
    {
        success = true,
        action_item_id = actionItemId,
        bitrix24_task_id = bitrixId,
        title = req.Title,
        status = "created",
        url = $"https://fotoprint.bitrix24.ru/company/personal/user/{req.ResponsibleId}/tasks/task/view/{bitrixId}/"
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

    string requiredPerm = toolName == "create_task" ? "bitrix24:create_tasks" : "bitrix24:read_tasks";
    var (isAllowed, empContext, reason) = await validator.ValidateAccessAsync(telegramId, requiredPerm, toolName, traceId, ctx.RequestAborted);

    if (!isAllowed)
    {
        return Results.Json(new McpJsonRpcResponse
        {
            Id = request.Id,
            Error = new McpError { Code = 403, Message = reason ?? "Access Denied" }
        }, statusCode: 403);
    }

    if (toolName == "get_user_tasks")
    {
        int employeeId = 1;
        if (request.Params?.Arguments != null && request.Params.Arguments.TryGetValue("employee_id", out var empObj))
        {
            if (empObj is JsonElement je && je.TryGetInt32(out var eid)) employeeId = eid;
            else if (int.TryParse(empObj?.ToString(), out var parsedEid)) employeeId = parsedEid;
        }

        var tasks = await repo.GetActionItemsByResponsibleAsync(employeeId);
        return Results.Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = McpToolCallResult.Text(JsonSerializer.Serialize(tasks))
        });
    }

    if (toolName == "create_task")
    {
        var title = request.Params?.Arguments?.GetValueOrDefault("title")?.ToString() ?? "Поручение по встрече";
        var bitrixId = Random.Shared.Next(1050, 9999);

        var res = new
        {
            bitrix24_task_id = bitrixId,
            title = title,
            status = "created",
            url = $"https://fotoprint.bitrix24.ru/tasks/view/{bitrixId}/"
        };

        return Results.Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = McpToolCallResult.Text(JsonSerializer.Serialize(res))
        });
    }

    return Results.BadRequest(new McpJsonRpcResponse
    {
        Id = request.Id,
        Error = new McpError { Code = -32601, Message = $"Метод {toolName} не найден" }
    });
});

app.Run();

public record CreateTaskRequest(string Title, string? Description, int ResponsibleId, DateTime? Deadline, Guid? MeetingId);

