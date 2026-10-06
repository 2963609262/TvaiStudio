namespace VideoEnhancer.TvaiTuner;

/// <summary>
/// 预设的编码器「类型档」（H.264/HEVC/AV1 三族 NVENC；G0 实证 Topaz ffmpeg 8.0 构建另有
/// ffv1/prores_ks/libaom-av1，但接入需容器与像素格式扩展，暂不进 UI——见 ARCH.md 实证节）。
/// 硬性约束：av1 不得带 -tune uhq（本机 + tvai 管线初始化失败）；像素格式由 PresetWriter 强制 yuv420p。
/// 控制方式与质量值不再烤死在档位里：默认值仅是初始显示值，导出请求可经
/// <see cref="WithRateControl"/> 覆盖（用户在面板/调参器自选 CQP/VBR 与数值）。
/// </summary>
internal sealed record EncoderProfile(
    string Key,
    string DisplayName,
    string Category,
    string Encoder,
    string Preset,
    string Profile,
    string Tune,
    string RateControl,
    string QualityName,
    string QualityValue,
    string AdvancedArguments)
{
    public const string FrameRatePassthrough = "-fps_mode:v passthrough";
    public const string FrameRateCfr = "-fps_mode:v cfr";

    /// <summary>UI 可选的质量控制方式（NVENC 语义）：CQP=恒定 qp，VBR=目标 cq。</summary>
    public static readonly IReadOnlyList<string> RateControlChoices = ["CQP", "VBR"];

    /// <summary>质量值的合理范围；超范围在导出前拒绝，避免把明显笔误交给 ffmpeg。</summary>
    public const int QualityValueMin = 1;
    public const int QualityValueMax = 51;

    public static readonly EncoderProfile H264Cqp18 = new(
        "h264", "H.264 NVENC", "nvenc", "h264_nvenc", "p7", "high", "hq",
        "CQP", "qp", "18",
        "-g 30 -rc-lookahead 20 -spatial_aq 1 -aq-strength 15 -b:v 0 -bf 0 " + FrameRatePassthrough);

    public static readonly EncoderProfile HevcVbr28 = new(
        "hevc", "HEVC NVENC", "H.265/HEVC", "hevc_nvenc", "p7", "", "",
        "VBR", "cq", "28",
        "-g 30 -rc-lookahead 20 -spatial_aq 1 -aq-strength 15 -b:v 0 -bf 0 " + FrameRatePassthrough);

    public static readonly EncoderProfile Av1Vbr38 = new(
        "av1", "AV1 NVENC", "AV1", "av1_nvenc", "p4", "", "",
        "VBR", "cq", "38",
        "-multipass fullres -g 30 -rc-lookahead 60 -spatial-aq 1 -temporal-aq 1 -aq-strength 15 -b:v 0 -bf 0 "
        + FrameRatePassthrough);

    public static readonly IReadOnlyList<EncoderProfile> All = [H264Cqp18, HevcVbr28, Av1Vbr38];

    /// <summary>
    /// 以用户自选的控制方式与质量值派生一个档位副本（显示名带后缀，预设备注据此可读）。
    /// 控制方式须先经调用方白名单校验（见 UiContract）；质量参数名按 NVENC 语义映射：
    /// CQP 用 <c>qp</c>，VBR 用 <c>cq</c>。
    /// </summary>
    public EncoderProfile WithRateControl(string rateControl, string qualityValue)
    {
        var rc = rateControl.ToUpperInvariant();
        var qualityName = rc switch
        {
            "CQP" => "qp",
            "VBR" => "cq",
            _ => QualityName,
        };
        return this with
        {
            RateControl = rc,
            QualityName = qualityName,
            QualityValue = qualityValue,
            DisplayName = $"{DisplayName} · {rc} {qualityValue}",
        };
    }

    /// <summary>
    /// <c>-rc</c> 的实际取值。3FUI 预设里的「CQP」是宿主界面标签，宿主会自行映射为
    /// <c>constqp</c>；本程序直接调 ffmpeg，必须自己做这层映射——否则
    /// <c>Unable to parse option value "cqp"</c> / <c>Error setting option rc</c>（已实测）。
    /// 未知值原样小写透传，交由 ffmpeg 报错（不静默写死）。
    /// </summary>
    public string RateControlArgument => RateControl.ToUpperInvariant() switch
    {
        "CQP" => "constqp",
        "CRF" => "crf",
        "CBR" => "cbr",
        _ => RateControl.ToLowerInvariant(),
    };

    public string AdvancedArgumentsFor(bool forceCfr)
        => forceCfr
            ? AdvancedArguments.Replace(FrameRatePassthrough, FrameRateCfr, StringComparison.Ordinal)
            : AdvancedArguments;

    public override string ToString() => DisplayName;
}
