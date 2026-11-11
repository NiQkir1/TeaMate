using System.Text.Json;
using Fotoprint.Core.Agents;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Models;
using Xunit;

namespace Fotoprint.Tests;

public class ContextSwitchDetectorTests
{
    private class MockGeminiClient : IGeminiClient
    {
        private readonly Func<string, GeminiGenerateResponse> _handler;

        public MockGeminiClient(Func<string, GeminiGenerateResponse> handler)
        {
            _handler = handler;
        }

        public Task<GeminiGenerateResponse> GenerateContentAsync(
            string prompt,
            string? systemInstruction = null,
            double? temperature = null,
            int? maxOutputTokens = null,
            string? traceId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(_handler(prompt));
        }

        public Task<GeminiTurnResponse> ChatTurnAsync(
            List<object> contents,
            object? tools = null,
            string? systemInstruction = null,
            double? temperature = null,
            int? maxOutputTokens = null,
            string? traceId = null,
            CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public GeminiCallStats GetStats() => new(1, 1, 60, 59, 1.6, DateTime.UtcNow, 0);
    }

    [Fact]
    public async Task EmptyHistory_Returns_NotSwitched_WithoutCallingLlm()
    {
        bool llmCalled = false;
        var mockGemini = new MockGeminiClient(_ =>
        {
            llmCalled = true;
            return new GeminiGenerateResponse(true, "{}", "STOP", 10, 10, 20, 1, null);
        });

        var config = new FotoprintConfig();
        var detector = new ContextSwitchDetector(mockGemini, config);

        var result = await detector.DetectSwitchAsync(new List<ChatMessage>(), "Привет, как дела?");

        Assert.False(result.IsSwitched);
        Assert.False(llmCalled);
        Assert.Contains("Первое сообщение", result.Reason);
    }

    [Fact]
    public async Task SameTopic_LlmReturns_False_ContextSwitchResult_IsFalse()
    {
        var mockGemini = new MockGeminiClient(prompt =>
        {
            var json = """
            {
              "context_switched": false,
              "suggested_branch": "doc_search",
              "reason": "Пользователь продолжает обсуждение регламента офсетной печати"
            }
            """;
            return new GeminiGenerateResponse(true, json, "STOP", 50, 25, 75, 1, null);
        });

        var config = new FotoprintConfig();
        var detector = new ContextSwitchDetector(mockGemini, config);

        var history = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Какая плотность краски для Heidelberg Speedmaster?" },
            new() { Role = "assistant", Content = "Согласно регламенту ТР-04, плотность 1.45." }
        };

        var result = await detector.DetectSwitchAsync(history, "А для мелованной бумаги 150г?");

        Assert.False(result.IsSwitched);
        Assert.Equal("doc_search", result.SuggestedBranch);
        Assert.Contains("продолжает обсуждение", result.Reason);
    }

    [Fact]
    public async Task TopicSwitched_LlmReturns_True_ContextSwitchResult_IsTrue()
    {
        var mockGemini = new MockGeminiClient(prompt =>
        {
            var json = """
            {
              "context_switched": true,
              "suggested_branch": "task_management",
              "reason": "Пользователь резко сменил тему с печати на постановку задачи в Битрикс24"
            }
            """;
            return new GeminiGenerateResponse(true, json, "STOP", 50, 25, 75, 1, null);
        });

        var config = new FotoprintConfig();
        var detector = new ContextSwitchDetector(mockGemini, config);

        var history = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Сколько брака на смене офсета?" },
            new() { Role = "assistant", Content = "Брак составил 1.2% за смену." }
        };

        var result = await detector.DetectSwitchAsync(history, "Поставь задачу Иванову подготовить акт списания до пятницы");

        Assert.True(result.IsSwitched);
        Assert.Equal("task_management", result.SuggestedBranch);
        Assert.Contains("сменил тему", result.Reason);
    }

    [Fact]
    public void ParseLlmResponse_Handles_MarkdownCodeBlocks_AndPlainTrueFalse()
    {
        // 1. В блоке ```json ... ```
        var markdownJson = "```json\n{\"context_switched\": true, \"suggested_branch\": \"executive_report\", \"reason\": \"Запрос сводки директора\"}\n```";
        var r1 = ContextSwitchDetector.ParseLlmResponse(markdownJson);
        Assert.True(r1.IsSwitched);
        Assert.Equal("executive_report", r1.SuggestedBranch);

        // 2. Прямой текстовый ответ
        var plainText = "context_switched: true";
        var r2 = ContextSwitchDetector.ParseLlmResponse(plainText);
        Assert.True(r2.IsSwitched);

        // 3. Прямой false
        var plainFalse = "{\"context_switched\": false}";
        var r3 = ContextSwitchDetector.ParseLlmResponse(plainFalse);
        Assert.False(r3.IsSwitched);

        // 4. JSON с окружающим текстом рассуждений
        var proseWithJson = "Анализирую историю...\n{\"context_switched\": true, \"suggested_branch\": \"general_chat\", \"reason\": \"Новая тема\"}\nГотово.";
        var r4 = ContextSwitchDetector.ParseLlmResponse(proseWithJson);
        Assert.True(r4.IsSwitched);
        Assert.Equal("general_chat", r4.SuggestedBranch);
        Assert.Equal("Новая тема", r4.Reason);
    }
}

