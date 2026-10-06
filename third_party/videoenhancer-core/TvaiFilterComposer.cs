namespace VideoEnhancer.Tvai;

/// <summary>tvai_up 滤镜串中的一个 <c>name=value</c> 选项（按出现顺序保存）。</summary>
internal sealed class FilterOption
{
    public required string Name { get; init; }

    public string Value { get; set; } = "";
}

/// <summary>
/// tvai_up 滤镜串的解析与合成。切分对单引号与反斜杠转义免疫，
/// 因此 <c>parameters='grain_sigma=0.42\:grain_type=silver_rich'</c> 会保持为单个选项值。
/// </summary>
internal static class TvaiFilterComposer
{
    /// <summary>模型可调参数名（顺序与 tvai_pe 输出、Topaz 模型 json 一致）。</summary>
    public static readonly string[] ParameterNames = ["preBlur", "noise", "details", "halo", "blur", "compression"];

    public static bool ContainsTvaiUp(string token)
        => token.Contains("tvai_up=", StringComparison.OrdinalIgnoreCase);

    /// <summary>在完整参数数组里定位含 tvai_up= 的那个 token；找不到返回 -1。</summary>
    public static int FindFilterTokenIndex(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (ContainsTvaiUp(args[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>读取 tvai_up 段的某个选项值；不存在返回 null。</summary>
    public static string? TryGetOption(string filterToken, string optionName)
        => TryParse(filterToken, out _, out var options) && TryFindOption(options, optionName, out var option)
            ? option.Value
            : null;

    public static string? TryGetModel(string filterToken)
        => TryGetOption(filterToken, "model");

    /// <summary>
    /// 合成 tvai_up 段：可覆盖 model/scale，可选写入 estimate=N，可选写入/清除 6 个具名参数。
    /// 滤镜链中的其它滤镜与未知选项一律原样保留。
    /// </summary>
    public static bool TryCompose(
        string filterToken,
        string? model,
        int? scale,
        int? estimateFrames,
        bool clearParameters,
        IReadOnlyDictionary<string, double>? parameterValues,
        out string composed,
        out string error)
    {
        composed = filterToken;
        error = "";
        if (!TryParse(filterToken, out var segmentIndex, out var options))
        {
            error = "未在命令行的滤镜串中找到 tvai_up= 段";
            return false;
        }

        if (!string.IsNullOrEmpty(model))
        {
            SetOption(options, "model", model);
        }

        if (scale is not null)
        {
            SetOption(options, "scale", scale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (estimateFrames is not null)
        {
            SetOption(options, "estimate", estimateFrames.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (clearParameters)
        {
            foreach (var name in ParameterNames)
            {
                RemoveOption(options, name);
            }
        }

        if (parameterValues is not null)
        {
            foreach (var pair in parameterValues)
            {
                SetOption(options, pair.Key, pair.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var segments = SplitRespectingQuotes(filterToken, ',');
        segments[segmentIndex] = "tvai_up=" + string.Join(':', options.Select(o => $"{o.Name}={o.Value}"));
        composed = string.Join(',', segments);
        return true;
    }

    // ── 解析实现 ──

    private static bool TryParse(string filterToken, out int segmentIndex, out List<FilterOption> options)
    {
        segmentIndex = -1;
        options = new List<FilterOption>();
        var segments = SplitRespectingQuotes(filterToken, ',');
        for (var index = 0; index < segments.Count; index++)
        {
            if (!segments[index].StartsWith("tvai_up=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            segmentIndex = index;
            foreach (var raw in SplitRespectingQuotes(segments[index]["tvai_up=".Length..], ':'))
            {
                var separator = FindUnquoted(raw, '=');
                if (separator <= 0)
                {
                    continue;
                }

                options.Add(new FilterOption
                {
                    Name = raw[..separator],
                    Value = raw[(separator + 1)..],
                });
            }

            return true;
        }

        return false;
    }

    private static bool TryFindOption(List<FilterOption> options, string name, out FilterOption option)
    {
        var found = options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        option = found!;
        return found is not null;
    }

    private static void SetOption(List<FilterOption> options, string name, string value)
    {
        if (TryFindOption(options, name, out var existing))
        {
            existing.Value = value;
            return;
        }

        // 插在 parameters= 之前（基础选项在前、扩展串在后的可读顺序），否则追加到末尾。
        var insertAt = options.FindIndex(o => o.Name.Equals("parameters", StringComparison.OrdinalIgnoreCase));
        var option = new FilterOption { Name = name, Value = value };
        if (insertAt < 0)
        {
            options.Add(option);
        }
        else
        {
            options.Insert(insertAt, option);
        }
    }

    private static void RemoveOption(List<FilterOption> options, string name)
    {
        if (TryFindOption(options, name, out var existing))
        {
            options.Remove(existing);
        }
    }

    /// <summary>按分隔符切分，单引号内的分隔符不切；反斜杠转义的下一个字符不参与判定（原样保留）。</summary>
    private static List<string> SplitRespectingQuotes(string text, char separator)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '\\' && index + 1 < text.Length)
            {
                current.Append(ch).Append(text[index + 1]);
                index++;
                continue;
            }

            if (ch == '\'')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }

            if (ch == separator && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static int FindUnquoted(string text, char target)
    {
        var inQuotes = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '\\' && index + 1 < text.Length)
            {
                index++;
                continue;
            }

            if (ch == '\'')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == target && !inQuotes)
            {
                return index;
            }
        }

        return -1;
    }
}
