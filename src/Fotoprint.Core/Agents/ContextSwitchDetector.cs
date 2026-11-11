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
            var initialBranch = ClassifyDomainBranch(currentMessage) ?? "general_chat";
            return new ContextSwitchResult(false, initialBranch, "Первое сообщение в сессии");
        }

        // Быстрый детерминированный гибридный классификатор (экономит 1 вызов LLM из квоты 40 RPM в 90%+ случаев)
        if (TryClassifyFastPath(activeHistory, currentMessage, out var fastResult))
        {
            if (_langfuse != null && !string.IsNullOrEmpty(traceId))
            {
                _ = _langfuse.LogEventAsync(
                    traceId,
                    "ContextSwitch:Detected",
                    "DEFAULT",
                    $"FastPath: Switch={fastResult.IsSwitched}, Branch={fastResult.SuggestedBranch}, Reason={fastResult.Reason}",
                    ct: ct);
            }
            return fastResult;
        }

        // Форматируем предыдущую сессию (резервный LLM-путь для нестандартных/неоднозначных фраз)
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
            var response = await _gemini.GenerateJsonNoThinkingAsync(
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

    private static bool TryClassifyFastPath(
        List<ChatMessage> activeHistory,
        string currentMessage,
        out ContextSwitchResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(currentMessage))
        {
            return false;
        }

        // Определяем домен предыдущего сообщения пользователя (или последнего сообщения в сессии)
        var lastUserMsg = activeHistory.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
                          ?? activeHistory[^1].Content;
        var prevBranch = ClassifyDomainBranch(lastUserMsg) ?? ClassifyDomainBranch(activeHistory[^1].Content);
        var currentBranch = ClassifyDomainBranch(currentMessage);

        // 1. Явная фраза-продолжение ("а для ...", "а по нему", "подробнее", "еще", "а какие там...") без жесткой смены домена
        if (IsContinuationPhrase(currentMessage))
        {
            if (currentBranch == null || prevBranch == null || string.Equals(currentBranch, prevBranch, StringComparison.OrdinalIgnoreCase))
            {
                var targetBranch = currentBranch ?? prevBranch ?? "doc_search";
                result = new ContextSwitchResult(
                    IsSwitched: false,
                    SuggestedBranch: targetBranch,
                    Reason: $"Пользователь продолжает обсуждение в ветке '{targetBranch}'");
                return true;
            }
        }

        // 2. Если домен текущего сообщения четко определен
        if (currentBranch != null && prevBranch != null)
        {
            if (string.Equals(currentBranch, prevBranch, StringComparison.OrdinalIgnoreCase))
            {
                result = new ContextSwitchResult(
                    IsSwitched: false,
                    SuggestedBranch: currentBranch,
                    Reason: $"Пользователь продолжает обсуждение темы '{currentBranch}'");
                return true;
            }

            // Если это явная команда на постановку задачи / встречу / другой модуль — фиксируем смену темы,
            // но для обычных вопросов по заказам/КПИ/документам внутри рабочего диалога сохраняем сессию
            bool hardSwitch = currentBranch is "task_management" or "meeting_prep" or "meeting_summary"
                              && currentMessage.Contains("поставь", StringComparison.OrdinalIgnoreCase)
                              || currentMessage.Contains("создай задач", StringComparison.OrdinalIgnoreCase)
                              || currentMessage.Contains("назначь встреч", StringComparison.OrdinalIgnoreCase);

            if (hardSwitch)
            {
                result = new ContextSwitchResult(
                    IsSwitched: true,
                    SuggestedBranch: currentBranch,
                    Reason: $"Пользователь резко сменил тему с '{prevBranch}' на '{currentBranch}'");
                return true;
            }

            // Мягкий переход внутри рабочего ассистента (например, проверил заказ 44102 и спросил про задачи) — не рвем контекст сессии!
            result = new ContextSwitchResult(
                IsSwitched: false,
                SuggestedBranch: currentBranch,
                Reason: $"Пользователь продолжает обсуждение в рабочей сессии (переход к '{currentBranch}')");
            return true;
        }

        if (currentBranch != null)
        {
            result = new ContextSwitchResult(
                IsSwitched: false,
                SuggestedBranch: currentBranch,
                Reason: $"Пользователь продолжает обсуждение ('{currentBranch}')");
            return true;
        }

        return false;
    }

    private static bool IsContinuationPhrase(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t.StartsWith("а ") ||
               t.StartsWith("и ") ||
               t.StartsWith("ну ") ||
               t.StartsWith("тогда ") ||
               t.StartsWith("подробнее") ||
               t.StartsWith("уточни") ||
               t.StartsWith("расскажи подробнее") ||
               t.StartsWith("а что ") ||
               t.StartsWith("а как") ||
               t.StartsWith("а для ") ||
               t.StartsWith("а по ");
    }

    private static string? ClassifyDomainBranch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();

        if (t.Contains("задач") || t.Contains("битрикс") || t.Contains("bitrix") || t.Contains("поручен") || t.Contains("сделк") || t.Contains("crm") || t.Contains("дедлайн"))
            return "task_management";

        if (t.Contains("протокол") || t.Contains("итоги встреч") || t.Contains("резюме встреч"))
            return "meeting_summary";

        if (t.Contains("встреч") || t.Contains("планерк") || t.Contains("совещан") || t.Contains("календар") || t.Contains("расписан"))
            return "meeting_prep";

        if (t.Contains("регламент") || t.Contains("heidelberg") || t.Contains("краск") || t.Contains("бумаг") ||
            t.Contains("плотн") || t.Contains("раг") || t.Contains("rag") || t.Contains("база знаний") ||
            t.Contains("базе знаний") || t.Contains("скидк") || t.Contains("сверхуроч") || t.Contains("отход") ||
            t.Contains("документ") || t.Contains("правил"))
            return "doc_search";

        if (t.Contains("выработк") || t.Contains("kpi") || t.Contains("кпи") || t.Contains("брак") ||
            t.Contains("оттиск") || t.Contains("загрузк") || t.Contains("сводк") || t.Contains("дайджест") ||
            t.Contains("заказ") || t.Contains("тираж") || t.Contains("44102") || t.Contains("1с") || t.Contains("1c") || t.Contains("офсет"))
            return "executive_report";

        if (t.Contains("привет") || t.Contains("здравствуй") || t.Contains("спасибо") || t.Contains("кто ты") || t.Contains("помощь"))
            return "general_chat";

        return null;
    }
}
