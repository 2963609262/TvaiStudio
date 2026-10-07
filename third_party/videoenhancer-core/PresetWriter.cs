using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoEnhancer.Tvai;

namespace VideoEnhancer.TvaiTuner;

/// <summary>
/// 把调参器的当前状态写成 3FUI 预设（schema v6），可直接放入 Preset_v6\User 入队使用。
/// 导出保证：强制「视频参数_色彩管理_像素格式=yuv420p」；manual 的 6 个绝对值内联进
/// 自定义滤镜内容（编排器命令行没有参数值通道），auto/relative 只写 --tvai-* 标记。
/// </summary>
internal static class PresetWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record ExportRequest(
        TvaiModel Model,
        string Mode,
        int EstimateFrames,
        int? Scale,
        int? TargetWidth,
        int? TargetHeight,
        IReadOnlyDictionary<string, double>? ManualValues,
        IReadOnlyDictionary<string, double>? RelativeOffsets,
        double? Prenoise,
        double? Grain,
        double? Gsize,
        string? Parameters,
        EncoderProfile Encoder,
        bool ForceCfr,
        string PresetName,
        string ExportDirectory);

    // tvai_up 滤镜对这三个选项是硬拒绝（实测：越界直接报 "Value x for parameter 'y' out of
    // range [a - b]" 且进程退出非 0，不是静默截断），因此 UI 侧与编排器侧都要按此钳制，
    // 否则用户填一个 0.5 的 prenoise 会得到一次失败的编码而不是一个可用的值。
    public const double PrenoiseMin = 0.0;
    public const double PrenoiseMax = 0.1;
    public const double GrainMin = 0.0;
    public const double GrainMax = 1.0;
    public const double GsizeMin = 0.0;
    public const double GsizeMax = 5.0;

    public static double? Clamp(double? value, double min, double max)
        => value is null ? null : Math.Min(max, Math.Max(min, value.Value));

    public sealed record ExportResult(string Path, string FilterToken, string LastArguments);

    public static ExportResult Export(ExportRequest request)
    {
        var filterToken = BuildFilterToken(request);
        var lastArguments = BuildLastArguments(request);
        var root = LoadTemplate();

        root["预设备注"] =
            $"Topaz {request.Model.Id} · {ModeDisplay(request.Mode)}（调参器导出；{request.Encoder.DisplayName}；"
            + (request.ForceCfr ? "强制 CFR" : "保真帧率") + "）。";
        root["输出容器"] = ".mp4";
        root["视频参数_比特率_控制方式"] = request.Encoder.RateControl;
        root["视频参数_质量控制_参数名"] = request.Encoder.QualityName;
        root["视频参数_质量控制_值"] = request.Encoder.QualityValue;
        root["视频参数_质量控制_进阶参数集"] = request.Encoder.AdvancedArgumentsFor(request.ForceCfr);
        root["视频参数_编码器_分类名称"] = request.Encoder.Category;
        root["视频参数_编码器_具体编码"] = request.Encoder.Encoder;
        root["视频参数_编码器_编码预设"] = request.Encoder.Preset;
        root["视频参数_编码器_配置文件"] = request.Encoder.Profile;
        root["视频参数_编码器_场景优化"] = request.Encoder.Tune;
        root["视频参数_色彩管理_像素格式"] = "yuv420p";
        root["音频参数_编码器_代号"] = "audio.copy";
        root["自定义参数_最后参数"] = lastArguments;

        if (root["滤镜排序系统"] is JsonArray filters && filters.Count > 0
            && filters[0] is JsonObject filter)
        {
            filter["实例ID"] = $"topaz-tuner-{request.Model.Id}-{Guid.NewGuid():N}"[..48];
            filter["显示名称"] = request.PresetName;
            filter["自定义滤镜内容"] = filterToken;
        }
        else
        {
            throw new InvalidOperationException("内置预设模板缺少「滤镜排序系统」，无法导出。");
        }

        Directory.CreateDirectory(request.ExportDirectory);
        var path = ResolveAvailablePath(request.ExportDirectory, request.PresetName);
        File.WriteAllText(path, root.ToJsonString(WriteOptions), new System.Text.UTF8Encoding(false));

        // 写后自校验：重新解析一次，确认结构合法且关键字段落位。
        var check = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException("导出文件重新解析失败。");
        if ((string?)check["视频参数_色彩管理_像素格式"] != "yuv420p"
            || (string?)check["自定义参数_最后参数"] != lastArguments
            || (string?)check["滤镜排序系统"]?[0]?["自定义滤镜内容"] != filterToken)
        {
            throw new InvalidOperationException("导出文件校验失败：关键字段与预期不一致。");
        }

        return new ExportResult(path, filterToken, lastArguments);
    }

    /// <summary>默认导出目录：从 EXE 向上寻找 3FUI 宿主根（含 FFmpegFreeUI.exe）下的 Preset_v6\User。</summary>
    public static string DefaultPresetDirectory()
    {
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "FFmpegFreeUI.exe")))
                {
                    return Path.Combine(directory.FullName, "Preset_v6", "User");
                }

                directory = directory.Parent;
            }
        }
        catch
        {
            // 视为未找到。
        }

        return "";
    }

    private static string BuildFilterToken(ExportRequest request)
        => ComposeFilterToken(
            request.Model,
            request.Mode,
            request.Scale,
            request.TargetWidth,
            request.TargetHeight,
            request.ManualValues,
            Clamp(request.Prenoise, PrenoiseMin, PrenoiseMax),
            Clamp(request.Grain, GrainMin, GrainMax),
            Clamp(request.Gsize, GsizeMin, GsizeMax),
            request.Parameters);

    /// <summary>
    /// 合成 tvai_up 滤镜串。预设导出与「直接编码」共用本方法，
    /// 因此导出预设里的滤镜串与直接编码实际下发的滤镜串必然一致（不会出现两套写法）。
    /// </summary>
    public static string ComposeFilterToken(
        TvaiModel model,
        string mode,
        int? scale,
        int? targetWidth,
        int? targetHeight,
        IReadOnlyDictionary<string, double>? manualValues,
        double? prenoise,
        double? grain,
        double? gsize,
        string? parameters = null)
    {
        // 越界会被 tvai_up 硬拒（见常量处注释），这里统一钳制到实测值域。
        prenoise = Clamp(prenoise, PrenoiseMin, PrenoiseMax);
        grain = Clamp(grain, GrainMin, GrainMax);
        gsize = Clamp(gsize, GsizeMin, GsizeMax);

        // 注意：tvai_up 的 scale 不接受 "0:w=W:h=H" 这种 ffmpeg 通用写法——实测会被静默忽略，
        // 仍按模型原生倍率输出（例如请求 800x600 得到 1280x960）。要精确目标尺寸必须在
        // tvai_up 之后串一个标准 scale 滤镜（已实测 tvai_up=...,scale=w=W:h=H → 800x600）。
        var token = $"tvai_up=model={model.Id}:scale={(scale ?? 2).ToString(CultureInfo.InvariantCulture)}:device=0:vram=1:instances=1";

        if (prenoise is not null)
        {
            token += $":prenoise={Format(prenoise.Value)}";
        }

        if (grain is not null)
        {
            token += $":grain={Format(grain.Value)}";
        }

        if (gsize is not null)
        {
            token += $":gsize={Format(gsize.Value)}";
        }

        // parameters= 是 Topaz 的自由串通道（例如 grain_sigma / grain_type），必须整体作为一个
        // 选项值下发：内部的 ':' 要写成 '\:'，否则会被当成选项分隔符而解析失败。
        // 实测 parameters='grain_sigma=0.42\:grain_type=silver_rich' 经预设链路透传正常。
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            token += $":parameters='{parameters}'";
        }

        if (mode == "manual" && manualValues is not null)
        {
            // manual 的 6 个绝对值必须内联进滤镜串（编排器命令行没有参数值通道）。
            // 注意：Topaz 滤镜选项名全小写（preBlur → preblur），大小写不符会被 ffmpeg 判为
            // "Option not found" 而拒绝打开输出文件（已实测）。
            var filterValues = manualValues.ToDictionary(
                pair => FilterOptionName(pair.Key),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            if (!TvaiFilterComposer.TryCompose(
                    token,
                    model: null,
                    scale: null,
                    estimateFrames: null,
                    clearParameters: false,
                    parameterValues: filterValues,
                    out var composed,
                    out var composeError))
            {
                throw new InvalidOperationException($"滤镜串合成失败：{composeError}");
            }

            token = composed;
        }

        // 目标尺寸：串在 tvai_up 之后（超分到原生倍率再精确缩放到目标）。
        if (targetWidth is > 0 && targetHeight is > 0)
        {
            token += $",scale=w={targetWidth}:h={targetHeight}";
        }

        return token;
    }

    /// <summary>
    /// 自动/相对模式的 estimate 帧数写入滤镜串（一步法：Topaz 自带 ffmpeg 直接吃 <c>estimate=N</c>）。
    /// 由直接编码路径使用；预设导出把 estimate 放在顶层 <c>--tvai-*</c> 标记里交给编排器。
    /// </summary>
    public static string ApplyEstimate(string filterToken, int estimateFrames)
    {
        if (!TvaiFilterComposer.TryCompose(
                filterToken,
                model: null,
                scale: null,
                estimateFrames: estimateFrames,
                clearParameters: false,
                parameterValues: null,
                out var composed,
                out var composeError))
        {
            throw new InvalidOperationException($"estimate 写入滤镜串失败：{composeError}");
        }

        return composed;
    }

    /// <summary>
    /// 直接编码（不经济排器）时使用的滤镜串：语义与编排器的合成完全一致——
    /// auto 只写 <c>estimate=N</c>；relative 先清空参数再写 6 项偏移；manual 内联 6 个绝对值。
    /// 供独立版 TvaiStudio 复用，保证「导出预设的滤镜串」与「直接下发的滤镜串」同源。
    /// </summary>
    public static string ComposeDirectFilterToken(
        TvaiModel model,
        string mode,
        int estimateFrames,
        int? scale,
        int? targetWidth,
        int? targetHeight,
        IReadOnlyDictionary<string, double>? manualValues,
        IReadOnlyDictionary<string, double>? relativeOffsets,
        double? prenoise,
        double? grain,
        double? gsize,
        string? parameters = null)
    {
        var token = ComposeFilterToken(
            model,
            mode,
            scale,
            targetWidth,
            targetHeight,
            mode == "manual" ? manualValues : null,
            prenoise,
            grain,
            gsize,
            parameters);

        if (mode == "manual")
        {
            return token;
        }

        IReadOnlyDictionary<string, double>? values = null;
        if (mode == "relative" && relativeOffsets is not null)
        {
            values = relativeOffsets.ToDictionary(
                pair => FilterOptionName(pair.Key),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        if (!TvaiFilterComposer.TryCompose(
                token,
                model: null,
                scale: null,
                estimateFrames: estimateFrames,
                clearParameters: mode == "relative",
                parameterValues: values,
                out var composed,
                out var composeError))
        {
            throw new InvalidOperationException($"滤镜串合成失败：{composeError}");
        }

        return composed;
    }

    /// <summary>模型参数名 → tvai_up 滤镜选项名（滤镜选项全小写）。</summary>
    private static string FilterOptionName(string parameterName) => parameterName switch
    {
        "preBlur" => "preblur",
        _ => parameterName.ToLowerInvariant(),
    };

    private static string BuildLastArguments(ExportRequest request)
    {
        var arguments = $"--tvai-mode={request.Mode} --tvai-model={request.Model.Id}";
        switch (request.Mode)
        {
            case "auto":
                arguments += $" --tvai-estimate={request.EstimateFrames.ToString(CultureInfo.InvariantCulture)}";
                break;
            case "relative":
                arguments += $" --tvai-estimate={request.EstimateFrames.ToString(CultureInfo.InvariantCulture)}";
                if (request.RelativeOffsets is not null)
                {
                    var parts = TvaiFilterComposer.ParameterNames.Select(name =>
                        $"{NameForRelative(name)}={Format(request.RelativeOffsets.TryGetValue(name, out var value) ? value : 0)}");
                    arguments += " --tvai-rel=" + string.Join(',', parts);
                }

                break;
        }

        return arguments;
    }

    private static string NameForRelative(string parameterName) => parameterName switch
    {
        "preBlur" => "preblur",
        _ => parameterName.ToLowerInvariant(),
    };

    private static string ModeDisplay(string mode) => mode switch
    {
        "auto" => "自动（estimate）",
        "relative" => "半自动（相对自动）",
        _ => "手动",
    };

    private static string Format(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string ResolveAvailablePath(string directory, string presetName)
    {
        var safeName = SanitizeFileName(presetName);
        var candidate = Path.Combine(directory, safeName + ".json");
        for (var index = 2; File.Exists(candidate) && index < 100; index++)
        {
            candidate = Path.Combine(directory, $"{safeName}-{index}.json");
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? "Topaz-调参导出" : cleaned;
    }

    private static JsonObject LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("PresetTemplate.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("内置预设模板资源缺失。");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject
            ?? throw new InvalidOperationException("内置预设模板解析失败。");
    }
}
