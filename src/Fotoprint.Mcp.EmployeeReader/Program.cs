using System.Text.Json;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Data;
using Fotoprint.Core.Mcp;
using Fotoprint.Core.Security;
using Fotoprint.Core.Telemetry;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

var appConfig = FotoprintConfigLoader.Load();
builder.Services.AddSingleton(appConfig);

// Configuration
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=fotoprint;Username=fotoprint;Password=fotoprint_secret";

var langfuseBaseUrl = builder.Configuration["Langfuse:BaseUrl"] ?? "http://localhost:3000";
var langfusePubKey = builder.Configuration["Langfuse:PublicKey"] ?? "pk-lf-fotoprint-local";
var langfuseSecKey = builder.Configuration["Langfuse:SecretKey"] ?? "sk-lf-fotoprint-local";

// Services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IFotoprintRepository>(_ => new FotoprintRepository(connectionString));

builder.Services.AddHttpClient<ILangfuseClient, LangfuseClient>();
builder.Services.AddSingleton(new LangfuseOptions
{
    BaseUrl = langfuseBaseUrl,
    PublicKey = langfusePubKey,
    SecretKey = langfuseSecKey,
    Enabled = true
});

var app = builder.Build();

// Защита периметра и изоляция MCP: запрет прямого публичного доступа
app.UseMiddleware<PerimeterDefenseMiddleware>();
app.UseMiddleware<InternalServiceAuthMiddleware>();

// Endpoints
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "MCP-EmployeeReader",
    timestamp = DateTime.UtcNow
}));

// Tools list
var tools = new List<McpToolDefinition>
{
    new()
    {
        Name = "get_employee_security_context",
        Description = "Получает паспорт безопасности сотрудника (роли, права, access_groups, отдел) по его Telegram ID.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                telegram_id = new { type = "integer", description = "Уникальный идентификатор пользователя в Telegram" }
            },
            required = new[] { "telegram_id" }
        }
    },
    new()
    {
        Name = "resolve_master_id",
        Description = "Находит уникальный цифровой MasterId сотрудника по его идентификатору в Telegram или другом мессенджере.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                messenger = new { type = "string", description = "Тип мессенджера (telegram, whatsapp, web)" },
                messenger_id = new { type = "string", description = "Идентификатор пользователя в мессенджере (например telegram_id)" }
            },
            required = new[] { "messenger_id" }
        }
    }
};

app.MapGet("/tools/list", () => Results.Ok(new { tools }));

// Tool execution logic
async Task<IResult> HandleGetSecurityContextAsync(long telegramId, IFotoprintRepository repo, IMemoryCache cache, ILangfuseClient langfuse)
{
    var cacheKey = $"sec_ctx_tg_{telegramId}";

    if (cache.TryGetValue(cacheKey, out EmployeeSecurityContext? cachedContext) && cachedContext != null)
    {
        return Results.Ok(cachedContext);
    }

    var context = await repo.GetEmployeeSecurityContextAsync(telegramId);
    if (context == null)
    {
        return Results.NotFound(new
        {
            error = "EmployeeNotFound",
            message = $"Сотрудник с Telegram ID {telegramId} не найден в базе данных.",
            telegram_id = telegramId
        });
    }

    // Кэшируем на 10 минут со скользящим сроком
    cache.Set(cacheKey, context, new MemoryCacheEntryOptions
    {
        SlidingExpiration = TimeSpan.FromMinutes(10),
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
    });

    return Results.Ok(context);
}

// Direct GET
app.MapGet("/tools/get_employee_security_context", async (long telegram_id, IFotoprintRepository repo, IMemoryCache cache, ILangfuseClient langfuse) =>
{
    return await HandleGetSecurityContextAsync(telegram_id, repo, cache, langfuse);
});

// Direct GET resolve_master_id
app.MapGet("/tools/resolve_master_id", async (string? messenger, string? messenger_id, long? telegram_id, IFotoprintRepository repo, IMemoryCache cache) =>
{
    var mType = !string.IsNullOrWhiteSpace(messenger) ? messenger : "telegram";
    var mId = !string.IsNullOrWhiteSpace(messenger_id) ? messenger_id : telegram_id?.ToString() ?? "";
    if (string.IsNullOrWhiteSpace(mId))
    {
        return Results.BadRequest(new { error = "MissingIdentifier", message = "Не указан messenger_id или telegram_id" });
    }

    var cacheKey = $"master_id_{mType}_{mId}";
    if (cache.TryGetValue(cacheKey, out long cachedMasterId))
    {
        return Results.Ok(new { master_id = cachedMasterId, messenger = mType, messenger_id = mId, found = true });
    }

    var masterId = await repo.ResolveMasterIdByMessengerAsync(mType, mId);
    if (!masterId.HasValue)
    {
        return Results.NotFound(new { error = "EmployeeNotFound", message = $"Сотрудник для {mType}:{mId} не найден", found = false });
    }

    cache.Set(cacheKey, masterId.Value, TimeSpan.FromMinutes(15));
    return Results.Ok(new { master_id = masterId.Value, messenger = mType, messenger_id = mId, found = true });
});

app.Run();
