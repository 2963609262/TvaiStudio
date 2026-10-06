namespace VideoEnhancer.TvaiTuner;

/// <summary>
/// 预设的编码器档位（与已部署的 Topaz 预设族一致）。硬性约束：av1 不得带 -tune uhq
/// （本机 + tvai 管线初始化失败）；像素格式由 PresetWriter 强制 yuv420p。
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

    public static readonly EncoderProfile H264Cqp18 = new(
        "h264", "H.264 NVENC · CQP 18", "nvenc", "h264_nvenc", "p7", "high", "hq",
        "CQP", "qp", "18",
        "-g 30 -rc-lookahead 20 -spatial_aq 1 -aq-strength 15 -b:v 0 -bf 0 " + FrameRatePassthrough);

    public static readonly EncoderProfile HevcVbr28 = new(
        "hevc", "HEVC NVENC · VBR CQ 28", "H.265/HEVC", "hevc_nvenc", "p7", "", "",
        "VBR", "cq", "28",
        "-g 30 -rc-lookahead 20 -spatial_aq 1 -aq-strength 15 -b:v 0 -bf 0 " + FrameRatePassthrough);

    public static readonly EncoderProfile Av1Vbr38 = new(
        "av1", "AV1 NVENC · VBR CQ 38", "AV1", "av1_nvenc", "p4", "", "",
        "VBR", "cq", "38",
        "-multipass fullres -g 30 -rc-lookahead 60 -spatial-aq 1 -temporal-aq 1 -aq-strength 15 -b:v 0 -bf 0 "
        + FrameRatePassthrough);

    public static readonly IReadOnlyList<EncoderProfile> All = [H264Cqp18, HevcVbr28, Av1Vbr38];

    /// <summary>
    /// <c>-rc</c> 的实际取值。3FUI 预设里的「CQP」是宿主界面标签，宿主会自行映射为
    /// <c>constqp</c>；本程序直接调 ffmpeg，必须自己做这层映射——否则
    /// <c>Unable to parse option value "cqp"</c> / <c>Error setting option rc</c>（已实测）。
    /// </summary>
    public string RateControlArgument => RateControl.ToUpperInvariant() switch
    {
        "CQP" => "constqp",
        _ => RateControl.ToLowerInvariant(),
    };

    public string AdvancedArgumentsFor(bool forceCfr)
        => forceCfr
            ? AdvancedArguments.Replace(FrameRatePassthrough, FrameRateCfr, StringComparison.Ordinal)
            : AdvancedArguments;

    public override string ToString() => DisplayName;
}
