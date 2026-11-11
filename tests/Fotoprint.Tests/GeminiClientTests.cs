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

            if (request.Headers.Authorization != null)
            {
                CapturedApiKeyHeader = request.Headers.Authorization.Parameter;
            }

            if (request.Content != null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var responseJson = new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = "Ответ модели NVIDIA для типографии Fotoprint."
                        },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = 15,
                    completion_tokens = 28,
                    total_tokens = 43
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
            Assert.Equal("Ответ модели NVIDIA для типографии Fotoprint.", res.Text);
            Assert.Equal(43, res.TotalTokens);
        }

        // Проверяем статистику счетчика
        var statsAfter4 = client.GetStats();
        Assert.Equal(4, statsAfter4.TotalCalls);
        Assert.Equal(4, statsAfter4.CallsInLast60Seconds);
        Assert.Equal(11, statsAfter4.RemainingQuotaInCurrentMinute);
        Assert.Equal(26.7, statsAfter4.CurrentUsagePercent);

        // Проверяем, что токен Bearer был передан в запросе
        Assert.Equal("test-gemini-key-12345", handler.CapturedApiKeyHeader);
    }

    [Fact]
    public async Task NvidiaClient_Sends_OpenAi_Compatible_Format_And_Parses_Response()
    {
        var openAiHandler = new FakeOpenAiMessageHandler();
        var httpClient = new HttpClient(openAiHandler);

        var config = new FotoprintConfig();
        config.Llm.Provider = "nvidia";
        config.Llm.Proxy.BaseUrl = "https://integrate.api.nvidia.com/v1";
        config.Llm.Proxy.ApiKeyEnvVar = "NVIDIA_TEST_KEY";
        config.Llm.Models.ChatModel = "nvidia/nemotron-3.5-super-vl-120b-a12b";

        Environment.SetEnvironmentVariable("NVIDIA_TEST_KEY", "nvapi-test-token-123");

        var lb = new LlmLoadBalancer(new LoadBalancerOptions { MaxConcurrentRequests = 5, MaxRequestsPerMinute = 60, MinIntervalBetweenRequestsMs = 1 });
        var rollback = new RollbackManager();

        var client = new GeminiClient(httpClient, config, lb, rollback);

        var res = await client.GenerateContentAsync("Тестовый запрос в модель NVIDIA");

        Assert.True(res.IsSuccess);
        Assert.Equal("Ответ от модели NVIDIA Nemotron", res.Text);
        Assert.Equal(50, res.TotalTokens);
        Assert.Equal("Bearer nvapi-test-token-123", openAiHandler.CapturedAuthHeader);
        Assert.Contains("chat/completions", openAiHandler.CapturedRequestUri);
        Assert.Contains("nvidia/nemotron-3.5-super-vl-120b-a12b", openAiHandler.CapturedRequestBody);
    }

    private class FakeOpenAiMessageHandler : HttpMessageHandler
    {
        public string? CapturedAuthHeader { get; private set; }
        public string? CapturedRequestUri { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestUri = request.RequestUri?.ToString();
            CapturedAuthHeader = request.Headers.Authorization?.ToString();
            if (request.Content != null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var responseJson = new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = "Ответ от модели NVIDIA Nemotron"
                        },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = 20,
                    completion_tokens = 30,
                    total_tokens = 50
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(responseJson), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}

