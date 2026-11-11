using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fotoprint.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Fotoprint.Core.Audio;

public class TsukikoTranscriber : IAudioTranscriber
{
    public const string ProviderName = "tsukiko-metal";

    private static readonly Regex TimestampLineRegex = new(
        @"^\s*\[(?<start>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{1,3})\]\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly FotoprintConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TsukikoTranscriber>? _logger;

    public TsukikoTranscriber(
        FotoprintConfig config,
        HttpClient? httpClient = null,
        ILogger<TsukikoTranscriber>? logger = null)
    {
        _config = config ?? new FotoprintConfig();
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_config.Audio.Enabled)
            {
                return false;
            }

            var cliPath = TsukikoPathResolver.ResolveCliPath(_config.Audio.Tsukiko.CliPath);
            if (!string.IsNullOrWhiteSpace(cliPath) && File.Exists(cliPath))
            {
                return true;
            }

            var envUrl = Environment.GetEnvironmentVariable("TRANSCRIBER_URL");
            var httpUrl = !string.IsNullOrWhiteSpace(envUrl)
                ? envUrl
                : _config.Audio.Tsukiko.HttpUrl;

            if (!string.IsNullOrWhiteSpace(httpUrl))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));

                var healthEndpoint = $"{httpUrl.TrimEnd('/')}/health";
                using var response = await _httpClient.GetAsync(healthEndpoint, cts.Token);
                return response.IsSuccessStatusCode;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[TsukikoTranscriber] Availability check returned false.");
            return false;
        }
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
                Provider: ProviderName,
                DurationMs: 0,
                ErrorMessage: "Audio stream is null.");
        }

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".wav";
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"fotoprint_audio_{Guid.NewGuid():N}{ext}");
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
            _logger?.LogError(ex, "[TsukikoTranscriber] Failed to process stream for {FileName}", fileName);
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: ProviderName,
                DurationMs: 0,
                ErrorMessage: $"Tsukiko stream processing error: {ex.Message}");
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
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: ProviderName,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"Audio file not found: {filePath}");
        }

        var lang = string.IsNullOrWhiteSpace(language) ? "ru" : language.Trim();
        var cliPath = TsukikoPathResolver.ResolveCliPath(_config.Audio.Tsukiko.CliPath);
        var envTranscriberUrl = Environment.GetEnvironmentVariable("TRANSCRIBER_URL");

        // 1. If TRANSCRIBER_URL is explicitly configured, try HTTP first
        if (!string.IsNullOrWhiteSpace(envTranscriberUrl))
        {
            var httpResult = await TryTranscribeViaHttpAsync(envTranscriberUrl, filePath, lang, sw, ct);
            if (httpResult.Success || string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
            {
                return httpResult;
            }
        }

        // 2. Local CLI execution (Tsukiko Metal / Vulkan GPU)
        if (!string.IsNullOrWhiteSpace(cliPath) && File.Exists(cliPath))
        {
            try
            {
                var isTsukikoBinary = Path.GetFileNameWithoutExtension(cliPath)
                    .Equals("tsukiko-transcribe", StringComparison.OrdinalIgnoreCase);

                if (isTsukikoBinary)
                {
                    var statusInfo = await ProbeTsukikoStatusAsync(cliPath, ct);
                    var resolvedModel = statusInfo?.ModelPath;
                    if (string.IsNullOrWhiteSpace(resolvedModel) || !File.Exists(resolvedModel))
                    {
                        resolvedModel = TsukikoPathResolver.ResolveModelPath();
                    }

                    if (statusInfo is { IsBusyWithoutAppApi: true, EnginePath: not null } &&
                        File.Exists(statusInfo.EnginePath) &&
                        !string.IsNullOrWhiteSpace(resolvedModel) &&
                        File.Exists(resolvedModel))
                    {
                        _logger?.LogInformation(
                            "[TsukikoTranscriber] tsukiko-transcribe reported busy=true, appApi=false. Using direct GPU engine {Engine}",
                            statusInfo.EnginePath);

                        return await RunDirectRecognizerAsync(
                            statusInfo.EnginePath,
                            resolvedModel,
                            filePath,
                            lang,
                            sw,
                            ct);
                    }
                }

                var cliResult = await RunStandardCliAsync(cliPath, filePath, lang, sw, ct);
                if (!cliResult.Success && isTsukikoBinary)
                {
                    var fallbackStatus = await ProbeTsukikoStatusAsync(cliPath, ct);
                    var resolvedModel = fallbackStatus?.ModelPath;
                    if (string.IsNullOrWhiteSpace(resolvedModel) || !File.Exists(resolvedModel))
                    {
                        resolvedModel = TsukikoPathResolver.ResolveModelPath();
                    }

                    if (fallbackStatus is { EnginePath: not null } &&
                        File.Exists(fallbackStatus.EnginePath) &&
                        !string.IsNullOrWhiteSpace(resolvedModel) &&
                        File.Exists(resolvedModel))
                    {
                        _logger?.LogInformation(
                            "[TsukikoTranscriber] Standard CLI failed ({Error}); retrying via direct GPU recognizer.",
                            cliResult.ErrorMessage);
                        return await RunDirectRecognizerAsync(
                            fallbackStatus.EnginePath,
                            resolvedModel,
                            filePath,
                            lang,
                            sw,
                            ct);
                    }
                }

                return cliResult;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[TsukikoTranscriber] CLI execution error on {FilePath}", filePath);
                return new TranscriptionResult(
                    Success: false,
                    Text: string.Empty,
                    Segments: new List<AudioSegment>(),
                    Provider: ProviderName,
                    DurationMs: sw.ElapsedMilliseconds,
                    ErrorMessage: $"Tsukiko CLI exception: {ex.Message}");
            }
        }

        // 3. Fallback to configured HttpUrl if CLI does not exist on host
        if (!string.IsNullOrWhiteSpace(_config.Audio.Tsukiko.HttpUrl))
        {
            return await TryTranscribeViaHttpAsync(_config.Audio.Tsukiko.HttpUrl, filePath, lang, sw, ct);
        }

        return new TranscriptionResult(
            Success: false,
            Text: string.Empty,
            Segments: new List<AudioSegment>(),
            Provider: ProviderName,
            DurationMs: sw.ElapsedMilliseconds,
            ErrorMessage: $"Tsukiko CLI not found at '{cliPath}' and no HTTP URL is reachable.");
    }

    private async Task<TranscriptionResult> RunStandardCliAsync(
        string cliPath,
        string filePath,
        string language,
        Stopwatch sw,
        CancellationToken ct)
    {
        var timeoutSeconds = _config.Audio.Tsukiko.TimeoutSeconds > 0
            ? _config.Audio.Tsukiko.TimeoutSeconds
            : 300;

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--lang");
        psi.ArgumentList.Add(language);
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add(filePath);

        var (exitCode, stdout, stderr) = await ExecuteProcessAsync(psi, TimeSpan.FromSeconds(timeoutSeconds), ct);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            var errMsg = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : $"Tsukiko CLI exited with code {exitCode}.";
            _logger?.LogWarning("[TsukikoTranscriber] CLI exited with code {ExitCode}: {Error}", exitCode, errMsg);
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: ProviderName,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: errMsg);
        }

        var parsed = ParseCliOutput(stdout, sw.ElapsedMilliseconds, ProviderName);
        if (!parsed.Success && !string.IsNullOrWhiteSpace(stderr))
        {
            return parsed with { ErrorMessage = stderr.Trim() };
        }

        return parsed;
    }

    private async Task<TranscriptionResult> RunDirectRecognizerAsync(
        string enginePath,
        string modelPath,
        string filePath,
        string language,
        Stopwatch sw,
        CancellationToken ct)
    {
        var timeoutSeconds = _config.Audio.Tsukiko.TimeoutSeconds > 0
            ? _config.Audio.Tsukiko.TimeoutSeconds
            : 300;

        var tempWavPath = Path.Combine(Path.GetTempPath(), $"fotoprint_pcm_{Guid.NewGuid():N}.wav");
        var tempOutPrefix = Path.Combine(Path.GetTempPath(), $"fotoprint_out_{Guid.NewGuid():N}");
        var tempOutJsonPath = tempOutPrefix + ".json";

        try
        {
            var wavInputPath = filePath;
            const string afconvertPath = "/usr/bin/afconvert";
            if (File.Exists(afconvertPath))
            {
                var afPsi = new ProcessStartInfo
                {
                    FileName = afconvertPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                afPsi.ArgumentList.Add("-f");
                afPsi.ArgumentList.Add("WAVE");
                afPsi.ArgumentList.Add("-d");
                afPsi.ArgumentList.Add("LEI16@16000");
                afPsi.ArgumentList.Add("-c");
                afPsi.ArgumentList.Add("1");
                afPsi.ArgumentList.Add(filePath);
                afPsi.ArgumentList.Add(tempWavPath);

                var (afExit, _, afErr) = await ExecuteProcessAsync(afPsi, TimeSpan.FromSeconds(30), ct);
                if (afExit == 0 && File.Exists(tempWavPath))
                {
                    wavInputPath = tempWavPath;
                }
                else
                {
                    _logger?.LogWarning(
                        "[TsukikoTranscriber] afconvert exited with {ExitCode} ({Error}); passing original file to recognizer.",
                        afExit,
                        afErr.Trim());
                }
            }

            var recPsi = new ProcessStartInfo
            {
                FileName = enginePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            recPsi.ArgumentList.Add("-m");
            recPsi.ArgumentList.Add(modelPath);
            recPsi.ArgumentList.Add("-l");
            recPsi.ArgumentList.Add(language);
            recPsi.ArgumentList.Add("-np");
            recPsi.ArgumentList.Add("-oj");
            recPsi.ArgumentList.Add("-of");
            recPsi.ArgumentList.Add(tempOutPrefix);
            recPsi.ArgumentList.Add("-f");
            recPsi.ArgumentList.Add(wavInputPath);

            var (recExit, recStdout, recStderr) = await ExecuteProcessAsync(recPsi, TimeSpan.FromSeconds(timeoutSeconds), ct);

            if (File.Exists(tempOutJsonPath))
            {
                var jsonText = await File.ReadAllTextAsync(tempOutJsonPath, ct);
                return ParseCliOutput(jsonText, sw.ElapsedMilliseconds, ProviderName);
            }

            if (!string.IsNullOrWhiteSpace(recStdout))
            {
                return ParseCliOutput(recStdout, sw.ElapsedMilliseconds, ProviderName);
            }

            var errorText = !string.IsNullOrWhiteSpace(recStderr)
                ? recStderr.Trim()
                : $"tsukiko-recognizer exited with code {recExit} and produced no output.";

            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: ProviderName,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: errorText);
        }
        finally
        {
            TryDeleteFile(tempWavPath);
            TryDeleteFile(tempOutJsonPath);
        }
    }

    private sealed record TsukikoStatusInfo(
        bool IsBusyWithoutAppApi,
        string? EnginePath,
        string? ModelPath);

    private async Task<TsukikoStatusInfo?> ProbeTsukikoStatusAsync(string cliPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("--status");

            var (_, stdout, _) = await ExecuteProcessAsync(psi, TimeSpan.FromSeconds(5), ct);
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var busy = root.TryGetProperty("busy", out var busyProp) && busyProp.ValueKind == JsonValueKind.True;
            var appApi = root.TryGetProperty("appApi", out var appApiProp) && appApiProp.ValueKind == JsonValueKind.True;
            var engine = root.TryGetProperty("engine", out var engProp) ? engProp.GetString() : null;
            var model = root.TryGetProperty("model", out var modProp) ? modProp.GetString() : null;

            return new TsukikoStatusInfo(
                IsBusyWithoutAppApi: busy && !appApi,
                EnginePath: engine,
                ModelPath: model);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[TsukikoTranscriber] Status probe failed.");
            return null;
        }
    }

    private async Task<TranscriptionResult> TryTranscribeViaHttpAsync(
        string baseUrl,
        string filePath,
        string language,
        Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            var endpoint = baseUrl.TrimEnd('/');
            if (!endpoint.EndsWith("/transcribe", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/transcribe";
            }

            using var form = new MultipartFormDataContent();
            var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", Path.GetFileName(filePath));
            form.Add(new StringContent(language), "language");

            using var response = await _httpClient.PostAsync(endpoint, form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new TranscriptionResult(
                    Success: false,
                    Text: string.Empty,
                    Segments: new List<AudioSegment>(),
                    Provider: ProviderName,
                    DurationMs: sw.ElapsedMilliseconds,
                    ErrorMessage: $"Tsukiko HTTP endpoint returned {(int)response.StatusCode}: {body}");
            }

            return ParseCliOutput(body, sw.ElapsedMilliseconds, ProviderName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TsukikoTranscriber] HTTP transcription failed against {BaseUrl}", baseUrl);
            return new TranscriptionResult(
                Success: false,
                Text: string.Empty,
                Segments: new List<AudioSegment>(),
                Provider: ProviderName,
                DurationMs: sw.ElapsedMilliseconds,
                ErrorMessage: $"Tsukiko HTTP request failed: {ex.Message}");
        }
    }

    public static TranscriptionResult ParseCliOutput(
        string rawOutput,
        long durationMs,
        string provider = ProviderName)
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

        // 1. Try JSON parsing (either direct JSON or embedded JSON object)
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

                    // Format B: Standard Tsukiko CLI JSON with "text" and optional "segments"
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
                // Fall through to timestamped text / plain text parsing
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
        if (parts.Length == 3 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            seconds = Math.Round(hours * 3600 + minutes * 60 + secs, 3);
            return true;
        }

        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mins2) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs2))
        {
            seconds = Math.Round(mins2 * 60 + secs2, 3);
            return true;
        }

        return false;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteProcessAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
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
            catch
            {
                // Ignore kill errors
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
