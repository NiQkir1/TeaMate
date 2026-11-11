using System.Text;
using Fotoprint.Core.Audio;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Documents;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Protocols;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fotoprint.Tests;

public class AudioTranscriptionTests
{
    private sealed class FakeAudioTranscriber : IAudioTranscriber
    {
        private readonly bool _isAvailable;
        private readonly TranscriptionResult _result;

        public int TranscribeStreamCalls { get; private set; }
        public int TranscribeFileCalls { get; private set; }

        public FakeAudioTranscriber(bool isAvailable, TranscriptionResult result)
        {
            _isAvailable = isAvailable;
            _result = result;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_isAvailable);
        }

        public Task<TranscriptionResult> TranscribeAsync(
            Stream audioStream,
            string fileName,
            string language = "ru",
            CancellationToken ct = default)
        {
            TranscribeStreamCalls++;
            return Task.FromResult(_result);
        }

        public Task<TranscriptionResult> TranscribeFileAsync(
            string filePath,
            string language = "ru",
            CancellationToken ct = default)
        {
            TranscribeFileCalls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeGeminiClient : IGeminiClient
    {
        private readonly Func<string, GeminiGenerateResponse> _handler;

        public FakeGeminiClient(Func<string, GeminiGenerateResponse> handler)
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
    public void TsukikoTranscriber_ParseCliOutput_Parses_Standard_Tsukiko_Json()
    {
        var json = """
        {
          "text": "Коллеги, начинаем планерку цеха офсетной печати. Кузнецову Ивану подготовить отчет по валам к пятнице.",
          "segments": [
            { "start": 0.0, "end": 4.2, "text": "Коллеги, начинаем планерку цеха офсетной печати." },
            { "start": 4.2, "end": 9.8, "text": "Кузнецову Ивану подготовить отчет по валам к пятнице." }
          ]
        }
        """;

        var result = TsukikoTranscriber.ParseCliOutput(json, durationMs: 850);

        Assert.True(result.Success);
        Assert.Equal("tsukiko-metal", result.Provider);
        Assert.Equal(850, result.DurationMs);
        Assert.Contains("Коллеги, начинаем планерку", result.Text);
        Assert.Contains("Кузнецову Ивану подготовить отчет", result.Text);
        Assert.NotNull(result.Segments);
        Assert.Equal(2, result.Segments!.Count);
        Assert.Equal(0.0, result.Segments[0].StartSeconds, 2);
        Assert.Equal(4.2, result.Segments[0].EndSeconds, 2);
        Assert.Equal(4.2, result.Segments[1].StartSeconds, 2);
        Assert.Equal(9.8, result.Segments[1].EndSeconds, 2);
    }

    [Fact]
    public void TsukikoTranscriber_ParseCliOutput_Parses_WhisperCpp_Recognizer_Json()
    {
        var whisperJson = """
        {
          "systeminfo": "WHISPER : COREML = 0 | METAL = 1",
          "params": {
            "model": "/Users/test/Library/Application Support/tsukiko/models/ggml-large-v3-turbo.bin",
            "language": "ru"
          },
          "transcription": [
            {
              "timestamps": { "from": "00:00:00,000", "to": "00:00:03,500" },
              "offsets": { "from": 0, "to": 3500 },
              "text": " Заказ 44102 запущен на машине Heidelberg."
            },
            {
              "timestamps": { "from": "00:00:03,500", "to": "00:00:07,200" },
              "offsets": { "from": 3500, "to": 7200 },
              "text": " Необходимо проверить влажность бумаги."
            }
          ]
        }
        """;

        var result = TsukikoTranscriber.ParseCliOutput(whisperJson, durationMs: 1120);

        Assert.True(result.Success);
        Assert.Equal("tsukiko-metal", result.Provider);
        Assert.Contains("Заказ 44102 запущен на машине Heidelberg.", result.Text);
        Assert.Contains("Необходимо проверить влажность бумаги.", result.Text);
        Assert.NotNull(result.Segments);
        Assert.Equal(2, result.Segments!.Count);
        Assert.Equal(0.0, result.Segments[0].StartSeconds, 2);
        Assert.Equal(3.5, result.Segments[0].EndSeconds, 2);
        Assert.Equal(3.5, result.Segments[1].StartSeconds, 2);
        Assert.Equal(7.2, result.Segments[1].EndSeconds, 2);
    }

    [Fact]
    public void TsukikoTranscriber_ParseCliOutput_Parses_Timestamped_Text_Lines()
    {
        var rawText = """
        [00:00:00.000 --> 00:00:03.000]   Привет, начинаем совещание по тиражу.
        [00:00:03.000 --> 00:00:06.500]   Морозовой Елене согласовать макет с заказчиком.
        """;

        var result = TsukikoTranscriber.ParseCliOutput(rawText, durationMs: 640);

        Assert.True(result.Success);
        Assert.Contains("Привет, начинаем совещание по тиражу.", result.Text);
        Assert.Contains("Морозовой Елене согласовать макет с заказчиком.", result.Text);
        Assert.NotNull(result.Segments);
        Assert.Equal(2, result.Segments!.Count);
        Assert.Equal(0.0, result.Segments[0].StartSeconds, 2);
        Assert.Equal(3.0, result.Segments[0].EndSeconds, 2);
        Assert.Equal(3.0, result.Segments[1].StartSeconds, 2);
        Assert.Equal(6.5, result.Segments[1].EndSeconds, 2);
    }

    [Fact]
    public async Task CompositeAudioTranscriber_Returns_Primary_When_Tsukiko_Succeeds()
    {
        var primary = new FakeAudioTranscriber(
            isAvailable: true,
            new TranscriptionResult(
                Success: true,
                Text: "Транскрипт с локального Metal GPU",
                Segments: new List<AudioSegment> { new(0, 2.5, "Транскрипт с локального Metal GPU") },
                Provider: "tsukiko-metal",
                DurationMs: 420));

        var fallback = new FakeAudioTranscriber(
            isAvailable: true,
            new TranscriptionResult(
                Success: true,
                Text: "Транскрипт из облака Gemini",
                Segments: null,
                Provider: "gemini",
                DurationMs: 950));

        var config = new FotoprintConfig();
        config.Audio.Provider = "auto";

        var composite = new CompositeAudioTranscriber(primary, fallback, config);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake-audio"));

        var result = await composite.TranscribeAsync(stream, "meeting.m4a", "ru");

        Assert.True(result.Success);
        Assert.Equal("tsukiko-metal", result.Provider);
        Assert.Equal("Транскрипт с локального Metal GPU", result.Text);
        Assert.Equal(1, primary.TranscribeStreamCalls);
        Assert.Equal(0, fallback.TranscribeStreamCalls);
    }

    [Fact]
    public async Task CompositeAudioTranscriber_Falls_Back_To_Gemini_When_Primary_Unavailable_Or_Fails()
    {
        // Сценарий 1: Tsukiko недоступен (например, контейнер без GPU)
        var unavailablePrimary = new FakeAudioTranscriber(
            isAvailable: false,
            new TranscriptionResult(false, string.Empty, null, "tsukiko-metal", 0, "CLI not found"));

        var geminiFallback = new FakeAudioTranscriber(
            isAvailable: true,
            new TranscriptionResult(
                Success: true,
                Text: "Резервная расшифровка через Gemini 1.5 Flash",
                Segments: new List<AudioSegment> { new(0, 4.0, "Резервная расшифровка через Gemini 1.5 Flash") },
                Provider: "gemini",
                DurationMs: 1100));

        var config = new FotoprintConfig();
        config.Audio.Provider = "auto";

        var composite1 = new CompositeAudioTranscriber(unavailablePrimary, geminiFallback, config);
        using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes("fake-audio-1"));
        var res1 = await composite1.TranscribeAsync(stream1, "voice.oga", "ru");

        Assert.True(res1.Success);
        Assert.Equal("gemini", res1.Provider);
        Assert.Contains("Gemini 1.5 Flash", res1.Text);
        Assert.Equal(0, unavailablePrimary.TranscribeStreamCalls);
        Assert.Equal(1, geminiFallback.TranscribeStreamCalls);

        // Сценарий 2: Tsukiko доступен, но вернул ошибку выполнения
        var failingPrimary = new FakeAudioTranscriber(
            isAvailable: true,
            new TranscriptionResult(false, string.Empty, null, "tsukiko-metal", 150, "Process timeout"));

        var composite2 = new CompositeAudioTranscriber(failingPrimary, geminiFallback, config);
        using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes("fake-audio-2"));
        var res2 = await composite2.TranscribeAsync(stream2, "voice2.oga", "ru");

        Assert.True(res2.Success);
        Assert.Equal("gemini", res2.Provider);
        Assert.Equal(1, failingPrimary.TranscribeStreamCalls);
        Assert.Equal(2, geminiFallback.TranscribeStreamCalls);
    }

    [Fact]
    public async Task CompositeAudioTranscriber_Respects_Explicit_Provider_Configuration()
    {
        var primary = new FakeAudioTranscriber(
            isAvailable: true,
            new TranscriptionResult(true, "Tsukiko Output", null, "tsukiko-metal", 300));

        var fallback = new FakeAudioTranscriber(
            isAvailable: true,
            new TranscriptionResult(true, "Gemini Output", null, "gemini", 700));

        var geminiConfig = new FotoprintConfig();
        geminiConfig.Audio.Provider = "gemini";

        var compositeGeminiOnly = new CompositeAudioTranscriber(primary, fallback, geminiConfig);
        using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes("audio"));
        var geminiRes = await compositeGeminiOnly.TranscribeAsync(stream1, "test.wav", "ru");

        Assert.True(geminiRes.Success);
        Assert.Equal("gemini", geminiRes.Provider);
        Assert.Equal(0, primary.TranscribeStreamCalls);
        Assert.Equal(1, fallback.TranscribeStreamCalls);

        var tsukikoConfig = new FotoprintConfig();
        tsukikoConfig.Audio.Provider = "tsukiko";

        var compositeTsukikoOnly = new CompositeAudioTranscriber(primary, fallback, tsukikoConfig);
        using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes("audio"));
        var tsukikoRes = await compositeTsukikoOnly.TranscribeAsync(stream2, "test.wav", "ru");

        Assert.True(tsukikoRes.Success);
        Assert.Equal("tsukiko-metal", tsukikoRes.Provider);
        Assert.Equal(1, primary.TranscribeStreamCalls);
        Assert.Equal(1, fallback.TranscribeStreamCalls);
    }

    [Fact]
    public async Task MeetingTranscriptAnalyzer_Extracts_Tasks_And_Enforces_NoHallucinations_Rule()
    {
        // 1. Проверка NormalizeActionItem напрямую
        var rawItem = new ExtractedActionItem(
            Title: "Заказать увлажняющий раствор для офсетного цеха",
            Description: null,
            Responsible: "",
            DeadlineText: "неизвестно",
            ParsedDeadline: null);

        var normalized = MeetingTranscriptAnalyzer.NormalizeActionItem(rawItem);
        Assert.Equal("требует уточнения", normalized.Responsible);
        Assert.Equal("требует уточнения", normalized.DeadlineText);

        // 2. Проверка ParseAnalysisJson с заполненными и пропущенными полями
        var llmJson = """
        {
          "title": "Планерка производства по тиражу 44102",
          "summary": "Обсудили статус печатной машины Heidelberg и поставку бумаги.",
          "decisions": [
            "Запустить ночную смену на линии резки"
          ],
          "discussed": [
            "Влажность бумаги в цехе №2",
            "График отгрузки клиенту Альфа-Маркет"
          ],
          "risks": [
            "Риск задержки доставки краски от поставщика"
          ],
          "requires_clarification": [
            "Не назван ответственный за проверку кондиционеров"
          ],
          "action_items": [
            {
              "title": "Подготовить график смен для цеха резки",
              "description": "Согласовать выход операторов",
              "responsible": "Кузнецов Иван",
              "deadline_text": "2026-11-15",
              "parsed_deadline": "2026-11-15"
            },
            {
              "title": "Проверить систему кондиционирования склада",
              "description": "Замерить влажность",
              "responsible": "",
              "deadline_text": null,
              "parsed_deadline": null
            }
          ]
        }
        """;

        var parsed = MeetingTranscriptAnalyzer.ParseAnalysisJson(llmJson);
        Assert.Equal("Планерка производства по тиражу 44102", parsed.Title);
        Assert.Single(parsed.Decisions);
        Assert.Equal(2, parsed.ActionItems.Count);
        Assert.Equal("Кузнецов Иван", parsed.ActionItems[0].Responsible);
        Assert.Equal("2026-11-15", parsed.ActionItems[0].DeadlineText);
        Assert.Equal("требует уточнения", parsed.ActionItems[1].Responsible);
        Assert.Equal("требует уточнения", parsed.ActionItems[1].DeadlineText);

        // 3. Проверка эвристического анализатора (при недоступности LLM)
        var heuristicTranscript = """
        Коллеги, начинаем совещание.
        Решили перевести машину Heidelberg Speedmaster на круглосуточный график.
        Поручение: срочно заказать комплект валов для офсетного цеха.
        """;

        var heuristicResult = MeetingTranscriptAnalyzer.BuildHeuristicAnalysis(heuristicTranscript, "Утренняя планерка");
        Assert.Equal("Утренняя планерка", heuristicResult.Title);
        Assert.NotEmpty(heuristicResult.Decisions);
        Assert.NotEmpty(heuristicResult.ActionItems);
        Assert.All(heuristicResult.ActionItems, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Responsible));
            Assert.False(string.IsNullOrWhiteSpace(item.DeadlineText));
        });
        Assert.Contains(heuristicResult.ActionItems, i => i.Responsible == "требует уточнения" || i.DeadlineText == "требует уточнения");

        // 4. Проверка полного вызова AnalyzeTranscriptAsync через IGeminiClient
        var fakeGemini = new FakeGeminiClient(_ => new GeminiGenerateResponse(
            IsSuccess: true,
            Text: llmJson,
            FinishReason: "STOP",
            PromptTokens: 120,
            CompletionTokens: 180,
            TotalTokens: 300,
            AttemptCount: 1,
            ErrorMessage: null));

        var analyzer = new MeetingTranscriptAnalyzer(fakeGemini, new FotoprintConfig(), null, NullLogger<MeetingTranscriptAnalyzer>.Instance);
        var fullAnalysis = await analyzer.AnalyzeTranscriptAsync(heuristicTranscript, "Планерка");
        Assert.Equal(2, fullAnalysis.ActionItems.Count);
        Assert.Equal("требует уточнения", fullAnalysis.ActionItems[1].Responsible);
        Assert.Equal("требует уточнения", fullAnalysis.ActionItems[1].DeadlineText);
    }

    [Fact]
    public async Task MeetingProtocolGenerator_Renders_RequiresClarification_For_Missing_Deadline_And_Responsible()
    {
        var generator = new MeetingProtocolGenerator();
        var exportData = new ProtocolExportData(
            Title: "Совещание по качеству печати",
            MeetingDate: DateTime.UtcNow,
            OrganizerName: "Смирнов Алексей",
            Participants: new List<string> { "Кузнецов Иван", "Морозова Елена" },
            Summary: "Разбор инцидента с разнооттеночностью тиража.",
            Decisions: new List<string> { "Провести внеплановую калибровку спектрофотометра." },
            ActionItems: new List<ActionItemExportData>
            {
                new(
                    Title: "Провести калибровку спектрофотометра в цехе №1",
                    ResponsibleName: "",
                    Deadline: null,
                    Status: "pending")
            });

        // 1. Проверяем HTML
        var html = generator.GenerateHtml(exportData);
        Assert.Contains("Провести калибровку спектрофотометра в цехе №1", html);
        Assert.Contains("требует уточнения", html);

        // 2. Проверяем бинарный DOCX через DocumentParser
        var docxBytes = generator.GenerateDocx(exportData);
        Assert.NotNull(docxBytes);
        Assert.True(docxBytes.Length > 0);

        var parser = new DocumentParser();
        await using var docxStream = new MemoryStream(docxBytes);
        var parsedDoc = await parser.ParseStreamAsync(docxStream, "Protocol_Test.docx");

        Assert.Contains("Провести калибровку спектрофотометра в цехе №1", parsedDoc.FullText);
        Assert.Contains("требует уточнения", parsedDoc.FullText);
    }

    [Fact]
    public void TsukikoTranscriber_ParseCliOutput_Handles_Empty_ErrorJson_And_PlainText_EdgeCases()
    {
        // 1. Пустая строка и пробелы -> Success == false
        var emptyResult = TsukikoTranscriber.ParseCliOutput("", durationMs: 10);
        Assert.False(emptyResult.Success);

        var whitespaceResult = TsukikoTranscriber.ParseCliOutput("   \n\t  ", durationMs: 10);
        Assert.False(whitespaceResult.Success);

        // 2. JSON с ошибкой -> Success == false
        var errorJsonResult = TsukikoTranscriber.ParseCliOutput("""{"error": "model not found"}""", durationMs: 25);
        Assert.False(errorJsonResult.Success);
        Assert.Contains("model not found", errorJsonResult.ErrorMessage ?? string.Empty);

        // 3. Обычный плоский текст без JSON и без таймкодов -> Success == true
        var plainResult = TsukikoTranscriber.ParseCliOutput("Обычный текст расшифровки без таймкодов.", durationMs: 100);
        Assert.True(plainResult.Success);
        Assert.Equal("Обычный текст расшифровки без таймкодов.", plainResult.Text);
    }

    [Fact]
    public void TsukikoPathResolver_ResolveCliPath_Finds_Portable_Windows_And_Mac_Binaries_Or_Returns_Null()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"fotoprint_cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 1. Пустая папка при несуществующем configuredPath -> null (Zero-Install Cloud Mode)
            var missing = TsukikoPathResolver.ResolveCliPath(
                configuredPath: "/non_existent_dir_12345/tsukiko-transcribe",
                customSearchRoot: tempRoot);
            Assert.Null(missing);

            // 2. Портативный бинарник macOS/Linux: tools/tsukiko/tsukiko-transcribe
            var toolsDir = Path.Combine(tempRoot, "tools", "tsukiko");
            Directory.CreateDirectory(toolsDir);
            var macBinPath = Path.Combine(toolsDir, "tsukiko-transcribe");
            File.WriteAllText(macBinPath, "#!/bin/sh\necho ok");

            var resolvedMac = TsukikoPathResolver.ResolveCliPath(
                configuredPath: "/non_existent_dir_12345/tsukiko-transcribe",
                customSearchRoot: tempRoot);
            Assert.NotNull(resolvedMac);
            Assert.Equal(Path.GetFullPath(macBinPath), Path.GetFullPath(resolvedMac!));

            File.Delete(macBinPath);

            // 3. Портативный бинарник Windows: tools/tsukiko/tsukiko-transcribe.exe
            var winBinPath = Path.Combine(toolsDir, "tsukiko-transcribe.exe");
            File.WriteAllText(winBinPath, "MZ-fake-exe");

            var resolvedWin = TsukikoPathResolver.ResolveCliPath(
                configuredPath: "/non_existent_dir_12345/tsukiko-transcribe",
                customSearchRoot: tempRoot);
            Assert.NotNull(resolvedWin);
            Assert.Equal(Path.GetFullPath(winBinPath), Path.GetFullPath(resolvedWin!));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void TsukikoPathResolver_ResolveModelPath_Finds_Portable_Ggml_Models_Or_Returns_Null()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"fotoprint_model_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 1. В пустой папке модель не найдена -> null
            var missingModel = TsukikoPathResolver.ResolveModelPath(customSearchRoot: tempRoot);
            Assert.Null(missingModel);

            // 2. Находит легкую модель ggml-base.bin в tools/tsukiko/models
            var modelsDir = Path.Combine(tempRoot, "tools", "tsukiko", "models");
            Directory.CreateDirectory(modelsDir);
            var baseModelPath = Path.Combine(modelsDir, "ggml-base.bin");
            File.WriteAllBytes(baseModelPath, new byte[] { 0x67, 0x67, 0x6D, 0x6C });

            var resolvedBase = TsukikoPathResolver.ResolveModelPath(customSearchRoot: tempRoot);
            Assert.NotNull(resolvedBase);
            Assert.Equal(Path.GetFullPath(baseModelPath), Path.GetFullPath(resolvedBase!));

            // 3. При наличии ggml-large-v3-turbo.bin выбирает приоритетную turbo-модель
            var turboModelPath = Path.Combine(modelsDir, "ggml-large-v3-turbo.bin");
            File.WriteAllBytes(turboModelPath, new byte[] { 0x67, 0x67, 0x6D, 0x6C });

            var resolvedTurbo = TsukikoPathResolver.ResolveModelPath(customSearchRoot: tempRoot);
            Assert.NotNull(resolvedTurbo);
            Assert.Equal(Path.GetFullPath(turboModelPath), Path.GetFullPath(resolvedTurbo!));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
