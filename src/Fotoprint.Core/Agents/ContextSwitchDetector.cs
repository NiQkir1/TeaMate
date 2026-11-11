using System.Text.Json;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Models;
using Fotoprint.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace Fotoprint.Core.Agents;

/// <summary>
/// Результат работы ContextSwitchDetector: сменился ли контекст диалога (true/false)
/// и рекомендуемая ветка обработки запроса для n8n / MCP.
/// </summary>
public record ContextSwitchResult(
    bool IsSwitched,
    string SuggestedBranch,
    string Reason
);

/// <summary>
/// Интерфейс детектора смены контекста. Вызывается на 2-м этапе шлюза ПЕРЕД передачей в n8n.
/// </summary>
public interface IContextSwitchDetector
{
    Task<ContextSwitchResult> DetectSwitchAsync(
        IReadOnlyList<ChatMessage> previousSessionMessages,
        string currentMessage,
        string? traceId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Реализация детектора смены контекста через вызов Gemini LLM.
/// Передает предыдущую историю сессии и текущее сообщение, получает строго true/false в JSON,
/// парсит ответ и определяет, сменился ли контекст перед передачей в n8n.
/// </summary>
public class ContextSwitchDetector : IContextSwitchDetector
{
    private readonly IGeminiClient _gemini;
    private readonly FotoprintConfig _config;
    private readonly ILangfuseClient? _langfuse;
    private readonly ILogger<ContextSwitchDetector>? _logger;

    public ContextSwitchDetector(
        IGeminiClient gemini,
        FotoprintConfig config,
        ILangfuseClient? langfuse = null,
        ILogger<ContextSwitchDetector>? logger = null)
    {
        _gemini = gemini;
        _config = config;
        _langfuse = langfuse;
        _logger = logger;
    }

    public async Task<ContextSwitchResult> DetectSwitchAsync(
        IReadOnlyList<ChatMessage> previousSessionMessages,
        string currentMessage,
        string? traceId = null,
        CancellationToken ct = default)
    {
        if (!_config.ContextSwitch.Enabled)
        {
            return new ContextSwitchResult(false, "doc_search", "Context switch detector disabled in configuration");
        }

        // Фильтруем историю: берем только незамороженные непустые сообщения
        var activeHistory = previousSessionMessages?
            .Where(m => !m.IsFrozen && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(_config.ContextSwitch.MaxHistoryMessages)
            .ToList();

        // Если истории нет - это начало диалога, смены контекста нет
        if (activeHistory == null || activeHistory.Count == 0)
        {
            return new ContextSwitchResult(false, "general_chat", "Первое сообщение в сессии");
        }

        // Форматируем предыдущую сессию
        var historyContext = string.Join("\n", activeHistory.Select(m => $"[{m.Role.ToUpperInvariant()}]: {m.Content}"));

        var prompt = $$"""
            ИСТОРИЯ ПРЕДЫДУЩЕЙ СЕССИИ:
            {{historyContext}}

            ТЕКУЩЕЕ НОВОЕ СООБЩЕНИЕ:
            "{{currentMessage}}"

            Вопрос: сменил ли пользователь контекст/тему диалога по отношению к предыдущей сессии?
            Ответь строго в формате JSON:
            {
              "context_switched": true,
              "suggested_branch": "meeting_prep" | "live_meeting_notes" | "meeting_summary" | "task_management" | "executive_report" | "doc_search" | "general_chat",
              "reason": "краткая причина"
            }
            """;

        var systemInstruction = """
            Ты — высокоточный классификатор смены контекста диалогов типографии Fotoprint.
            Твоя цель — определить, сменил ли пользователь тему разговора по отношению к предыдущей сессии.
            Если пользователь переключился на совершенно другую тему или задачу (например, обсуждали брак и технологию печати, а он попросил поставить задачу в Битрикс или назначить встречу) — context_switched = true.
            Если сообщение развивает, уточняет или продолжает предыдущий диалог — context_switched = false.
            Всегда отвечай максимально кратко и строго валидным JSON без markdown-обертки.
            """;

        try
        {
            var startTime = DateTime.UtcNow;
            var response = await _gemini.GenerateContentAsync(
                prompt: prompt,
                systemInstruction: systemInstruction,
                temperature: _config.ContextSwitch.Temperature,
                maxOutputTokens: _config.ContextSwitch.MaxOutputTokens,
                traceId: traceId,
                ct: ct);
            var endTime = DateTime.UtcNow;

            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Text))
            {
                return new ContextSwitchResult(false, "doc_search", $"Gemini failed: {response.ErrorMessage}");
            }

            var result = ParseLlmResponse(response.Text);

            if (_langfuse != null && !string.IsNullOrEmpty(traceId))
            {
                // Фиксация поколения токенов Gen:ContextSwitchDetector и FinOps
                await _langfuse.CreateGenerationAsync(
                    traceId: traceId,
                    name: "Gen:ContextSwitchDetector",
                    model: _config.Llm.Models.FastModel,
                    startTime: startTime,
                    endTime: endTime,
                    promptTokens: response.PromptTokens,
                    completionTokens: response.CandidatesTokens,
                    totalTokens: response.TotalTokens,
                    input: prompt,
                    output: response.Text,
                    metadata: new
                    {
                        is_context_switched = result.IsSwitched,
                        suggested_branch = result.SuggestedBranch,
                        reason = result.Reason
                    },
                    ct: ct);

                await _langfuse.LogEventAsync(
                    traceId,
                    "ContextSwitch:Detected",
                    "DEFAULT",
                    $"Switch={result.IsSwitched}, Branch={result.SuggestedBranch}, Reason={result.Reason}");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Context switch LLM classification failed, fallback to false");
            return new ContextSwitchResult(false, "doc_search", $"Fallback due to error: {ex.Message}");
        }
    }

    /// <summary>
    /// Парсит ответ LLM: поддерживает как структурированный JSON, так и прямой true/false.
    /// </summary>
    public static ContextSwitchResult ParseLlmResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ContextSwitchResult(false, "general_chat", "Empty LLM response");
        }

        try
        {
            var cleanJson = raw.Trim();
            if (cleanJson.StartsWith("```json"))
            {
                cleanJson = cleanJson[7..];
            }
            if (cleanJson.StartsWith("```"))
            {
                cleanJson = cleanJson[3..];
            }
            if (cleanJson.EndsWith("```"))
            {
                cleanJson = cleanJson[..^3];
            }
            cleanJson = cleanJson.Trim();

            var firstBrace = cleanJson.IndexOf('{');
            var lastBrace = cleanJson.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                cleanJson = cleanJson[firstBrace..(lastBrace + 1)];
            }

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            bool isSwitched = false;
            if (root.TryGetProperty("context_switched", out var swProp))
            {
                if (swProp.ValueKind == JsonValueKind.True || swProp.ValueKind == JsonValueKind.False)
                {
                    isSwitched = swProp.GetBoolean();
                }
                else if (bool.TryParse(swProp.GetString(), out var b))
                {
                    isSwitched = b;
                }
            }

            string branch = "doc_search";
            if (root.TryGetProperty("suggested_branch", out var bProp) && bProp.GetString() is string bStr && !string.IsNullOrWhiteSpace(bStr))
            {
                branch = bStr;
            }

            string reason = "Classified by Gemini";
            if (root.TryGetProperty("reason", out var rProp) && rProp.GetString() is string rStr && !string.IsNullOrWhiteSpace(rStr))
            {
                reason = rStr;
            }

            return new ContextSwitchResult(isSwitched, branch, reason);
        }
        catch
        {
            // Резервный текстовый парсер true/false если JSON поврежден
            var lower = raw.ToLowerInvariant();
            var isSwitched = lower.Contains("\"context_switched\": true") ||
                             lower.Contains("context_switched: true") ||
                             lower.Contains("true");

            return new ContextSwitchResult(isSwitched, "general_chat", $"Parsed from text: {raw.Trim()}");
        }
    }
}
