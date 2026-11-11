using System.Net;
using System.Text.Json;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Resilience;
using Fotoprint.Core.Telemetry;
using Xunit;

namespace Fotoprint.Tests;

public class GeminiClientTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? CapturedApiKeyHeader { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            if (request.Headers.TryGetValues("x-goog-api-key", out var values))
            {
                CapturedApiKeyHeader = values.FirstOrDefault();
            }

            if (request.Content != null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var responseJson = new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new[]
                            {
                                new { text = "Ответ модели Gemini для типографии Fotoprint." }
                            },
                            role = "model"
                        },
                        finishReason = "STOP"
                    }
                },
                usageMetadata = new
                {
                    promptTokenCount = 15,
                    candidatesTokenCount = 28,
                    totalTokenCount = 43
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(responseJson), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task GeminiClient_Tracks_Call_Count_And_Strict_15_RPM_Limit()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var config = new FotoprintConfig();
        config.Llm.Provider = "gemini";
        config.Llm.RateLimits.Rpm = 15;
        config.Llm.Proxy.ApiKeyEnvVar = "FAKE_GEMINI_KEY";

        Environment.SetEnvironmentVariable("FAKE_GEMINI_KEY", "test-gemini-key-12345");

        var lbOptions = new LoadBalancerOptions
        {
            MaxConcurrentRequests = 5,
            MinIntervalBetweenRequestsMs = 1,
            MaxRequestsPerMinute = 15
        };
        var loadBalancer = new LlmLoadBalancer(lbOptions);
        var rollbackManager = new RollbackManager();

        var client = new GeminiClient(httpClient, config, loadBalancer, rollbackManager);

        // Начальное состояние
        var initialStats = client.GetStats();
        Assert.Equal(0, initialStats.TotalCalls);
        Assert.Equal(15, initialStats.RpmLimit);
        Assert.Equal(15, initialStats.RemainingQuotaInCurrentMinute);
        Assert.Equal(0.0, initialStats.CurrentUsagePercent);

        // Делаем 4 вызова подряд
        for (int i = 0; i < 4; i++)
        {
            var res = await client.GenerateContentAsync($"Тестовый запрос #{i + 1}");
            Assert.True(res.IsSuccess);
            Assert.Equal("Ответ модели Gemini для типографии Fotoprint.", res.Text);
            Assert.Equal(43, res.TotalTokens);
        }

        // Проверяем статистику счетчика
        var statsAfter4 = client.GetStats();
        Assert.Equal(4, statsAfter4.TotalCalls);
        Assert.Equal(4, statsAfter4.CallsInLast60Seconds);
        Assert.Equal(11, statsAfter4.RemainingQuotaInCurrentMinute);
        Assert.Equal(26.7, statsAfter4.CurrentUsagePercent);

        // Проверяем, что заголовок x-goog-api-key был передан в запросе
        Assert.Equal("test-gemini-key-12345", handler.CapturedApiKeyHeader);
    }
}

