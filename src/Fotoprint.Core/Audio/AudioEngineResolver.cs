using System.Runtime.InteropServices;
using Fotoprint.Core.Configuration;

namespace Fotoprint.Core.Audio;

public interface IAudioEngineResolver
{
    string? ResolveEnginePath(string? customSearchRoot = null);
    string? ResolveAudioConverterPath(string? customSearchRoot = null);
    string? ResolveModelPath(string? customSearchRoot = null);
    string GetProviderName();
}

public class AudioEngineResolver : IAudioEngineResolver
{
    private readonly FotoprintConfig? _config;

    public AudioEngineResolver(FotoprintConfig? config = null)
    {
        _config = config;
    }

    public string? ResolveEnginePath(string? customSearchRoot = null)
    {
        if (customSearchRoot == null)
        {
            var envPath = Environment.GetEnvironmentVariable("AUDIO_ENGINE_PATH");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                return Path.GetFullPath(envPath);
            }
        }

        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // Relative engine relative candidates
        var macCandidates = new[]
        {
            Path.Combine("tools", "engine", "macos", "tsukiko-recognizer")
        };

        var winCandidates = new[]
        {
            Path.Combine("tools", "engine", "win", "tsukiko-recognizer-vulkan.exe"),
            Path.Combine("tools", "engine", "win", "tsukiko-recognizer-cpu.exe")
        };

        if (!string.IsNullOrWhiteSpace(customSearchRoot))
        {
            // When customSearchRoot is explicitly provided (e.g. in unit tests), check OS-native path first,
            // then check both Mac and Windows candidates inside customSearchRoot.
            var primaryCandidates = isWindows ? winCandidates : macCandidates;
            var secondaryCandidates = isWindows ? macCandidates : winCandidates;

            foreach (var rel in primaryCandidates)
            {
                var full = Path.Combine(customSearchRoot, rel);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }

            foreach (var rel in secondaryCandidates)
            {
                var full = Path.Combine(customSearchRoot, rel);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }

            return null;
        }

        var roots = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        var targetCandidates = isWindows ? winCandidates : macCandidates;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            var current = root;
            for (int i = 0; i <= 6; i++)
            {
                foreach (var rel in targetCandidates)
                {
                    var combined = Path.Combine(current, rel);
                    if (File.Exists(combined))
                    {
                        return Path.GetFullPath(combined);
                    }
                }

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        return null;
    }

    public string? ResolveAudioConverterPath(string? customSearchRoot = null)
    {
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (isMac)
        {
            const string afconvert = "/usr/bin/afconvert";
            if (File.Exists(afconvert))
            {
                return afconvert;
            }
        }

        var relWinFfmpeg = Path.Combine("tools", "engine", "win", "ffmpeg.exe");

        if (!string.IsNullOrWhiteSpace(customSearchRoot))
        {
            var combined = Path.Combine(customSearchRoot, relWinFfmpeg);
            if (File.Exists(combined)) return Path.GetFullPath(combined);
        }

        var roots = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            var current = root;
            for (int i = 0; i <= 6; i++)
            {
                var combined = Path.Combine(current, relWinFfmpeg);
                if (File.Exists(combined))
                {
                    return Path.GetFullPath(combined);
                }

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        if (isMac && File.Exists("/usr/bin/afconvert"))
        {
            return "/usr/bin/afconvert";
        }

        return null;
    }

    public string? ResolveModelPath(string? customSearchRoot = null)
    {
        // 1. Env vars (when customSearchRoot == null)
        if (customSearchRoot == null)
        {
            var envModel = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH")
                           ?? Environment.GetEnvironmentVariable("TSUKIKO_MODEL_PATH");
            if (!string.IsNullOrWhiteSpace(envModel) && File.Exists(envModel))
            {
                return Path.GetFullPath(envModel);
            }
        }

        var modelNames = new[]
        {
            Path.Combine("models", "ggml-large-v3-turbo.bin"),
            Path.Combine("models", "ggml-base.bin")
        };

        // 2. Relative to customSearchRoot
        if (!string.IsNullOrWhiteSpace(customSearchRoot))
        {
            foreach (var rel in modelNames)
            {
                var combined = Path.Combine(customSearchRoot, rel);
                if (File.Exists(combined)) return Path.GetFullPath(combined);
            }
            return null;
        }

        // Search up to 6 parent levels
        var roots = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            var current = root;
            for (int i = 0; i <= 6; i++)
            {
                foreach (var rel in modelNames)
                {
                    var combined = Path.Combine(current, rel);
                    if (File.Exists(combined))
                    {
                        return Path.GetFullPath(combined);
                    }
                }

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        // 3. Standard OS model caches
        var osCachePaths = new List<string>();

        // macOS cache
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var macDir = Path.Combine(userProfile, "Library", "Application Support", "app.yuko.tsukiko", "models");
            osCachePaths.Add(Path.Combine(macDir, "ggml-large-v3-turbo.bin"));
            osCachePaths.Add(Path.Combine(macDir, "ggml-base.bin"));
        }

        // Windows cache
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var winDir = Path.Combine(appData, "app.yuko.tsukiko", "models");
            osCachePaths.Add(Path.Combine(winDir, "ggml-large-v3-turbo.bin"));
            osCachePaths.Add(Path.Combine(winDir, "ggml-base.bin"));
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var winDir = Path.Combine(localAppData, "app.yuko.tsukiko", "models");
            osCachePaths.Add(Path.Combine(winDir, "ggml-large-v3-turbo.bin"));
            osCachePaths.Add(Path.Combine(winDir, "ggml-base.bin"));
        }

        foreach (var candidate in osCachePaths)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public string GetProviderName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "local-metal";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var enginePath = ResolveEnginePath();
            if (!string.IsNullOrWhiteSpace(enginePath) &&
                enginePath.Contains("tsukiko-recognizer-cpu", StringComparison.OrdinalIgnoreCase))
            {
                return "local-cpu";
            }
            return "local-vulkan";
        }

        return "local-metal";
    }
}
