using System.Text;
using System.Text.Json;

namespace VideoEnhancer.Router;

/// <summary>
/// 路由器配置。默认读取与 EXE 同目录的 <c>videoenhancer-router.config.json</c>（UTF-8，可带/不带 BOM）。
/// 所有相对路径按 EXE 所在目录解析，绝不按当前工作目录解析（宿主会把子进程 cwd 设为 3FUI 工作目录）。
/// </summary>
internal sealed class RouterConfig
{
    public const string FileName = "videoenhancer-router.config.json";

    /// <summary>当前实例实际使用的配置文件名（下游复用者可换成自己的设置文件名）。</summary>
    private string _fileName = FileName;

    /// <summary>3FUI 原生 ffmpeg（Anime4K/普通预设走这里）。</summary>
    public string ThreeFuiFfmpeg { get; private set; } = "";

    /// <summary>3FUI 的 ffprobe（宿主"FFprobe获取时长"阶段与抽流工具走这里）。</summary>
    public string ThreeFuiFfprobe { get; private set; } = "";

    /// <summary>Topaz 自带 ffmpeg（tvai_ 滤镜走这里）。</summary>
    public string TopazFfmpeg { get; private set; } = "";

    /// <summary>插件后端 videoenhancer.exe（含 -modelpath/-ffmpeg-settings 等插件参数的任务走这里）。</summary>
    public string PluginExe { get; private set; } = "";

    /// <summary>Topaz 编排器 videoenhancer-tvai.exe（含 --tvai-* 顶层标记的任务走这里）。</summary>
    public string TvaiOrchestrator { get; private set; } = "";

    /// <summary>仅注入 Topaz 分支子进程的环境变量（TVAI_MODEL_DIR / TVAI_MODEL_DATA_DIR 等）。</summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>路由日志文件（可空 = 不记录）。相对路径按 EXE 目录解析。</summary>
    public string LogPath { get; private set; } = "";

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(BaseDirectory, FileName);

    /// <summary>
    /// 读取配置；<paramref name="fileName"/> 允许下游复用者使用自己的设置文件名（默认与本路由器一致）。
    /// 缺省时相对路径按 EXE 目录解析，语义与 <see cref="TryLoad(out RouterConfig, out string)"/> 相同。
    /// </summary>
    public static bool TryLoad(string fileName, out RouterConfig config, out string error)
    {
        config = new RouterConfig();
        error = "";
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            config._fileName = fileName.Trim();
        }

        var path = Path.Combine(BaseDirectory, config._fileName);
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                config.ThreeFuiFfmpeg = ReadString(root, "threeFuiFfmpeg");
                config.ThreeFuiFfprobe = ReadString(root, "threeFuiFfprobe");
                config.TopazFfmpeg = ReadString(root, "topazFfmpeg");
                config.PluginExe = ReadString(root, "pluginExe");
                config.TvaiOrchestrator = ReadString(root, "tvaiOrchestrator");
                config.LogPath = ReadString(root, "logPath");

                if (root.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in envElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            config.EnvironmentVariables[property.Name] = property.Value.GetString() ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"读取路由器配置失败：{path}{Environment.NewLine}{ex.Message}";
                return false;
            }
        }

        config.ApplyDefaults();
        return true;
    }

    public static bool TryLoad(out RouterConfig config, out string error)
        => TryLoad(FileName, out config, out error);

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? (element.GetString() ?? "").Trim()
            : "";

    /// <summary>配置缺省时按 3FUI 安装布局推导；不写入磁盘，只在内存中生效。</summary>
    private void ApplyDefaults()
    {
        var hostRoot = TryFindHostRoot();
        if (string.IsNullOrEmpty(ThreeFuiFfmpeg) && hostRoot.Length > 0)
        {
            ThreeFuiFfmpeg = Path.Combine(hostRoot, "ffmpeg.exe");
        }

        if (string.IsNullOrEmpty(ThreeFuiFfprobe) && hostRoot.Length > 0)
        {
            ThreeFuiFfprobe = Path.Combine(hostRoot, "bin", "ffmpeg", "ffprobe.exe");
        }

        if (string.IsNullOrEmpty(PluginExe))
        {
            PluginExe = Path.Combine(BaseDirectory, "videoenhancer.exe");
        }

        if (string.IsNullOrEmpty(TvaiOrchestrator))
        {
            TvaiOrchestrator = Path.Combine(BaseDirectory, "videoenhancer-tvai.exe");
        }
    }

    /// <summary>从 EXE 所在目录向上寻找包含 FFmpegFreeUI.exe 的宿主根目录；找不到时退回上两级（Plugin\videoenhancer → 3FUI 根）。</summary>
    private static string TryFindHostRoot()
    {
        try
        {
            var directory = new DirectoryInfo(BaseDirectory);
            var upTwo = directory.Parent?.Parent;
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "FFmpegFreeUI.exe")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return upTwo?.FullName ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>解析为绝对路径：相对路径按 EXE 目录解析。</summary>
    public static string ResolvePath(string value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : Path.GetFullPath(value, BaseDirectory);

    /// <summary>追加一行路由日志；任何失败都静默忽略（日志绝不能影响视频任务）。</summary>
    public void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(LogPath))
        {
            return;
        }

        try
        {
            var path = ResolvePath(LogPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // 忽略：日志失败不影响转发。
        }
    }
}
