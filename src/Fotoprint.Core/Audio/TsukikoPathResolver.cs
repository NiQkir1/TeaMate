namespace Fotoprint.Core.Audio;

public static class TsukikoPathResolver
{
    public const string DefaultMacCliPath = "/Applications/tsukiko.app/Contents/Helpers/tsukiko-transcribe";

    private static readonly string[] CandidateBinaryRelativePaths =
    [
        Path.Combine("tools", "tsukiko", "tsukiko-transcribe.exe"),
        Path.Combine("tools", "tsukiko", "tsukiko-transcribe"),
        Path.Combine("tools", "tsukiko", "Helpers", "tsukiko-transcribe.exe"),
        Path.Combine("tools", "tsukiko", "Helpers", "tsukiko-transcribe"),
        Path.Combine("tools", "tsukiko", "tsukiko.app", "Contents", "Helpers", "tsukiko-transcribe")
    ];

    private static readonly string[] PreferredModelFileNames =
    [
        "ggml-large-v3-turbo.bin",
        "ggml-base.bin",
        "ggml-small.bin",
        "ggml-medium.bin",
        "ggml-large-v3.bin"
    ];

    /// <summary>
    /// Resolves the path to tsukiko-transcribe(.exe) across Windows, macOS, and project-local tools/tsukiko/.
    /// Priority:
    /// 1. Explicit TSUKIKO_CLI_PATH environment variable (if file exists)
    /// 2. Explicit configuredPath from config.yaml (if file exists and is not a missing default path)
    /// 3. Project-local portable directory: tools/tsukiko/tsukiko-transcribe[.exe]
    /// 4. Standard OS installation paths (Windows %LOCALAPPDATA%/Program Files, macOS /Applications & ~/Applications)
    /// 5. Returns null if not found (activates Zero-Install Cloud Mode).
    /// </summary>
    public static string? ResolveCliPath(string? configuredPath = null, string? customSearchRoot = null)
    {
        // 1. Explicit TSUKIKO_CLI_PATH environment variable
        var envCliPath = Environment.GetEnvironmentVariable("TSUKIKO_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(envCliPath) && File.Exists(envCliPath.Trim()))
        {
            return Path.GetFullPath(envCliPath.Trim());
        }

        // If customSearchRoot is explicitly provided (e.g. isolated portable directory or test fixture),
        // check explicit non-default configuredPath first, then search within customSearchRoot only.
        if (!string.IsNullOrWhiteSpace(customSearchRoot))
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) &&
                !IsDefaultConfigPath(configuredPath) &&
                File.Exists(configuredPath.Trim()))
            {
                return Path.GetFullPath(configuredPath.Trim());
            }

            return SearchPortableCliInRoot(customSearchRoot.Trim(), maxParentLevels: 0);
        }

        // 2. Explicit configuredPath from config.yaml
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmedConfigPath = configuredPath.Trim();
            if (File.Exists(trimmedConfigPath))
            {
                return Path.GetFullPath(trimmedConfigPath);
            }

            // If caller specified a custom non-default path that does not exist, respect that and do not fall back to global OS paths
            if (!IsDefaultConfigPath(trimmedConfigPath))
            {
                var portableFromCustom = SearchPortableCliAcrossRoots();
                return portableFromCustom;
            }
        }

        // 3. Project-local portable directory: tools/tsukiko/tsukiko-transcribe[.exe]
        var portableMatch = SearchPortableCliAcrossRoots();
        if (!string.IsNullOrWhiteSpace(portableMatch))
        {
            return portableMatch;
        }

        // 4. Standard OS installation paths
        foreach (var osPath in GetStandardOsCliPaths())
        {
            if (!string.IsNullOrWhiteSpace(osPath) && File.Exists(osPath))
            {
                return osPath;
            }
        }

        // 5. Not found -> Zero-Install Cloud Mode
        return null;
    }

    /// <summary>
    /// Resolves the local Whisper GGML model file (ggml-large-v3-turbo.bin or ggml-base.bin)
    /// across portable tools/tsukiko/models and OS AppData directories.
    /// </summary>
    public static string? ResolveModelPath(string? customSearchRoot = null)
    {
        var envModelPath = Environment.GetEnvironmentVariable("TSUKIKO_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envModelPath) && File.Exists(envModelPath.Trim()))
        {
            return Path.GetFullPath(envModelPath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(customSearchRoot))
        {
            return SearchModelInRoot(customSearchRoot.Trim(), maxParentLevels: 0);
        }

        var roots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var found = SearchModelInRoot(root, maxParentLevels: 6);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        foreach (var modelDir in GetStandardOsModelDirectories())
        {
            var found = FindModelInDirectory(modelDir);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsDefaultConfigPath(string path)
    {
        return string.Equals(path, DefaultMacCliPath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "auto", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SearchPortableCliAcrossRoots()
    {
        var roots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var found = SearchPortableCliInRoot(root, maxParentLevels: 6);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        return null;
    }

    private static string? SearchPortableCliInRoot(string startDir, int maxParentLevels)
    {
        try
        {
            var current = startDir;
            for (int level = 0; level <= maxParentLevels && !string.IsNullOrWhiteSpace(current); level++)
            {
                foreach (var relPath in CandidateBinaryRelativePaths)
                {
                    var candidate = Path.Combine(current, relPath);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }
        catch
        {
            // Ignore directory traversal errors
        }

        return null;
    }

    private static string? SearchModelInRoot(string startDir, int maxParentLevels)
    {
        try
        {
            var current = startDir;
            for (int level = 0; level <= maxParentLevels && !string.IsNullOrWhiteSpace(current); level++)
            {
                var portableModelsDir = Path.Combine(current, "tools", "tsukiko", "models");
                var found = FindModelInDirectory(portableModelsDir);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }

                var directModelsDir = Path.Combine(current, "models");
                found = FindModelInDirectory(directModelsDir);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }
        catch
        {
            // Ignore directory traversal errors
        }

        return null;
    }

    private static string? FindModelInDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return null;
        }

        try
        {
            foreach (var modelName in PreferredModelFileNames)
            {
                var candidate = Path.Combine(directoryPath, modelName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            var anyBin = Directory.EnumerateFiles(directoryPath, "ggml-*.bin").FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(anyBin))
            {
                return Path.GetFullPath(anyBin);
            }
        }
        catch
        {
            // Ignore permission errors
        }

        return null;
    }

    private static IEnumerable<string> GetStandardOsCliPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "tsukiko", "tsukiko-transcribe.exe");
            yield return Path.Combine(localAppData, "tsukiko", "tsukiko-transcribe.exe");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "tsukiko", "tsukiko-transcribe.exe");
        }

        yield return @"C:\Program Files\tsukiko\tsukiko-transcribe.exe";
        yield return DefaultMacCliPath;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "Applications", "tsukiko.app", "Contents", "Helpers", "tsukiko-transcribe");
        }
    }

    private static IEnumerable<string> GetStandardOsModelDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "app.yuko.tsukiko", "models");
            yield return Path.Combine(appData, "tsukiko", "models");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "app.yuko.tsukiko", "models");
            yield return Path.Combine(localAppData, "tsukiko", "models");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "Library", "Application Support", "app.yuko.tsukiko", "models");
            yield return Path.Combine(userProfile, "Library", "Application Support", "tsukiko", "models");
        }
    }
}
