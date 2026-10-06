namespace TvaiStudio;

/// <summary>
/// 定位本机的 Topaz Video AI 安装。只做只读探测，绝不复制/下载任何 Topaz 文件
/// （Topaz EULA 禁止再分发其可执行文件与模型）。
/// </summary>
internal static class TopazLocator
{
    /// <summary>按常见安装位置查找 Topaz 自带 ffmpeg.exe；找不到返回 null。</summary>
    public static string? FindFfmpeg()
    {
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>模型定义目录（含 &lt;id&gt;.json）：优先 ProgramData，其次安装目录内 models。</summary>
    public static string? FindModelDir()
    {
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Topaz Labs LLC",
            "Topaz Video",
            "models");
        if (Directory.Exists(programData))
        {
            return programData;
        }

        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "models");
            if (Directory.Exists(candidate) && ContainsJson(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>权重目录（含 *.tz / *.tz3）：优先安装目录内 models（与模型 json 目录通常不同）。</summary>
    public static string? FindModelDataDir()
    {
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "models");
            if (Directory.Exists(candidate) && ContainsWeights(candidate))
            {
                return candidate;
            }
        }

        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Topaz Labs LLC",
            "Topaz Video",
            "models");
        return Directory.Exists(programData) ? programData : null;
    }

    /// <summary>Topaz Video AI 的候选安装根目录（按版本号从新到旧）。</summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var bases = new[]
        {
            @"D:\Topaz Labs LLC",
            @"C:\Program Files\Topaz Labs LLC",
            @"C:\Program Files (x86)\Topaz Labs LLC",
        };

        foreach (var basePath in bases)
        {
            if (!Directory.Exists(basePath))
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(basePath, "Topaz Video*");
            }
            catch
            {
                continue;
            }

            // 版本号降序：Topaz Video 1.2.1 优先于 Topaz Video 1.0.4。
            foreach (var directory in directories
                         .OrderByDescending(d => ParseVersion(d))
                         .ThenByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }

    private static Version ParseVersion(string path)
    {
        var name = Path.GetFileName(path);
        var digits = new string(name.Where(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }

    private static bool ContainsJson(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.json").Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsWeights(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.tz3").Any()
                   || Directory.EnumerateFiles(directory, "*.tz").Any();
        }
        catch
        {
            return false;
        }
    }
}
