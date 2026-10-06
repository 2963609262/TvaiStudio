using System.Text;
using System.Text.Json;

namespace TvaiStudio;

/// <summary>
/// TvaiStudio 自身设置，保存到 EXE 同目录的 <c>tvaistudio.config.json</c>。
/// 路径来源：自动探测（Topaz 默认安装位置）→ 用户显式指定 → 落盘。
/// </summary>
internal sealed class StudioSettings
{
    public const string FileName = "tvaistudio.config.json";

    /// <summary>Topaz Video AI 自带的 ffmpeg.exe —— 唯一含 tvai_up / tvai_pe 滤镜的构建。</summary>
    public string TopazFfmpeg { get; set; } = "";

    /// <summary>模型定义目录（TVAI_MODEL_DIR，含 &lt;id&gt;.json）。</summary>
    public string ModelDir { get; set; } = "";

    /// <summary>权重目录（TVAI_MODEL_DATA_DIR，含 *.tz / *.tz3）。</summary>
    public string ModelDataDir { get; set; } = "";

    public string OutputDirectory { get; set; } = "";

    public string EncoderKey { get; set; } = "hevc";

    /// <summary>质量控制方式（NVENC）：CQP=恒定 qp，VBR=目标 cq。与 QualityValue 成对生效。</summary>
    public string RateControl { get; set; } = "VBR";

    /// <summary>质量值（CQP 的 qp / VBR 的 cq），1..51。</summary>
    public int QualityValue { get; set; } = 28;

    public string Mode { get; set; } = "auto";

    public int EstimateFrames { get; set; } = 8;

    public double SampleSeconds { get; set; } = 8;

    public int Scale { get; set; } = 2;

    public bool ForceCfr { get; set; }

    public string Model { get; set; } = "rhea-1";

    /// <summary>最近一次使用的输入目录（文件对话框起点）。</summary>
    public string LastInputDirectory { get; set; } = "";

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static StudioSettings Load()
    {
        StudioSettings settings;
        try
        {
            settings = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(ConfigPath, Encoding.UTF8))
                  ?? new StudioSettings()
                : new StudioSettings();
        }
        catch
        {
            // 配置损坏时退回默认值并重新探测，不阻断启动。
            settings = new StudioSettings();
        }

        settings.ApplyDetectedDefaults();
        return settings;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
        }
        catch
        {
            // 设置保存失败不影响转码。
        }
    }

    /// <summary>空缺项按本机常见安装位置探测；探测不到就留空，由界面提示用户指定。</summary>
    public void ApplyDetectedDefaults()
    {
        if (string.IsNullOrWhiteSpace(TopazFfmpeg) || !File.Exists(TopazFfmpeg))
        {
            TopazFfmpeg = TopazLocator.FindFfmpeg() ?? TopazFfmpeg;
        }

        if (string.IsNullOrWhiteSpace(ModelDir) || !Directory.Exists(ModelDir))
        {
            ModelDir = TopazLocator.FindModelDir() ?? ModelDir;
        }

        if (string.IsNullOrWhiteSpace(ModelDataDir) || !Directory.Exists(ModelDataDir))
        {
            ModelDataDir = TopazLocator.FindModelDataDir() ?? ModelDataDir;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "TvaiStudio");
        }
    }

    /// <summary>供子进程使用的环境变量表（TVAI_* 缺一不可，否则 Topaz 报 Model not found）。</summary>
    public Dictionary<string, string> BuildEnvironmentVariables()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ModelDir))
        {
            env["TVAI_MODEL_DIR"] = ModelDir;
        }

        if (!string.IsNullOrWhiteSpace(ModelDataDir))
        {
            env["TVAI_MODEL_DATA_DIR"] = ModelDataDir;
        }

        return env;
    }

    /// <summary>配置是否足以运行：Topaz ffmpeg 存在且两个模型目录都配置了。</summary>
    public bool IsRunnable(out string problem)
    {
        if (string.IsNullOrWhiteSpace(TopazFfmpeg) || !File.Exists(TopazFfmpeg))
        {
            problem = "未找到 Topaz 自带的 ffmpeg.exe（tvai_up 滤镜只存在于该构建中）。请指定其路径。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ModelDir) || !Directory.Exists(ModelDir))
        {
            problem = $"模型定义目录不存在：{ModelDir}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ModelDataDir) || !Directory.Exists(ModelDataDir))
        {
            problem = $"权重目录不存在：{ModelDataDir}";
            return false;
        }

        problem = "";
        return true;
    }
}
