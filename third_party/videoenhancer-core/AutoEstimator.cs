using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEnhancer.Tvai;

/// <summary>一次预览估计的结果（与 <see cref="TvaiFilterComposer.ParameterNames"/> 同序的 6 个绝对值）。</summary>
internal sealed class TvaiEstimation
{
    public required string Estimator { get; init; }

    public required int SampleCount { get; init; }

    public required double[] Values { get; init; }
}

/// <summary>
/// 两步法预览估计（与 Topaz GUI 的 Estimate 按钮同款）：
/// 取中段片段跑 <c>tvai_pe=model=&lt;estimator&gt;</c>，逐帧解析 <c>Parameter values:[...]</c>，按参数取中位数。
/// 输出只回传给调用方，不进入宿主 stdout/stderr。
/// </summary>
internal static class AutoEstimator
{
    private static readonly Regex ParameterValuesPattern =
        new(@"Parameter values:\[([^\]]*)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationPattern =
        new(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryEstimate(
        string ffmpegPath,
        string input,
        string estimator,
        double sampleSeconds,
        IReadOnlyDictionary<string, string> environmentVariables,
        out TvaiEstimation estimation,
        out string error)
    {
        estimation = null!;
        error = "";

        var duration = TryProbeDuration(ffmpegPath, input, environmentVariables);
        var start = duration > sampleSeconds + 2 ? Math.Max(0, (duration - sampleSeconds) / 2) : 0.5;

        var arguments = new List<string>
        {
            "-hide_banner", "-y", "-nostdin",
            "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", sampleSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", input,
            "-vf", "tvai_pe=model=" + estimator,
            "-f", "null", "-",
        };

        var stderr = new StringBuilder();
        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath)
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

            foreach (var pair in environmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = $"无法启动 Topaz ffmpeg：{ffmpegPath}";
                return false;
            }

            // 估计输出只用于解析：stdout 丢弃、stderr 收集，避免与宿主进度流混淆。
            var drain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var reader = process.StandardError;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                stderr.AppendLine(line);
            }

            process.WaitForExit();
            drain.Wait();
        }
        catch (Exception ex)
        {
            error = $"估计进程失败：{ex.Message}";
            return false;
        }

        var samples = CollectSamples(stderr.ToString());
        if (samples.Count == 0)
        {
            error = $"估计器 {estimator} 未输出参数值（权重缺失或模型不兼容时 Topaz 会直接崩溃）";
            return false;
        }

        var values = new double[TvaiFilterComposer.ParameterNames.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var list = samples.TryGetValue(index, out var bucket) ? bucket : new List<double>();
            values[index] = list.Count == 0 ? 0 : Median(list);
        }

        estimation = new TvaiEstimation
        {
            Estimator = estimator,
            SampleCount = samples.Values.Max(v => v.Count),
            Values = values,
        };
        return true;
    }

    private static Dictionary<int, List<double>> CollectSamples(string stderr)
    {
        var samples = new Dictionary<int, List<double>>();
        foreach (Match match in ParameterValuesPattern.Matches(stderr))
        {
            var parts = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
            for (var index = 0; index < parts.Length && index < TvaiFilterComposer.ParameterNames.Length; index++)
            {
                if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                if (!samples.TryGetValue(index, out var bucket))
                {
                    bucket = new List<double>();
                    samples[index] = bucket;
                }

                bucket.Add(value);
            }
        }

        return samples;
    }

    /// <summary>与 GUI 内嵌 JS 一致：排序后取下中位数（floor(n/2)）。</summary>
    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static double TryProbeDuration(string ffmpegPath, string input, IReadOnlyDictionary<string, string> environmentVariables)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(input);
            foreach (var pair in environmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return 0;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.BaseStream.CopyTo(Stream.Null);
            process.WaitForExit();

            var match = DurationPattern.Match(stderr);
            if (!match.Success)
            {
                return 0;
            }

            var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return hours * 3600 + minutes * 60 + seconds;
        }
        catch
        {
            return 0;
        }
    }
}
