using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fotoprint.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Fotoprint.Core.Audio;

public class LocalEngineAudioTranscriber : IAudioTranscriber
{
    private static readonly Regex TimestampLineRegex = new(
        @"^\s*\[(?<start>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{1,3})\]\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly IAudioEngineResolver _engineResolver;
    private readonly IVocabularyProvider _vocabularyProvider;
    private readonly IVocabularyPostProcessor _postProcessor;
    private readonly FotoprintConfig _config;
    private readonly ILogger<LocalEngineAudioTranscriber>? _logger;

    public LocalEngineAudioTranscriber(
        IAudioEngineResolver engineResolver,
        IVocabularyProvider vocabularyProvider,
        IVocabularyPostProcessor postProcessor,
        FotoprintConfig config,
        ILogger<LocalEngineAudioTranscriber>? logger = null)
    {
        _engineResolver = engineResolver ?? throw new ArgumentNullException(nameof(engineResolver));
        _vocabularyProvider = vocabularyProvider ?? throw new ArgumentNullException(nameof(vocabularyProvider));
        _postProcessor = postProcessor ?? throw new ArgumentNullException(nameof(postProcessor));
        _config = config ?? new FotoprintConfig();
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_config.Audio.Enabled)
        {
            return Task.FromResult(false);
        }

        var enginePath = _engineResolver.ResolveEnginePath();
        var modelPath = _engineResolver.ResolveModelPath();

        var available = !string.IsNullOrWhiteSpace(enginePath) &&
                        File.Exists(enginePath) &&
                        !string.IsNullOrWhiteSpace(modelPath) &&
                        File.Exists(modelPath);

        return Task.FromResult(available);
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        Stream audioStream,
        string fileName,
        string language = "ru",
        CancellationToken ct = default)
    {
        if (audioStream == null)
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: _engineResolver.GetProviderName(),
                DurationMs: 0,
                ErrorMessage: "Audio stream is null.");
        }

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".wav";
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"fotoprint_input_{Guid.NewGuid():N}{ext}");
        try
        {
            if (audioStream.CanSeek && audioStream.Position > 0)
            {
                audioStream.Position = 0;
            }

            await using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await audioStream.CopyToAsync(fs, ct);
            }

            return await TranscribeFileAsync(tempFilePath, language, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocalEngineAudioTranscriber] Failed to process stream for {FileName}", fileName);
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: _engineResolver.GetProviderName(),
                DurationMs: 0,
                ErrorMessage: $"Audio stream processing error: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(tempFilePath);
        }
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(
        string filePath,
        string language = "ru",
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var provider = _engineResolver.GetProviderName();

        if (!_config.Audio.Enabled)
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: "disabled",
                DurationMs: 0,
                ErrorMessage: "Audio transcription is disabled in configuration.");
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: provider,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"Audio file not found: {filePath}");
        }

        var enginePath = _engineResolver.ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath))
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: provider,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"C++ transcription engine binary not found. Expected under tools/engine/.");
        }

        var modelPath = _engineResolver.ResolveModelPath();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: provider,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"Whisper model file (.bin) not found. Run ./scripts/download-model.sh.");
        }

        var lang = string.IsNullOrWhiteSpace(language) ? "ru" : language.Trim();
        var timeoutSeconds = _config.Audio.Engine.TimeoutSeconds > 0
            ? _config.Audio.Engine.TimeoutSeconds
            : 300;

        var tempWavPath = Path.Combine(Path.GetTempPath(), $"fotoprint_pcm_{Guid.NewGuid():N}.wav");
        var tempOutPrefix = Path.Combine(Path.GetTempPath(), $"fotoprint_out_{Guid.NewGuid():N}");
        var tempOutJsonPath = tempOutPrefix + ".json";

        try
        {
            // Convert to 16kHz mono PCM WAV if converter is available
            var wavInputPath = await ConvertTo16kMonoWavAsync(filePath, tempWavPath, ct);

            // Vocabulary prompt from IVocabularyProvider
            var vocabularyPrompt = _vocabularyProvider.GetPromptString();

            var psi = new ProcessStartInfo
            {
                FileName = enginePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(lang);
            psi.ArgumentList.Add("-np");

            if (!string.IsNullOrWhiteSpace(vocabularyPrompt))
            {
                psi.ArgumentList.Add("--prompt");
                psi.ArgumentList.Add(vocabularyPrompt);
            }

            psi.ArgumentList.Add("-oj");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add(tempOutPrefix);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(wavInputPath);

            var (exitCode, stdout, stderr) = await ExecuteProcessAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), ct);

            TranscriptionResult rawResult;
            if (File.Exists(tempOutJsonPath))
            {
                var jsonText = await File.ReadAllTextAsync(tempOutJsonPath, ct);
                rawResult = ParseEngineOutput(jsonText, sw.ElapsedMilliseconds, provider);
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                rawResult = ParseEngineOutput(stdout, sw.ElapsedMilliseconds, provider);
            }
            else
            {
                var errMsg = !string.IsNullOrWhiteSpace(stderr)
                    ? stderr.Trim()
                    : $"Engine exited with code {exitCode} and produced no output.";
                _logger?.LogWarning("[LocalEngineAudioTranscriber] Engine failed: {Error}", errMsg);
                return new TranscriptionResult(
                    Success: false,
                    Text: string.Empty,
                    Segments: new List<AudioSegment>(),
                    Provider: provider,
                    DurationMs: sw.ElapsedMilliseconds,
                    ErrorMessage: errMsg);
            }

            if (!rawResult.Success && !string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(rawResult.ErrorMessage))
            {
                rawResult = rawResult with { ErrorMessage = stderr.Trim() };
            }

            // Apply IVocabularyPostProcessor for homophone correction
            var finalResult = _postProcessor.ProcessResult(rawResult);
            return finalResult;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocalEngineAudioTranscriber] Execution error for {FilePath}", filePath);
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: provider,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"Local engine exception: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(tempWavPath);
            TryDeleteFile(tempOutJsonPath);
            TryDeleteFile(tempOutPrefix + ".txt");
        }
    }

    private async Task<string> ConvertTo16kMonoWavAsync(string inputPath, string targetWavPath, CancellationToken ct)
    {
        try
        {
            var converterPath = _engineResolver.ResolveAudioConverterPath();
            if (string.IsNullOrWhiteSpace(converterPath) || !File.Exists(converterPath))
            {
                return inputPath;
            }

            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                converterPath.EndsWith("afconvert", StringComparison.OrdinalIgnoreCase))
            {
                psi = new ProcessStartInfo
                {
                    FileName = converterPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("WAVE");
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add("LEI16@16000");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add(inputPath);
                psi.ArgumentList.Add(targetWavPath);
            }
            else
            {
                // ffmpeg
                psi = new ProcessStartInfo
                {
                    FileName = converterPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(inputPath);
                psi.ArgumentList.Add("-ar");
                psi.ArgumentList.Add("16000");
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("pcm_s16le");
                psi.ArgumentList.Add(targetWavPath);
            }

            var (exitCode, _, stderr) = await ExecuteProcessAsync(psi, TimeSpan.FromSeconds(60), ct);
            if (exitCode == 0 && File.Exists(targetWavPath))
            {
                return targetWavPath;
            }

            _logger?.LogDebug("[LocalEngineAudioTranscriber] Audio converter exited with {ExitCode}: {Error}. Using original file.", exitCode, stderr.Trim());
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LocalEngineAudioTranscriber] Audio conversion failed. Using original file.");
        }

        return inputPath;
    }

    public static TranscriptionResult ParseEngineOutput(
        string rawOutput,
        long durationMs,
        string provider = "local-metal")
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: provider,
                DurationMs: durationMs,
                ErrorMessage: "Empty output from transcription engine.");
        }

        var trimmed = rawOutput.Trim();

        // 1. Try JSON parsing
        var jsonCandidate = ExtractJsonObject(trimmed);
        if (!string.IsNullOrWhiteSpace(jsonCandidate))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonCandidate);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyCaseInsensitive(root, "error", out var errProp) &&
                        errProp.ValueKind == JsonValueKind.String)
                    {
                        var errText = errProp.GetString();
                        if (!string.IsNullOrWhiteSpace(errText) &&
                            (!TryGetPropertyCaseInsensitive(root, "text", out var txtCheck) ||
                             string.IsNullOrWhiteSpace(txtCheck.GetString())))
                        {
                            return new TranscriptionResult(
                                Success: false,
                                Text: string.Empty,
                                Segments: new List<AudioSegment>(),
                                Provider: provider,
                                DurationMs: durationMs,
                                ErrorMessage: errText);
                        }
                    }

                    // Format A: whisper.cpp / tsukiko-recognizer JSON with "transcription" array
                    if (TryGetPropertyCaseInsensitive(root, "transcription", out var transArray) &&
                        transArray.ValueKind == JsonValueKind.Array)
                    {
                        var whisperSegments = new List<AudioSegment>();
                        var textParts = new List<string>();

                        foreach (var item in transArray.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;

                            var segText = TryGetPropertyCaseInsensitive(item, "text", out var tProp) && tProp.ValueKind == JsonValueKind.String
                                ? (tProp.GetString()?.Trim() ?? string.Empty)
                                : string.Empty;

                            if (string.IsNullOrWhiteSpace(segText)) continue;

                            double startSec = 0;
                            double endSec = 0;

                            if (TryGetPropertyCaseInsensitive(item, "offsets", out var offsets) &&
                                offsets.ValueKind == JsonValueKind.Object)
                            {
                                if (TryGetPropertyCaseInsensitive(offsets, "from", out var fromMs) && fromMs.TryGetDouble(out var fVal))
                                {
                                    startSec = Math.Round(fVal / 1000.0, 3);
                                }
                                if (TryGetPropertyCaseInsensitive(offsets, "to", out var toMs) && toMs.TryGetDouble(out var tVal))
                                {
                                    endSec = Math.Round(tVal / 1000.0, 3);
                                }
                            }
                            else if (TryGetPropertyCaseInsensitive(item, "timestamps", out var timestamps) &&
                                     timestamps.ValueKind == JsonValueKind.Object)
                            {
                                if (TryGetPropertyCaseInsensitive(timestamps, "from", out var fromTs) && fromTs.ValueKind == JsonValueKind.String)
                                {
                                    TryParseTimestampToSeconds(fromTs.GetString(), out startSec);
                                }
                                if (TryGetPropertyCaseInsensitive(timestamps, "to", out var toTs) && toTs.ValueKind == JsonValueKind.String)
                                {
                                    TryParseTimestampToSeconds(toTs.GetString(), out endSec);
                                }
                            }

                            string? speaker = null;
                            if (TryGetPropertyCaseInsensitive(item, "speaker", out var spkProp) && spkProp.ValueKind == JsonValueKind.String)
                            {
                                speaker = spkProp.GetString();
                            }

                            whisperSegments.Add(new AudioSegment(startSec, endSec, segText, speaker));
                            textParts.Add(segText);
                        }

                        var combinedWhisperText = string.Join(" ", textParts).Trim();
                        return new TranscriptionResult(
                            Success: !string.IsNullOrWhiteSpace(combinedWhisperText),
                            Text: combinedWhisperText,
                            Segments: whisperSegments,
                            Provider: provider,
                            DurationMs: durationMs,
                            ErrorMessage: string.IsNullOrWhiteSpace(combinedWhisperText) ? "No speech detected in audio." : null);
                    }

                    // Format B: Standard JSON with "text" and optional "segments"
                    var hasTextProp = TryGetPropertyCaseInsensitive(root, "text", out var rootTextProp);
                    var hasSegmentsProp = TryGetPropertyCaseInsensitive(root, "segments", out var rootSegmentsProp);

                    if (hasTextProp || hasSegmentsProp)
                    {
                        var segments = new List<AudioSegment>();
                        if (hasSegmentsProp && rootSegmentsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var seg in rootSegmentsProp.EnumerateArray())
                            {
                                if (seg.ValueKind != JsonValueKind.Object) continue;

                                var segText = TryGetPropertyCaseInsensitive(seg, "text", out var stProp) && stProp.ValueKind == JsonValueKind.String
                                    ? (stProp.GetString()?.Trim() ?? string.Empty)
                                    : string.Empty;

                                if (string.IsNullOrWhiteSpace(segText)) continue;

                                double startSec = ExtractSeconds(seg, "start", "start_seconds", "startSeconds", "startMs");
                                double endSec = ExtractSeconds(seg, "end", "end_seconds", "endSeconds", "endMs");

                                string? speaker = null;
                                if (TryGetPropertyCaseInsensitive(seg, "speaker", out var spkProp) && spkProp.ValueKind == JsonValueKind.String)
                                {
                                    speaker = spkProp.GetString();
                                }

                                segments.Add(new AudioSegment(startSec, endSec, segText, speaker));
                            }
                        }

                        var rootText = hasTextProp && rootTextProp.ValueKind == JsonValueKind.String
                            ? (rootTextProp.GetString()?.Trim() ?? string.Empty)
                            : string.Empty;

                        if (string.IsNullOrWhiteSpace(rootText) && segments.Count > 0)
                        {
                            rootText = string.Join(" ", segments.Select(s => s.Text)).Trim();
                        }

                        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(rootText))
                        {
                            segments.Add(new AudioSegment(0.0, Math.Max(0.0, Math.Round(durationMs / 1000.0, 2)), rootText));
                        }

                        return new TranscriptionResult(
                            Success: !string.IsNullOrWhiteSpace(rootText),
                            Text: rootText,
                            Segments: segments,
                            Provider: provider,
                            DurationMs: durationMs,
                            ErrorMessage: string.IsNullOrWhiteSpace(rootText) ? "Empty text in JSON output." : null);
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to timestamped / plain text
            }
        }

        // 2. Timestamped lines: [00:00:00.000 --> 00:00:03.000] Text
        var matches = TimestampLineRegex.Matches(trimmed);
        if (matches.Count > 0)
        {
            var lineSegments = new List<AudioSegment>();
            var lineTexts = new List<string>();

            foreach (Match match in matches)
            {
                var startStr = match.Groups["start"].Value;
                var endStr = match.Groups["end"].Value;
                var lineText = match.Groups["text"].Value.Trim();

                if (string.IsNullOrWhiteSpace(lineText)) continue;

                TryParseTimestampToSeconds(startStr, out var startSec);
                TryParseTimestampToSeconds(endStr, out var endSec);

                lineSegments.Add(new AudioSegment(startSec, endSec, lineText));
                lineTexts.Add(lineText);
            }

            var joinedText = string.Join(" ", lineTexts).Trim();
            if (!string.IsNullOrWhiteSpace(joinedText))
            {
                return new TranscriptionResult(
                    Success: true,
                    Text: joinedText,
                    Segments: lineSegments,
                    Provider: provider,
                    DurationMs: durationMs);
            }
        }

        // 3. Plain text fallback
        return new TranscriptionResult(
            Success: true,
            Text: trimmed,
            Segments: new List<AudioSegment>
            {
                new(0.0, Math.Max(0.0, Math.Round(durationMs / 1000.0, 2)), trimmed)
            },
            Provider: provider,
            DurationMs: durationMs);
    }

    private static string? ExtractJsonObject(string input)
    {
        var firstBrace = input.IndexOf('{');
        var lastBrace = input.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return input.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static double ExtractSeconds(JsonElement obj, string secKey1, string secKey2, string secKey3, string msKey)
    {
        if (TryGetPropertyCaseInsensitive(obj, secKey1, out var p1) && TryReadDouble(p1, out var v1)) return v1;
        if (TryGetPropertyCaseInsensitive(obj, secKey2, out var p2) && TryReadDouble(p2, out var v2)) return v2;
        if (TryGetPropertyCaseInsensitive(obj, secKey3, out var p3) && TryReadDouble(p3, out var v3)) return v3;
        if (TryGetPropertyCaseInsensitive(obj, msKey, out var pMs) && TryReadDouble(pMs, out var vMs)) return Math.Round(vMs / 1000.0, 3);
        return 0.0;
    }

    private static bool TryReadDouble(JsonElement el, out double val)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out val))
        {
            return true;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out val))
        {
            return true;
        }

        val = 0.0;
        return false;
    }

    private static bool TryParseTimestampToSeconds(string? rawTimestamp, out double seconds)
    {
        seconds = 0.0;
        if (string.IsNullOrWhiteSpace(rawTimestamp))
        {
            return false;
        }

        var normalized = rawTimestamp.Trim().Replace(',', '.');
        var parts = normalized.Split(':');

        try
        {
            if (parts.Length == 3)
            {
                var h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                var m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                var s = double.Parse(parts[2], CultureInfo.InvariantCulture);
                seconds = Math.Round(h * 3600 + m * 60 + s, 3);
                return true;
            }

            if (parts.Length == 2)
            {
                var m = double.Parse(parts[0], CultureInfo.InvariantCulture);
                var s = double.Parse(parts[1], CultureInfo.InvariantCulture);
                seconds = Math.Round(m * 60 + s, 3);
                return true;
            }

            if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sOnly))
            {
                seconds = Math.Round(sOnly, 3);
                return true;
            }
        }
        catch
        {
            // Ignore format errors
        }

        return false;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteProcessAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {psi.FileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            throw new TimeoutException($"Process '{psi.FileName}' timed out after {timeout.TotalSeconds}s.");
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
