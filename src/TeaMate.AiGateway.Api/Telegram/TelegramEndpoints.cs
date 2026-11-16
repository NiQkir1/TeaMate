using System.Text.Json;
using TeaMate.Core.Configuration;
using TeaMate.Core.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace TeaMate.AiGateway.Api.Telegram;

public sealed record TelegramSetWebhookPayload(
    string? Url = null,
    string? SecretToken = null,
    bool DropPendingUpdates = false);

public static class TelegramEndpoints
{
    public static IServiceCollection AddTelegramIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var botOptions = new TelegramBotOptions
        {
            BotToken = configuration["Telegram:BotToken"] ?? string.Empty,
            WebhookSecret = configuration["Telegram:WebhookSecret"],
            CloudflaredMetricsUrl = configuration["Telegram:CloudflaredMetricsUrl"],
            ApiBaseUrl = configuration["Telegram:ApiBaseUrl"] ?? "https://api.telegram.org"
        };

        services.AddSingleton(botOptions);
        services.AddSingleton(new TelegramUpdateDeduplicator(TimeSpan.FromMinutes(10)));
        services.AddSingleton<CloudflareWebhookState>();
        services.AddSingleton<TelegramWebhookProcessor>();

        services.AddHttpClient<ITelegramBotClient, TelegramBotClient>(client =>
        {
            client.BaseAddress = new Uri(botOptions.ApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Suppress HttpClient Info logs for ITelegramBotClient so the bot token in the request URL is never logged
        services.AddLogging(logging =>
        {
            logging.AddFilter("System.Net.Http.HttpClient.ITelegramBotClient", LogLevel.Warning);
        });

        services.AddHttpClient("CloudflaredMetrics", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddSingleton<CloudflareWebhookRegistrarService>();
        services.AddHostedService(sp => sp.GetRequiredService<CloudflareWebhookRegistrarService>());

        return services;
    }

    public static IEndpointRouteBuilder MapTelegramEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/telegram/webhook", async (
            [FromBody] JsonElement rawUpdate,
            HttpContext httpContext,
            TelegramWebhookProcessor processor) =>
        {
            return await processor.HandleUpdateAsync(httpContext, rawUpdate);
        });

        endpoints.MapPost("/api/telegram/set-webhook", async (
            HttpContext httpContext,
            ITelegramBotClient telegramBot,
            TelegramBotOptions options,
            CloudflareWebhookState state,
            CloudflareWebhookRegistrarService registrar,
            TeaMateConfig config,
            CancellationToken ct) =>
        {
            var internalKey = httpContext.Request.Headers["X-Internal-Service-Key"].FirstOrDefault();
            if (!TelegramWebhookSecurity.IsSecretValid(config.Security.InternalServiceSecretKey, internalKey))
            {
                return Results.Unauthorized();
            }

            if (!options.IsConfigured)
            {
                return Results.BadRequest(new { error = "Telegram bot token is not configured." });
            }

            TelegramSetWebhookPayload? body = null;
            if (httpContext.Request.ContentLength is > 0 || httpContext.Request.HasJsonContentType())
            {
                try
                {
                    body = await JsonSerializer.DeserializeAsync<TelegramSetWebhookPayload>(
                        httpContext.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        ct);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = $"Invalid JSON payload: {ex.Message}" });
                }
            }

            var targetUrl = body?.Url;
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                targetUrl = await registrar.TryFetchTunnelWebhookUrlAsync(ct);
            }

            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                return Results.BadRequest(new
                {
                    error = "Webhook URL was not provided and could not be resolved from cloudflared."
                });
            }

            var secretToSet = body?.SecretToken ?? options.WebhookSecret;
            var dropPending = body?.DropPendingUpdates ?? false;

            var result = await telegramBot.SetWebhookAsync(targetUrl, secretToSet, dropPending, ct);
            if (!result.Ok)
            {
                state.RecordError(result.Description ?? "setWebhook failed", targetUrl);
                return Results.BadRequest(new
                {
                    ok = false,
                    error_code = result.ErrorCode,
                    description = result.Description,
                    url = targetUrl
                });
            }

            state.RecordSync(targetUrl, DateTimeOffset.UtcNow);
            return Results.Ok(new
            {
                ok = true,
                url = targetUrl,
                secret_token_configured = !string.IsNullOrWhiteSpace(secretToSet)
            });
        });

        return endpoints;
    }
}
