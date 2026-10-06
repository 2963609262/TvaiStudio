using VideoEnhancer.Tvai;

namespace VideoEnhancer.TvaiTuner;

/// <summary>调参界面的取值规则：六个参数的值域/默认值随模型与模式变化。</summary>
internal static class TunerViewModel
{
    public sealed record ParameterSpec(string Name, string GuiName, double Min, double Max, double Default);

    /// <summary>
    /// manual：按模型 json 的 min/max/default；relative：统一 -1..1、默认 0（偏移，0=中性）。
    /// 参数顺序与 tvai_pe/模型 json 保持一致（preBlur/noise/details/halo/blur/compression）。
    /// </summary>
    public static IReadOnlyList<ParameterSpec> BuildParameterSpecs(TvaiModel model, string mode)
    {
        var specs = new List<ParameterSpec>();
        foreach (var name in TvaiFilterComposer.ParameterNames)
        {
            var fromModel = model.Parameters.FirstOrDefault(
                parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            specs.Add(mode == "relative"
                ? new ParameterSpec(name, fromModel?.GuiName ?? name, -1, 1, 0)
                : new ParameterSpec(
                    name,
                    fromModel?.GuiName ?? name,
                    fromModel?.Min ?? 0,
                    fromModel?.Max ?? 1,
                    fromModel?.Default ?? 0.5));
        }

        return specs;
    }
}
