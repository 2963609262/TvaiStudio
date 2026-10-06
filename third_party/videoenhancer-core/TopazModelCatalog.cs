using System.Text.Json;

namespace VideoEnhancer.Tvai;

/// <summary>模型 json 中的一个可调参数（来自 Topaz 模型定义的 parameters/extraParameters 数组）。</summary>
internal sealed class TvaiParameter
{
    public required string Name { get; init; }

    public required string GuiName { get; init; }

    public required double Min { get; init; }

    public required double Max { get; init; }

    public required double Default { get; init; }
}

/// <summary>一个 Topaz 模型的目录条目（只读，来自 <c>TVAI_MODEL_DIR\&lt;id&gt;.json</c>）。</summary>
internal sealed class TvaiModel
{
    public required string Id { get; init; }

    public required string ShortName { get; init; }

    public required string Version { get; init; }

    public required string DisplayName { get; init; }

    public required int ModelType { get; init; }

    /// <summary>自动参数估计器（如 prap-3 / nap-3）；空=该模型不支持自动/相对模式。</summary>
    public required string AutoModel { get; init; }

    public required List<TvaiParameter> Parameters { get; init; }

    public string WeightFilePrefix => $"{ShortName}-v{Version}-";
}

/// <summary>
/// Topaz 模型目录：扫描 json 定义目录，提供模型查找与本地权重核对。
/// 只读，不写任何文件；权重缺失时由调用方给出 exit 3 的明确错误（Topaz ffmpeg 自身缺权重时会段错误）。
/// </summary>
internal sealed class TopazModelCatalog
{
    private readonly Dictionary<string, TvaiModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _weightsDirectory;

    private TopazModelCatalog(string definitionsDirectory, string weightsDirectory)
    {
        _weightsDirectory = weightsDirectory;
        if (!Directory.Exists(definitionsDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(definitionsDirectory, "*.json"))
        {
            var model = TryParse(path);
            if (model is not null)
            {
                _models[model.Id] = model;
            }
        }
    }

    public static TopazModelCatalog Load(string definitionsDirectory, string weightsDirectory)
        => new(definitionsDirectory, weightsDirectory);

    public int Count => _models.Count;

    public TvaiModel? Find(string id)
        => _models.TryGetValue(id, out var model) ? model : null;

    public IEnumerable<string> Ids => _models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    /// <summary>本地是否已下载该模型的权重（任一 <c>&lt;shortName&gt;-v&lt;version&gt;-*</c> 文件；兼容 .tz 与 .tz3）。</summary>
    public bool HasWeights(TvaiModel model)
        => CountWeightFiles(model.WeightFilePrefix) > 0;

    public int CountWeightFiles(string filePrefix)
    {
        if (!Directory.Exists(_weightsDirectory))
        {
            return 0;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_weightsDirectory, filePrefix + "*"))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".tz", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tz3", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    public string WeightsDirectory => _weightsDirectory;

    private static TvaiModel? TryParse(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (!root.TryGetProperty("shortName", out var shortNameElement)
                || shortNameElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var id = Path.GetFileNameWithoutExtension(path);
            var shortName = shortNameElement.GetString() ?? "";
            if (shortName.Length == 0)
            {
                return null;
            }

            var version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.ToString()
                : "";

            var displayName = root.TryGetProperty("displayName", out var displayElement)
                ? (displayElement.GetString() ?? id)
                : id;

            var modelType = root.TryGetProperty("modelType", out var typeElement)
                            && typeElement.TryGetInt32(out var typeValue)
                ? typeValue
                : 0;

            var autoModel = root.TryGetProperty("autoModel", out var autoElement)
                && autoElement.ValueKind == JsonValueKind.String
                ? (autoElement.GetString() ?? "")
                : "";

            var parameters = new List<TvaiParameter>();
            if (root.TryGetProperty("parameters", out var parametersElement)
                && parametersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in parametersElement.EnumerateArray())
                {
                    var parameter = ParseParameter(item);
                    if (parameter is not null)
                    {
                        parameters.Add(parameter);
                    }
                }
            }

            return new TvaiModel
            {
                Id = id,
                ShortName = shortName,
                Version = version,
                DisplayName = displayName,
                ModelType = modelType,
                AutoModel = autoModel,
                Parameters = parameters,
            };
        }
        catch
        {
            return null;
        }
    }

    private static TvaiParameter? ParseParameter(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = nameElement.GetString() ?? "";
        if (name.Length == 0)
        {
            return null;
        }

        var guiName = item.TryGetProperty("guiName", out var guiElement)
            && guiElement.ValueKind == JsonValueKind.String
            ? (guiElement.GetString() ?? name)
            : name;

        return new TvaiParameter
        {
            Name = name,
            GuiName = guiName,
            Min = ReadDouble(item, "min", 0),
            Max = ReadDouble(item, "max", 1),
            Default = ReadDouble(item, "default", 0.5),
        };
    }

    private static double ReadDouble(JsonElement item, string name, double fallback)
        => item.TryGetProperty(name, out var element) && element.TryGetDouble(out var value)
            ? value
            : fallback;
}
