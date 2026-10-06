using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoEnhancer.Router;
using VideoEnhancer.Tvai;
using VideoEnhancer.TvaiTuner;

namespace TvaiStudio;

/// <summary>一次编码请求（已解析并校验过的、可执行的规格）。</summary>
internal sealed record EncodeRequest(
    string Input,
    string Output,
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
    EncoderProfile Encoder,
    bool ForceCfr);

/// <summary>进度回调载荷（来自 Topaz ffmpeg 的 stderr 进度行）。</summary>
internal sealed record EncodeProgress(
    string Stage,
    double? Frame,
    double? Fps,
    string Time,
    double? Speed,
    double? BitrateKbps);

/// <summary>
/// 直接编码管线：把请求合成 tvai_up 滤镜串后调用 Topaz 自带 ffmpeg.exe。
///
/// 与 VideoEnhancer 编排器的关系：滤镜串与编码参数由同一份共享内核（PresetWriter/EncoderProfile）
/// 合成，因此「本程序直接编码的产物」与「导出预设经 3FUI 编码的产物」使用完全相同的写法；
/// 本程序不需要 VideoEnhancer 的任何二进制。
///
/// 安全不变量：目标 EXE 由用户设置给出并做 File.Exists 校验；全部参数经 ArgumentList 逐项传递、
/// UseShellExecute=false、CreateNoWindow=true —— 无 shell 参与、不做字符串拼接。
/// </summary>
internal sealed class EncodePipeline
{
    private static readonly Regex ProgressPattern = new(
        @"frame=\s*(?<frame>\d+).*?fps=\s*(?<fps>[\d.]+).*?time=(?<time>[\d:.]+).*?speed=\s*(?<speed>[\d.]+)x",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BitratePattern = new(
        @"bitrate=\s*(?<bitrate>[\d.]+)kbits/s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly StudioSettings _settings;

    public EncodePipeline(StudioSettings settings) => _settings = settings;

    /// <summary>按请求合成完整参数表（不含 EXE 路径）；供执行与「命令行预览」共用。</summary>
    public static List<string> BuildArguments(EncodeRequest request)
    {
        var filterToken = PresetWriter.ComposeDirectFilterToken(
            request.Model,
            request.Mode,
            request.EstimateFrames,
            request.Scale,
            request.TargetWidth,
            request.TargetHeight,
            request.ManualValues,
            request.RelativeOffsets,
            request.Prenoise,
            request.Grain,
            request.Gsize);

        var arguments = new List<string>
        {
            "-hide_banner", "-y", "-nostdin",
            "-i", request.Input,
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-filter:v", filterToken,
            "-c:v", request.Encoder.Encoder,
        };

        if (!string.IsNullOrEmpty(request.Encoder.Preset))
        {
            arguments.Add("-preset:v");
            arguments.Add(request.Encoder.Preset);
        }

        if (!string.IsNullOrEmpty(request.Encoder.Profile))
        {
            arguments.Add("-profile:v");
            arguments.Add(request.Encoder.Profile);
        }

        if (!string.IsNullOrEmpty(request.Encoder.Tune))
        {
            arguments.Add("-tune:v");
            arguments.Add(request.Encoder.Tune);
        }

        arguments.Add("-rc");
        arguments.Add(request.Encoder.RateControlArgument);
        arguments.Add($"-{request.Encoder.QualityName}");
        arguments.Add(request.Encoder.QualityValue);

        // 像素格式：tvai_up 恒输出高位深 4:4:4/RGB 系，硬件编码器必须显式转 4:2:0，
        // 否则 av1_nvenc 报 YUV444P not supported、hevc_nvenc 产出 gbrp10le 绿屏。
        arguments.Add("-pix_fmt");
        arguments.Add("yuv420p");

        foreach (var extra in request.Encoder.AdvancedArgumentsFor(request.ForceCfr).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            arguments.Add(extra);
        }

        arguments.Add("-c:a");
        arguments.Add("copy");
        arguments.Add(request.Output);
        return arguments;
    }

    /// <summary>把参数表拼成可读命令行（仅用于界面展示与日志，不用于执行）。</summary>
    public static string DescribeArguments(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(Quote));

    /// <summary>
    /// 执行一次编码。<paramref name="cancellation"/> 触发时按进程树终止（Topaz ffmpeg 会拉起子进程）。
    /// 返回 ffmpeg 原始退出码；被取消时返回 <see cref="CancelledExitCode"/>。
    /// </summary>
    public const int CancelledExitCode = -2;

    public int Run(
        EncodeRequest request,
        Action<EncodeProgress>? onProgress,
        Action<string>? onLog,
        CancellationToken cancellation,
        out string error)
    {
        error = "";
        if (!_settings.IsRunnable(out var problem))
        {
            error = problem;
            return -1;
        }

        var arguments = BuildArguments(request);
        var startInfo = new ProcessStartInfo(_settings.TopazFfmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in _settings.BuildEnvironmentVariables())
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var job = JobObject.CreateKillOnClose();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                error = $"无法启动 Topaz ffmpeg：{_settings.TopazFfmpeg}";
                return -1;
            }

            JobObject.Assign(job, process);

            // ffmpeg 把进度写 stderr；stdout 通常为空但要读干，避免管道写满造成死锁。
            var drain = Task.Run(() => process.StandardOutput.BaseStream.CopyTo(Stream.Null));

            double? duration = null;
            var pending = new StringBuilder();
            var buffer = new char[4096];
            var reader = process.StandardError;
            while (true)
            {
                if (cancellation.IsCancellationRequested)
                {
                    KillTree(process);
                    drain.Wait(2000);
                    return CancelledExitCode;
                }

                var read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                pending.Append(buffer, 0, read);
                DrainLines(pending, line =>
                {
                    onLog?.Invoke(line);
                    if (duration is null)
                    {
                        var match = DurationPattern.Match(line);
                        if (match.Success)
                        {
                            duration = ParseDuration(match);
                        }
                    }

                    var progress = ParseProgress(line, duration);
                    if (progress is not null)
                    {
                        onProgress?.Invoke(progress);
                    }
                });
            }

            DrainLines(pending, line => onLog?.Invoke(line), flush: true);
            process.WaitForExit();
            drain.Wait(2000);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            error = $"编码失败：{ex.Message}";
            return -1;
        }
        finally
        {
            process?.Dispose();
            JobObject.Close(job);
        }
    }

    private static void KillTree(Process process)
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
            // 进程可能恰好退出；忽略。
        }
    }

    /// <summary>
    /// ffmpeg 进度用 \r 刷新且不换行，因此按 \r 与 \n 一起切分；
    /// 未以分隔符结尾的残留留在缓冲里等下一批数据。
    /// </summary>
    private static void DrainLines(StringBuilder pending, Action<string> onLine, bool flush = false)
    {
        var text = pending.ToString();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            var line = text[start..index].Trim();
            if (line.Length > 0)
            {
                onLine(line);
            }

            start = index + 1;
        }

        pending.Clear();
        var tail = text[start..];
        if (flush)
        {
            if (tail.Trim().Length > 0)
            {
                onLine(tail.Trim());
            }
        }
        else
        {
            pending.Append(tail);
        }
    }

    private static EncodeProgress? ParseProgress(string line, double? duration)
    {
        var match = ProgressPattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        double? frame = double.TryParse(match.Groups["frame"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : null;
        double? fps = double.TryParse(match.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
        double? speed = double.TryParse(match.Groups["speed"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;

        double? bitrate = null;
        var bitrateMatch = BitratePattern.Match(line);
        if (bitrateMatch.Success
            && double.TryParse(bitrateMatch.Groups["bitrate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            bitrate = b;
        }

        var timeText = match.Groups["time"].Value;
        var stage = "编码中";
        if (duration is > 0 && TryParseTime(timeText, out var elapsed))
        {
            var percent = Math.Clamp(elapsed / duration.Value * 100, 0, 100);
            stage = $"编码中 {percent:0.0}%";
        }

        return new EncodeProgress(stage, frame, fps, timeText, speed, bitrate);
    }

    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    private static double ParseDuration(Match match)
        => double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600
           + double.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
           + double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);

    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
