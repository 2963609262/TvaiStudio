using System.Globalization;
using System.Text;
using VideoEnhancer.Tvai;
using VideoEnhancer.TvaiTuner;

namespace TvaiStudio;

/// <summary>
/// TvaiStudio 入口。无参数 = GUI；带参数 = 无窗口模式（便于脚本与验收，不依赖 GUI 自动化）：
///
///   tvaistudio --self-test                     探测 Topaz/模型目录并打印结论（退出码 0/3）
///   tvaistudio --models                        列出模型与本地权重（JSON）
///   tvaistudio --check --model=rhea-1          校验模型权重（缺权重退出码 3）
///   tvaistudio --estimate-only --model=rhea-1 --input=in.mp4
///   tvaistudio --batch --input=a.mp4 --input=b.mp4 --output-dir=out --model=rhea-1
///   tvaistudio --encode --input=in.mp4 --output=out.mp4 --model=rhea-1 --mode=auto --encoder=hevc
///   tvaistudio --export-preset=out.json --model=rhea-1 --mode=auto --encoder=hevc
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitMissingDependency = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _rawArguments = args;

        try
        {
            // 形如 `tvaistudio a.mp4 b.mp4`：打开界面并把文件预置进队列（拖放的等价入口）。
            var files = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal) && File.Exists(arg)).ToList();
            var hasVerbs = args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal));

            if (args.Length > 0 && hasVerbs)
            {
                return RunHeadless(args);
            }

            ApplicationConfiguration.Initialize();
            var form = new StudioForm();
            if (files.Count > 0)
            {
                form.QueueFilesOnLoad(files);
            }

            Application.Run(form);
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tvaistudio] {ex.Message}");
            return ExitUsage;
        }
    }

    /// <summary>原始参数（保留重复项：<c>--input</c> 可出现多次，字典会互相覆盖）。</summary>
    private static string[] _rawArguments = [];

    /// <summary>按出现顺序取出某个选项的全部取值。</summary>
    private static List<string> AllValues(string name)
    {
        var values = new List<string>();
        foreach (var arg in _rawArguments)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                values.Add(arg[(name.Length + 1)..]);
            }
        }

        return values;
    }

    private static int RunHeadless(string[] args)
    {
        var options = ParseOptions(args, out var error);
        if (error.Length > 0)
        {
            Console.Error.WriteLine($"[tvaistudio] {error}");
            return ExitUsage;
        }

        var settings = StudioSettings.Load();

        if (options.ContainsKey("--help"))
        {
            PrintHelp();
            return ExitOk;
        }

        if (options.ContainsKey("--self-test"))
        {
            return SelfTest(settings);
        }

        if (!settings.IsRunnable(out var problem))
        {
            Console.Error.WriteLine($"[tvaistudio] {problem}");
            return ExitMissingDependency;
        }

        var catalog = TopazModelCatalog.Load(settings.ModelDir, settings.ModelDataDir);

        if (options.ContainsKey("--models"))
        {
            return ListModels(catalog, settings);
        }

        var modelId = options.GetValueOrDefault("--model", settings.Model).Trim();
        var model = catalog.Find(modelId);
        if (model is null)
        {
            Console.Error.WriteLine($"[tvaistudio] 目录中没有模型 {modelId}（定义目录：{settings.ModelDir}）。");
            return ExitMissingDependency;
        }

        var mode = options.GetValueOrDefault("--mode", settings.Mode).Trim().ToLowerInvariant();
        if (mode is not ("manual" or "auto" or "relative"))
        {
            Console.Error.WriteLine($"[tvaistudio] --mode 应为 manual|auto|relative，当前：{mode}");
            return ExitUsage;
        }

        if (options.ContainsKey("--check"))
        {
            return CheckModel(catalog, model, mode, settings);
        }

        if (options.ContainsKey("--estimate-only"))
        {
            return Estimate(settings, model, options);
        }

        if (options.ContainsKey("--encode"))
        {
            return Encode(settings, model, mode, options);
        }

        if (options.ContainsKey("--batch"))
        {
            return Batch(settings, model, mode, options);
        }

        if (options.TryGetValue("--export-preset", out var presetPath))
        {
            return ExportPreset(settings, model, mode, presetPath, options);
        }

        Console.Error.WriteLine("[tvaistudio] 未识别的参数组合，使用 --help 查看用法。");
        return ExitUsage;
    }

    // ── 无窗口动词 ──

    private static int SelfTest(StudioSettings settings)
    {
        Console.WriteLine("TvaiStudio 自检");
        Console.WriteLine($"  Topaz ffmpeg   : {(File.Exists(settings.TopazFfmpeg) ? settings.TopazFfmpeg : "[未找到] " + settings.TopazFfmpeg)}");
        Console.WriteLine($"  模型定义目录   : {(Directory.Exists(settings.ModelDir) ? settings.ModelDir : "[不存在] " + settings.ModelDir)}");
        Console.WriteLine($"  权重目录       : {(Directory.Exists(settings.ModelDataDir) ? settings.ModelDataDir : "[不存在] " + settings.ModelDataDir)}");
        Console.WriteLine($"  输出目录       : {settings.OutputDirectory}");
        Console.WriteLine($"  配置文件       : {StudioSettings.ConfigPath}");

        if (!settings.IsRunnable(out var problem))
        {
            Console.Error.WriteLine($"  结论：不可运行 —— {problem}");
            return ExitMissingDependency;
        }

        var catalog = TopazModelCatalog.Load(settings.ModelDir, settings.ModelDataDir);
        var withWeights = catalog.Ids.Count(id =>
        {
            var model = catalog.Find(id);
            return model is not null && catalog.HasWeights(model);
        });
        Console.WriteLine($"  模型目录       : {catalog.Count} 个模型定义，其中 {withWeights} 个本地有权重");
        Console.WriteLine("  结论：可运行。");
        return ExitOk;
    }

    private static int ListModels(TopazModelCatalog catalog, StudioSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"count\": ").Append(catalog.Count).Append(",\n  \"models\": [\n");
        var ids = catalog.Ids.ToList();
        for (var index = 0; index < ids.Count; index++)
        {
            var model = catalog.Find(ids[index])!;
            var weights = catalog.CountWeightFiles(model.WeightFilePrefix);
            var estimator = model.AutoModel.Length > 0 ? catalog.Find(model.AutoModel) : null;
            var estimatorWeights = estimator is null ? 0 : catalog.CountWeightFiles(estimator.WeightFilePrefix);
            builder.Append("    {")
                .Append($"\"id\": \"{model.Id}\", ")
                .Append($"\"displayName\": \"{model.DisplayName}\", ")
                .Append($"\"autoModel\": \"{model.AutoModel}\", ")
                .Append($"\"weightCount\": {weights}, ")
                .Append($"\"estimatorWeightCount\": {estimatorWeights}, ")
                .Append($"\"parameterCount\": {model.Parameters.Count}")
                .Append('}');
            builder.Append(index == ids.Count - 1 ? "\n" : ",\n");
        }

        builder.Append("  ]\n}");
        Console.WriteLine(builder.ToString());
        return ExitOk;
    }

    private static int CheckModel(TopazModelCatalog catalog, TvaiModel model, string mode, StudioSettings settings)
    {
        var weights = catalog.CountWeightFiles(model.WeightFilePrefix);
        var estimator = model.AutoModel.Length > 0 ? catalog.Find(model.AutoModel) : null;
        var estimatorWeights = estimator is null ? 0 : catalog.CountWeightFiles(estimator.WeightFilePrefix);

        Console.WriteLine($"[tvaistudio] 模型 {model.Id}（{model.DisplayName}）");
        Console.WriteLine($"  权重：{model.WeightFilePrefix}* → {weights} 个文件 [{settings.ModelDataDir}]");
        Console.WriteLine(model.AutoModel.Length == 0
            ? "  估计器：无（该模型不支持自动/相对模式）"
            : $"  估计器：{model.AutoModel} → {(estimator is null ? "不在目录中" : $"{estimatorWeights} 个文件")}");

        if (weights == 0)
        {
            Console.Error.WriteLine("  结论：缺少模型权重，Topaz 会直接崩溃（exit 139），已提前拦截。");
            Console.Error.WriteLine("  获取方式：在 Topaz Video AI 中对该模型做一次导出/Estimate 让其自动下载，");
            Console.Error.WriteLine("            或从官方 CDN 手动放置并保留 .tz3 扩展名。");
            return ExitMissingDependency;
        }

        if (mode is "auto" or "relative" && estimatorWeights == 0)
        {
            var estimatorName = estimator is null ? model.AutoModel : estimator.Id;
            Console.Error.WriteLine(
                estimatorName.Length > 0
                    ? $"  结论：{mode} 模式需要估计器 {estimatorName} 的权重，当前缺失。"
                    : $"  结论：{mode} 模式需要估计器权重，但该模型定义未声明 autoModel。");
            return ExitMissingDependency;
        }

        Console.WriteLine("  结论：可用于该模式。");
        return ExitOk;
    }

    private static int Estimate(StudioSettings settings, TvaiModel model, Dictionary<string, string> options)
    {
        var input = Require(options, "--input");
        if (input.Length == 0 || !File.Exists(input))
        {
            Console.Error.WriteLine($"[tvaistudio] 输入文件不存在：{input}");
            return ExitUsage;
        }

        if (model.AutoModel.Length == 0)
        {
            Console.Error.WriteLine($"[tvaistudio] 模型 {model.Id} 不支持估计（模型定义未声明 autoModel）。");
            return ExitMissingDependency;
        }

        var sampleSeconds = ParseDouble(options.GetValueOrDefault("--sample-seconds", ""), settings.SampleSeconds);
        if (!AutoEstimator.TryEstimate(
                settings.TopazFfmpeg,
                input,
                model.AutoModel,
                sampleSeconds,
                settings.BuildEnvironmentVariables(),
                out var estimation,
                out var failure))
        {
            Console.Error.WriteLine($"[tvaistudio] 估计失败：{failure}");
            return ExitMissingDependency;
        }

        var builder = new StringBuilder();
        builder.Append("{\n  \"estimator\": \"").Append(estimation.Estimator).Append("\",\n");
        builder.Append($"  \"sampleCount\": {estimation.SampleCount},\n  \"values\": {{\n");
        for (var index = 0; index < estimation.Values.Length; index++)
        {
            var suffix = index == estimation.Values.Length - 1 ? "\n" : ",\n";
            builder.Append($"    \"{TvaiFilterComposer.ParameterNames[index]}\": {estimation.Values[index].ToString("0.######", CultureInfo.InvariantCulture)}{suffix}");
        }

        builder.Append("  }\n}");
        Console.WriteLine(builder.ToString());
        return ExitOk;
    }

    private static int Encode(StudioSettings settings, TvaiModel model, string mode, Dictionary<string, string> options)
    {
        var input = Require(options, "--input");
        if (input.Length == 0 || !File.Exists(input))
        {
            Console.Error.WriteLine($"[tvaistudio] 输入文件不存在：{input}");
            return ExitUsage;
        }

        var output = Require(options, "--output");
        if (output.Length == 0)
        {
            Console.Error.WriteLine("[tvaistudio] 需要 --output=<输出文件>");
            return ExitUsage;
        }

        var encoderKey = options.GetValueOrDefault("--encoder", settings.EncoderKey).Trim();
        var encoder = EncoderProfile.All.FirstOrDefault(p => p.Key.Equals(encoderKey, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            Console.Error.WriteLine($"[tvaistudio] --encoder 无效：{encoderKey}（可用：{string.Join('/', EncoderProfile.All.Select(p => p.Key))}）");
            return ExitUsage;
        }

        if (!TryApplyRateControlOverride(encoder, options, out encoder, out var rateControlError))
        {
            Console.Error.WriteLine($"[tvaistudio] {rateControlError}");
            return ExitUsage;
        }

        if (mode is "auto" or "relative" && model.AutoModel.Length == 0)
        {
            Console.Error.WriteLine($"[tvaistudio] 模型 {model.Id} 不支持 {mode} 模式（未声明 autoModel）。");
            return ExitMissingDependency;
        }

        var request = new EncodeRequest(
            Path.GetFullPath(input),
            Path.GetFullPath(output),
            model,
            mode,
            ParseInt(options.GetValueOrDefault("--estimate", ""), settings.EstimateFrames),
            ParseInt(options.GetValueOrDefault("--scale", ""), settings.Scale),
            ParseNullableInt(options.GetValueOrDefault("--width", "")),
            ParseNullableInt(options.GetValueOrDefault("--height", "")),
            null,
            null,
            encoder,
            options.ContainsKey("--force-cfr"));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

        Console.WriteLine($"[tvaistudio] {EncodePipeline.DescribeArguments(EncodePipeline.BuildArguments(request))}");

        var pipeline = new EncodePipeline(settings);
        var exitCode = pipeline.Run(
            request,
            progress => Console.WriteLine($"[tvaistudio] {progress.Stage}  frame={progress.Frame}  speed={progress.Speed}x"),
            null,
            CancellationToken.None,
            out var encodeError);

        if (encodeError.Length > 0)
        {
            Console.Error.WriteLine($"[tvaistudio] {encodeError}");
            return ExitUsage;
        }

        Console.WriteLine($"[tvaistudio] 编码结束，退出码 {exitCode}");
        return exitCode;
    }

    /// <summary>
    /// 批量编码：按 --input 多次给出（或 --input-dir 扫描目录）逐个编码到 --output-dir。
    /// 与 GUI 队列语义一致（顺序执行、每项独立退出码、取消时整树终止），
    /// 因此可在不驱动界面的情况下验收队列行为。
    /// </summary>
    private static int Batch(StudioSettings settings, TvaiModel model, string mode, Dictionary<string, string> options)
    {
        var inputs = new List<string>();
        foreach (var value in AllValues("--input"))
        {
            if (File.Exists(value))
            {
                inputs.Add(Path.GetFullPath(value));
            }
        }

        var inputDir = options.GetValueOrDefault("--input-dir", "").Trim();
        if (inputDir.Length > 0 && Directory.Exists(inputDir))
        {
            inputs.AddRange(Directory
                .EnumerateFiles(inputDir)
                .Where(path => new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m4v", ".flv" }
                    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFullPath));
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("[tvaistudio] --batch 需要至少一个 --input=<文件> 或 --input-dir=<目录>");
            return ExitUsage;
        }

        var outputDir = options.GetValueOrDefault("--output-dir", settings.OutputDirectory).Trim();
        if (outputDir.Length == 0)
        {
            outputDir = settings.OutputDirectory;
        }

        Directory.CreateDirectory(outputDir);

        var encoderKey = options.GetValueOrDefault("--encoder", settings.EncoderKey).Trim();
        var encoder = EncoderProfile.All.FirstOrDefault(p => p.Key.Equals(encoderKey, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            Console.Error.WriteLine($"[tvaistudio] --encoder 无效：{encoderKey}");
            return ExitUsage;
        }

        if (!TryApplyRateControlOverride(encoder, options, out encoder, out var batchRateControlError))
        {
            Console.Error.WriteLine($"[tvaistudio] {batchRateControlError}");
            return ExitUsage;
        }

        if (mode is "auto" or "relative" && model.AutoModel.Length == 0)
        {
            Console.Error.WriteLine($"[tvaistudio] 模型 {model.Id} 不支持 {mode} 模式。");
            return ExitMissingDependency;
        }

        var estimateFrames = ParseInt(options.GetValueOrDefault("--estimate", ""), settings.EstimateFrames);
        var scale = ParseInt(options.GetValueOrDefault("--scale", ""), settings.Scale);
        var width = ParseNullableInt(options.GetValueOrDefault("--width", ""));
        var height = ParseNullableInt(options.GetValueOrDefault("--height", ""));
        var forceCfr = options.ContainsKey("--force-cfr");

        // 取消支持：Ctrl+C / SIGTERM 时终止当前 ffmpeg 进程树（与 GUI 取消同一路径）。
        using var cancellation = new CancellationTokenSource();
        void RequestCancel(object? sender, EventArgs e)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 正常结束后的 ProcessExit 也会走到这里：此时已释放，忽略。
            }
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RequestCancel(null, EventArgs.Empty);
        };
        AppDomain.CurrentDomain.ProcessExit += RequestCancel;

        var pipeline = new EncodePipeline(settings);
        var succeeded = 0;
        var failed = 0;
        var cancelled = 0;

        for (var index = 0; index < inputs.Count; index++)
        {
            if (cancellation.IsCancellationRequested)
            {
                cancelled += inputs.Count - index;
                break;
            }

            var input = inputs[index];
            var output = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + "_tvai.mp4");
            Console.WriteLine($"[tvaistudio] ({index + 1}/{inputs.Count}) {Path.GetFileName(input)} → {Path.GetFileName(output)}");

            var request = new EncodeRequest(
                input, output, model, mode, estimateFrames, scale, width, height,
                null, null, encoder, forceCfr);

            var exitCode = pipeline.Run(
                request,
                progress => Console.WriteLine($"[tvaistudio]   {progress.Stage} frame={progress.Frame}"),
                null,
                cancellation.Token,
                out var encodeError);

            if (encodeError.Length > 0)
            {
                Console.Error.WriteLine($"[tvaistudio]   {encodeError}");
            }

            if (exitCode == EncodePipeline.CancelledExitCode)
            {
                cancelled += inputs.Count - index;
                EncodePipeline.DeletePartialOutput(output);
                Console.WriteLine("[tvaistudio]   已取消（进程树已终止）");
                break;
            }

            if (exitCode == 0)
            {
                succeeded++;
            }
            else
            {
                failed++;
                Console.Error.WriteLine($"[tvaistudio]   失败，退出码 {exitCode}");
            }
        }

        Console.WriteLine($"[tvaistudio] 批量结束：成功 {succeeded}，失败 {failed}，取消 {cancelled}");
        return failed > 0 ? 1 : ExitOk;
    }

    private static int ExportPreset(
        StudioSettings settings,
        TvaiModel model,
        string mode,
        string presetPath,
        Dictionary<string, string> options)
    {
        var encoderKey = options.GetValueOrDefault("--encoder", settings.EncoderKey).Trim();
        var encoder = EncoderProfile.All.FirstOrDefault(p => p.Key.Equals(encoderKey, StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            Console.Error.WriteLine($"[tvaistudio] --encoder 无效：{encoderKey}");
            return ExitUsage;
        }

        if (!TryApplyRateControlOverride(encoder, options, out encoder, out var exportRateControlError))
        {
            Console.Error.WriteLine($"[tvaistudio] {exportRateControlError}");
            return ExitUsage;
        }

        var exportDirectory = Path.GetDirectoryName(Path.GetFullPath(presetPath));
        if (string.IsNullOrEmpty(exportDirectory))
        {
            exportDirectory = settings.OutputDirectory;
        }

        var request = new PresetWriter.ExportRequest(
            model,
            mode,
            ParseInt(options.GetValueOrDefault("--estimate", ""), settings.EstimateFrames),
            ParseInt(options.GetValueOrDefault("--scale", ""), settings.Scale),
            ParseNullableInt(options.GetValueOrDefault("--width", "")),
            ParseNullableInt(options.GetValueOrDefault("--height", "")),
            null,
            null,
            null,
            null,
            null,
            null,
            encoder,
            options.ContainsKey("--force-cfr"),
            Path.GetFileNameWithoutExtension(presetPath),
            exportDirectory);

        try
        {
            var result = PresetWriter.Export(request);
            Console.WriteLine($"[tvaistudio] 已导出预设：{result.Path}");
            Console.WriteLine($"[tvaistudio] 滤镜串：{result.FilterToken}");
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tvaistudio] 预设导出失败：{ex.Message}");
            return ExitUsage;
        }
    }

    // ── 参数解析 ──

    private static Dictionary<string, string> ParseOptions(string[] args, out string error)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = "";
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=');
            var name = separator < 0 ? arg : arg[..separator];
            var value = separator < 0 ? "" : arg[(separator + 1)..];
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                options[name] = value;
            }
            else
            {
                error = $"无法识别的参数：{arg}";
                return options;
            }
        }

        return options;
    }

    private static string Require(Dictionary<string, string> options, string name)
    {
        var value = options.GetValueOrDefault(name, "").Trim();
        if (value.Length == 0)
        {
            Console.Error.WriteLine($"[tvaistudio] 缺少 {name}");
        }

        return value;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static int? ParseNullableInt(string text)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static double ParseDouble(string text, double fallback)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private static void PrintHelp()
    {
        Console.WriteLine("TvaiStudio — Topaz Video AI tvai_up 超分转码工具（独立版）");
        Console.WriteLine();
        Console.WriteLine("无参数启动图形界面；以下为无窗口模式（脚本/验收用）：");
        Console.WriteLine("  --self-test                              探测 Topaz 与模型目录并打印结论");
        Console.WriteLine("  --models                                 列出模型与本地权重数量（JSON）");
        Console.WriteLine("  --check --model=<id> [--mode=<m>]        校验模型/估计器权重（缺权重退出码 3）");
        Console.WriteLine("  --estimate-only --model=<id> --input=<file>   预览估计（输出 JSON）");
        Console.WriteLine("  --encode --input=<file> --output=<file> --model=<id> [--mode=manual|auto|relative]");
        Console.WriteLine("           [--encoder=h264|hevc|av1] [--estimate=N] [--scale=N] [--width=N --height=N] [--force-cfr]");
        Console.WriteLine("  --batch --input=<f1> [--input=<f2> …] [--input-dir=<dir>] --output-dir=<dir> --model=<id>");
        Console.WriteLine("           批量编码（与界面队列同一语义；Ctrl+C 取消并终止进程树）");
        Console.WriteLine("  --export-preset=<file> --model=<id> [--mode=<m>] [--encoder=<k>]   导出预设文件（JSON）");
        Console.WriteLine("  [--rate-control=CQP|VBR] [--quality-value=N]             质量控制方式与值（与 --encoder 成套；缺省用保存的设置）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功；2 用法/配置错误；3 缺少依赖（Topaz ffmpeg 或权重）；其余为 ffmpeg 原始退出码。");
    }

    /// <summary>
    /// 解析可选的 <c>--rate-control</c> / <c>--quality-value</c> 覆盖并应用到编码器档位。
    /// 两个参数都缺省时保持档位默认值（与既有无窗口行为一致）；只给其一时报用法错误。
    /// </summary>
    private static bool TryApplyRateControlOverride(
        EncoderProfile baseEncoder,
        Dictionary<string, string> options,
        out EncoderProfile encoder,
        out string error)
    {
        var hasRateControl = options.TryGetValue("--rate-control", out var rateControlRaw);
        var hasQualityValue = options.TryGetValue("--quality-value", out var qualityValueRaw);
        if (!hasRateControl && !hasQualityValue)
        {
            encoder = baseEncoder;
            error = "";
            return true;
        }

        if (hasRateControl != hasQualityValue)
        {
            encoder = baseEncoder;
            error = "--rate-control 与 --quality-value 必须成对提供。";
            return false;
        }

        var rateControl = rateControlRaw?.Trim().ToUpperInvariant() ?? "";
        var qualityValue = qualityValueRaw?.Trim() ?? "";
        if (rateControl is not ("CQP" or "VBR"))
        {
            encoder = baseEncoder;
            error = $"--rate-control 无效：{rateControl}（可用：CQP/VBR）";
            return false;
        }

        if (!int.TryParse(qualityValue, out var value)
            || value < EncoderProfile.QualityValueMin
            || value > EncoderProfile.QualityValueMax)
        {
            encoder = baseEncoder;
            error = $"--quality-value 无效：{qualityValue}（应为 {EncoderProfile.QualityValueMin}..{EncoderProfile.QualityValueMax} 的整数）";
            return false;
        }

        encoder = baseEncoder.WithRateControl(rateControl, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        error = "";
        return true;
    }
}
