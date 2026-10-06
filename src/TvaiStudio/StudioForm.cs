using System.Globalization;
using System.Runtime.InteropServices;
using VideoEnhancer.Tvai;
using VideoEnhancer.TvaiTuner;

namespace TvaiStudio;

/// <summary>
/// TvaiStudio 主窗口：批量超分转码（Topaz tvai_up 直连）。
///
/// DPI 处理（实测得出的结论）：本程序是 PerMonitorV2 DPI-aware，GDI+ 会按显示器 DPI
/// 自动把「磅值」字体放大（96 DPI 下 9pt 字高 15px，144 DPI 下 23px），但**像素坐标不会**
/// 自动放大。因此这里保持字体用磅值（9F，自动跟随 DPI），同时把所有设计像素坐标乘以
/// <see cref="_scale"/> = 显示器DPI/96。若两者都放大或都不放大，都会出现「字大框小」的裁切。
/// 布局在 <see cref="OnShown"/> 里按实际 DPI 构建（OnLoad 阶段 DPI 仍报 96，不可用）。
/// </summary>
internal sealed class StudioForm : Form
{
    private const int DesignWidth = 1000;
    private const int DesignHeight = 676;
    private const int OuterMargin = 12;

    /// <summary>显示器 DPI / 96；所有设计坐标乘以此值（字体用磅值，不再乘）。</summary>
    private float _scale = 1f;

    private int S(int value) => (int)Math.Round(value * _scale);

    private Rectangle R(int x, int y, int width, int height)
        => new(S(x), S(y), S(width), S(height));

    private int InnerWidth => DesignWidth - OuterMargin * 2;

    private readonly StudioSettings _settings;
    private TopazModelCatalog _catalog;
    private readonly List<QueueItem> _queue = [];

    // 环境
    private readonly Label _lblEnv = new();
    private readonly Button _btnBrowseFfmpeg = new();

    // 模型与模式
    private readonly ComboBox _cmbModel = new();
    private readonly ComboBox _cmbMode = new();
    private readonly ComboBox _cmbEncoder = new();
    private readonly NumericUpDown _numEstimate = new();
    private readonly NumericUpDown _numSampleSeconds = new();
    private readonly NumericUpDown _numScale = new();
    private readonly CheckBox _chkForceCfr = new();
    private readonly Button _btnCheck = new();
    private readonly Button _btnEstimate = new();
    private readonly Label _lblParamHint = new();

    // 参数滑条（manual=绝对值 / relative=偏移）；行固定 6 条，随模型更新标签与值域。
    private readonly Dictionary<string, TrackBar> _sliders = [];
    private readonly Dictionary<string, NumericUpDown> _numbers = [];
    private readonly Dictionary<string, Label> _paramLabels = [];
    private bool _syncingParameters;

    // 队列
    private readonly ListView _listQueue = new();
    private readonly Button _btnAddFiles = new();
    private readonly Button _btnRemove = new();
    private readonly Button _btnClear = new();
    private readonly TextBox _txtOutputDir = new();
    private readonly Button _btnBrowseOutput = new();

    // 执行
    private readonly Button _btnStart = new();
    private readonly Button _btnCancel = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _lblProgress = new();
    private readonly TextBox _log = new();

    private CancellationTokenSource? _cancellation;
    private bool _running;

    public StudioForm()
    {
        _settings = StudioSettings.Load();
        _catalog = TopazModelCatalog.Load(_settings.ModelDir, _settings.ModelDataDir);

        Text = "TvaiStudio — Topaz tvai_up 超分转码";
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(DesignWidth, DesignHeight);
        MinimumSize = new Size(DesignWidth, DesignHeight);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
    }

    /// <summary>
    /// 在窗口落到实际显示器之后再建布局：OnLoad 阶段 DPI 仍报 96，
    /// 此时按 96 建出来的坐标在 144 DPI 下会与自动放大的字体不匹配（字大框小 → 裁切）。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_layoutBuilt)
        {
            return;
        }

        using (var graphics = CreateGraphics())
        {
            _scale = graphics.DpiX / 96f;
        }

        BuildLayout();
        LoadModels();
        RefreshEnvironment();
        AppendLog("[tvaistudio] 就绪。添加文件后可开始编码队列。");
        _layoutBuilt = true;

        if (_pendingFiles.Count > 0)
        {
            AddInputs(_pendingFiles);
            _pendingFiles.Clear();
        }
    }

    private bool _layoutBuilt;
    private readonly List<string> _pendingFiles = [];

    /// <summary>命令行传入的文件：界面首次显示后加入队列（与拖放同一路径）。</summary>
    public void QueueFilesOnLoad(IEnumerable<string> files) => _pendingFiles.AddRange(files);

    // ── 布局（96 DPI 设计坐标，经 S()/R() 换算到实际 DPI） ──

    private void BuildLayout()
    {
        // 环境行
        _lblEnv.Bounds = R(OuterMargin, 12, InnerWidth - 150, 20);
        _lblEnv.AutoEllipsis = true;
        _btnBrowseFfmpeg.Text = "指定 Topaz ffmpeg…";
        _btnBrowseFfmpeg.Bounds = R(OuterMargin + InnerWidth - 138, 9, 138, 26);
        _btnBrowseFfmpeg.Click += (_, _) => BrowseTopazFfmpeg();
        Controls.Add(_lblEnv);
        Controls.Add(_btnBrowseFfmpeg);

        // 模型与模式
        var gbModel = new GroupBox { Text = "模型与模式", Bounds = R(OuterMargin, 36, InnerWidth, 92) };
        Controls.Add(gbModel);

        AddLabel(gbModel, "模型", 12, 24, 40);
        _cmbModel.Bounds = R(54, 21, 250, 24);
        _cmbModel.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbModel.SelectedIndexChanged += (_, _) => OnModelChanged();
        gbModel.Controls.Add(_cmbModel);

        _btnCheck.Text = "校验";
        _btnCheck.Bounds = R(312, 21, 64, 25);
        _btnCheck.Click += (_, _) => RunCheck();
        gbModel.Controls.Add(_btnCheck);

        AddLabel(gbModel, "模式", 392, 24, 40);
        _cmbMode.Bounds = R(434, 21, 110, 24);
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Items.AddRange(["手动", "自动", "相对"]);
        _cmbMode.SelectedIndex = 1;
        _cmbMode.SelectedIndexChanged += (_, _) => OnModeChanged();
        gbModel.Controls.Add(_cmbMode);

        AddLabel(gbModel, "编码器", 560, 24, 50);
        _cmbEncoder.Bounds = R(612, 21, 190, 24);
        _cmbEncoder.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var profile in EncoderProfile.All)
        {
            _cmbEncoder.Items.Add(profile.DisplayName);
        }

        _cmbEncoder.SelectedIndex = Math.Max(0, EncoderProfile.All.ToList().FindIndex(p => p.Key == _settings.EncoderKey));
        gbModel.Controls.Add(_cmbEncoder);

        AddLabel(gbModel, "估计帧数", 12, 58, 60);
        _numEstimate.Bounds = R(74, 55, 56, 24);
        _numEstimate.Minimum = 0;
        _numEstimate.Maximum = 100;
        _numEstimate.Value = Math.Clamp(_settings.EstimateFrames, 0, 100);
        gbModel.Controls.Add(_numEstimate);

        AddLabel(gbModel, "采样秒数", 144, 58, 60);
        _numSampleSeconds.Bounds = R(206, 55, 56, 24);
        _numSampleSeconds.Minimum = 1;
        _numSampleSeconds.Maximum = 600;
        _numSampleSeconds.Value = Math.Clamp((decimal)_settings.SampleSeconds, 1, 600);
        gbModel.Controls.Add(_numSampleSeconds);

        _btnEstimate.Text = "预估（队列选中项）";
        _btnEstimate.Bounds = R(278, 54, 150, 26);
        _btnEstimate.Click += (_, _) => RunEstimate();
        gbModel.Controls.Add(_btnEstimate);

        AddLabel(gbModel, "倍率", 444, 58, 34);
        _numScale.Bounds = R(480, 55, 56, 24);
        _numScale.Minimum = 1;
        _numScale.Maximum = 8;
        _numScale.Value = Math.Clamp(_settings.Scale, 1, 8);
        gbModel.Controls.Add(_numScale);

        _chkForceCfr.Text = "强制 CFR";
        _chkForceCfr.Bounds = R(552, 55, 100, 24);
        _chkForceCfr.Checked = _settings.ForceCfr;
        gbModel.Controls.Add(_chkForceCfr);

        // 参数区：固定 6 行，随模型/模式更新标签与值域。
        var gbParams = new GroupBox { Text = "参数（手动 = 绝对值；相对 = 相对自动的偏移，0 为中性）", Bounds = R(OuterMargin, 132, InnerWidth, 200) };
        Controls.Add(gbParams);

        _lblParamHint.Bounds = R(12, 22, InnerWidth - 24, 18);
        _lblParamHint.ForeColor = Color.DimGray;
        gbParams.Controls.Add(_lblParamHint);

        var top = 46;
        foreach (var name in TvaiFilterComposer.ParameterNames)
        {
            var label = new Label { Bounds = R(12, top + 4, 250, 18), TextAlign = ContentAlignment.MiddleLeft };
            gbParams.Controls.Add(label);
            _paramLabels[name] = label;

            var slider = new TrackBar
            {
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None,
                Bounds = R(268, top, 560, 26),
                Value = 500,
            };
            var number = new NumericUpDown
            {
                Bounds = R(838, top + 2, 90, 24),
                DecimalPlaces = 3,
                Minimum = 0,
                Maximum = 1,
                Increment = 0.005m,
                Value = 0.5m,
            };

            var captured = name;
            slider.ValueChanged += (_, _) => SyncFromSlider(captured);
            number.ValueChanged += (_, _) => SyncFromNumber(captured);

            _sliders[name] = slider;
            _numbers[name] = number;
            gbParams.Controls.Add(slider);
            gbParams.Controls.Add(number);
            top += 25;
        }

        // 批量队列
        var gbQueue = new GroupBox { Text = "批量队列（可拖放文件到窗口）", Bounds = R(OuterMargin, 336, InnerWidth, 172) };
        Controls.Add(gbQueue);

        _btnAddFiles.Text = "添加文件…";
        _btnAddFiles.Bounds = R(12, 22, 106, 26);
        _btnAddFiles.Click += (_, _) => AddFiles();
        gbQueue.Controls.Add(_btnAddFiles);

        _btnRemove.Text = "移除选中";
        _btnRemove.Bounds = R(124, 22, 96, 26);
        _btnRemove.Click += (_, _) => RemoveSelected();
        gbQueue.Controls.Add(_btnRemove);

        _btnClear.Text = "清空";
        _btnClear.Bounds = R(226, 22, 64, 26);
        _btnClear.Click += (_, _) => ClearQueue();
        gbQueue.Controls.Add(_btnClear);

        AddLabel(gbQueue, "输出目录", 302, 25, 60);
        _txtOutputDir.Bounds = R(366, 23, InnerWidth - 366 - 100, 24);
        _txtOutputDir.Text = _settings.OutputDirectory;
        gbQueue.Controls.Add(_txtOutputDir);

        _btnBrowseOutput.Text = "浏览…";
        _btnBrowseOutput.Bounds = R(InnerWidth - 88, 22, 76, 26);
        _btnBrowseOutput.Click += (_, _) => BrowseOutputDirectory();
        gbQueue.Controls.Add(_btnBrowseOutput);

        _listQueue.Bounds = R(12, 54, InnerWidth - 24, 108);
        _listQueue.View = View.Details;
        _listQueue.FullRowSelect = true;
        _listQueue.GridLines = true;
        _listQueue.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _listQueue.Columns.Add("输入", S(470));
        _listQueue.Columns.Add("输出", S(400));
        _listQueue.Columns.Add("状态", S(80));
        gbQueue.Controls.Add(_listQueue);

        // 执行行
        _btnStart.Text = "开始编码队列";
        _btnStart.Bounds = R(OuterMargin, 514, 140, 30);
        _btnStart.Click += (_, _) => StartQueue();
        Controls.Add(_btnStart);

        _btnCancel.Text = "取消";
        _btnCancel.Bounds = R(OuterMargin + 150, 514, 80, 30);
        _btnCancel.Enabled = false;
        _btnCancel.Click += (_, _) => CancelQueue();
        Controls.Add(_btnCancel);

        _progress.Bounds = R(OuterMargin + 240, 518, InnerWidth - 240, 22);
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        Controls.Add(_progress);

        _lblProgress.Bounds = R(OuterMargin, 548, InnerWidth, 18);
        _lblProgress.ForeColor = Color.DimGray;
        _lblProgress.AutoEllipsis = true;
        Controls.Add(_lblProgress);

        _log.Bounds = R(OuterMargin, 570, InnerWidth, DesignHeight - 570 - OuterMargin);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 8.5F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_log);

        ClientSize = new Size(S(DesignWidth), S(DesignHeight));
        MinimumSize = ClientSize;
    }

    private void AddLabel(Control host, string text, int left, int top, int width)
        => host.Controls.Add(new Label
        {
            Text = text,
            Bounds = R(left, top, width, 20),
            TextAlign = ContentAlignment.MiddleLeft,
        });

    // ── 参数同步 ──

    private void SyncFromSlider(string name)
    {
        if (_syncingParameters || !_sliders.TryGetValue(name, out var slider) || !_numbers.TryGetValue(name, out var number))
        {
            return;
        }

        var spec = CurrentSpecs().FirstOrDefault(s => s.Name == name);
        if (spec is null)
        {
            return;
        }

        _syncingParameters = true;
        var value = spec.Min + (spec.Max - spec.Min) * slider.Value / 1000.0;
        number.Value = (decimal)Math.Clamp(value, (double)number.Minimum, (double)number.Maximum);
        _syncingParameters = false;
    }

    private void SyncFromNumber(string name)
    {
        if (_syncingParameters || !_sliders.TryGetValue(name, out var slider) || !_numbers.TryGetValue(name, out var number))
        {
            return;
        }

        var spec = CurrentSpecs().FirstOrDefault(s => s.Name == name);
        if (spec is null)
        {
            return;
        }

        _syncingParameters = true;
        var span = spec.Max - spec.Min;
        slider.Value = span <= 0
            ? 500
            : Math.Clamp((int)Math.Round(((double)number.Value - spec.Min) / span * 1000), 0, 1000);
        _syncingParameters = false;
    }

    /// <summary>按当前模型与模式给出 6 个参数的值域（manual=模型原生，relative=±1 偏移）。</summary>
    private IReadOnlyList<TunerViewModel.ParameterSpec> CurrentSpecs()
    {
        var model = SelectedModel();
        if (model is null)
        {
            return TvaiFilterComposer.ParameterNames
                .Select(name => new TunerViewModel.ParameterSpec(name, name, 0, 1, 0.5))
                .ToList();
        }

        return TunerViewModel.BuildParameterSpecs(model, SelectedMode());
    }

    /// <summary>模型或模式变化后刷新参数行的标签、值域与可用状态。</summary>
    private void RefreshParameters()
    {
        var model = SelectedModel();
        var specs = CurrentSpecs();
        var hasManualParameters = model is null || model.Parameters.Count > 0;

        _lblParamHint.Text = !hasManualParameters
            ? "该模型定义中没有可调参数（自动/相对模式仍可用）。"
            : SelectedMode() == "relative"
                ? "相对模式：滑条范围 ±1，0 = 中性（在自动基线上做偏移）。"
                : "手动模式：数值为模型原生单位；滑条在值域内线性映射。";

        _syncingParameters = true;
        foreach (var spec in specs)
        {
            if (_paramLabels.TryGetValue(spec.Name, out var label))
            {
                label.Text = $"{spec.GuiName}（{spec.Name}）";
                label.ForeColor = hasManualParameters ? SystemColors.ControlText : SystemColors.GrayText;
            }

            if (!_sliders.TryGetValue(spec.Name, out var slider) || !_numbers.TryGetValue(spec.Name, out var number))
            {
                continue;
            }

            slider.Enabled = hasManualParameters;
            number.Enabled = hasManualParameters;
            number.Minimum = (decimal)spec.Min;
            number.Maximum = (decimal)spec.Max;
            number.Value = (decimal)Math.Clamp(spec.Default, spec.Min, spec.Max);
            var span = spec.Max - spec.Min;
            slider.Value = span <= 0
                ? 500
                : Math.Clamp((int)Math.Round((spec.Default - spec.Min) / span * 1000), 0, 1000);
        }

        _syncingParameters = false;
    }

    // ── 环境与模型 ──

    private void RefreshEnvironment()
    {
        if (_settings.IsRunnable(out var problem))
        {
            _lblEnv.ForeColor = Color.FromArgb(0, 120, 60);
            _lblEnv.Text = $"Topaz ffmpeg：{_settings.TopazFfmpeg}　|　模型定义：{_settings.ModelDir}　|　权重：{_settings.ModelDataDir}";
        }
        else
        {
            _lblEnv.ForeColor = Color.Firebrick;
            _lblEnv.Text = problem;
        }
    }

    private void LoadModels()
    {
        _catalog = TopazModelCatalog.Load(_settings.ModelDir, _settings.ModelDataDir);
        _cmbModel.Items.Clear();
        foreach (var id in _catalog.Ids)
        {
            var model = _catalog.Find(id)!;
            var weights = _catalog.CountWeightFiles(model.WeightFilePrefix);
            _cmbModel.Items.Add($"{model.DisplayName}（{model.Id}） · 权重 {weights}");
        }

        if (_cmbModel.Items.Count == 0)
        {
            AppendLog("[tvaistudio] 未找到任何模型定义，请检查模型定义目录。");
            RefreshParameters();
            return;
        }

        var index = _catalog.Ids.ToList().IndexOf(_settings.Model);
        _cmbModel.SelectedIndex = index >= 0 ? index : 0;
    }

    private TvaiModel? SelectedModel()
    {
        var index = _cmbModel.SelectedIndex;
        var ids = _catalog.Ids.ToList();
        return index >= 0 && index < ids.Count ? _catalog.Find(ids[index]) : null;
    }

    private string SelectedMode()
        => _cmbMode.SelectedIndex switch { 0 => "manual", 2 => "relative", _ => "auto" };

    private EncoderProfile SelectedEncoder()
        => EncoderProfile.All[Math.Max(0, _cmbEncoder.SelectedIndex)];

    private void OnModelChanged()
    {
        var model = SelectedModel();
        if (model is null)
        {
            return;
        }

        _settings.Model = model.Id;
        var supportsAuto = model.AutoModel.Length > 0;
        _cmbMode.Items[1] = supportsAuto ? "自动" : "自动（不支持）";
        _cmbMode.Items[2] = supportsAuto ? "相对" : "相对（不支持）";
        RefreshParameters();
    }

    private void OnModeChanged()
    {
        var model = SelectedModel();
        if (model is not null && model.AutoModel.Length == 0 && SelectedMode() != "manual")
        {
            _cmbMode.SelectedIndex = 0;
            AppendLog($"[tvaistudio] 模型 {model.Id} 不支持自动/相对模式，已切回手动。");
            return;
        }

        RefreshParameters();
    }

    // ── 队列操作 ──

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddInputs(files);
        }
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.m4v;*.ts;*.flv|所有文件|*.*",
            InitialDirectory = Directory.Exists(_settings.LastInputDirectory) ? _settings.LastInputDirectory : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddInputs(dialog.FileNames);
        }
    }

    private void AddInputs(IEnumerable<string> files)
    {
        var added = 0;
        string? first = null;
        foreach (var file in files)
        {
            if (!File.Exists(file) || _queue.Any(item => string.Equals(item.Input, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            first ??= file;
            var item = new QueueItem(file, ComputeOutputPath(file));
            _queue.Add(item);
            var row = new ListViewItem(Path.GetFileName(file)) { Tag = item };
            row.SubItems.Add(item.Output);
            row.SubItems.Add("待处理");
            _listQueue.Items.Add(row);
            added++;
        }

        if (added > 0)
        {
            if (first is not null)
            {
                _settings.LastInputDirectory = Path.GetDirectoryName(first) ?? _settings.LastInputDirectory;
            }

            AppendLog($"[tvaistudio] 已加入 {added} 个文件，队列共 {_queue.Count} 项。");
        }
    }

    private string ComputeOutputPath(string input)
    {
        var directory = _txtOutputDir.Text.Trim();
        if (directory.Length == 0)
        {
            directory = _settings.OutputDirectory;
        }

        var name = Path.GetFileNameWithoutExtension(input);
        var candidate = Path.Combine(directory, name + "_tvai.mp4");
        for (var index = 2; File.Exists(candidate) && index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{name}_tvai-{index}.mp4");
        }

        return candidate;
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem row in _listQueue.SelectedItems)
        {
            if (row.Tag is QueueItem item)
            {
                _queue.Remove(item);
            }

            _listQueue.Items.Remove(row);
        }
    }

    private void ClearQueue()
    {
        _queue.Clear();
        _listQueue.Items.Clear();
    }

    private void BrowseTopazFfmpeg()
    {
        using var dialog = new OpenFileDialog { Filter = "ffmpeg|ffmpeg.exe|所有文件|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _settings.TopazFfmpeg = dialog.FileName;
            _settings.Save();
            RefreshEnvironment();
        }
    }

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtOutputDir.Text = dialog.SelectedPath;
        }
    }

    // ── 校验与预估 ──

    private void RunCheck()
    {
        var model = SelectedModel();
        if (model is null)
        {
            return;
        }

        var weights = _catalog.CountWeightFiles(model.WeightFilePrefix);
        var estimator = model.AutoModel.Length > 0 ? _catalog.Find(model.AutoModel) : null;
        var estimatorWeights = estimator is null ? 0 : _catalog.CountWeightFiles(estimator.WeightFilePrefix);
        AppendLog($"[校验] {model.Id}：模型权重 {weights} 个；估计器 {(estimator is null ? "无（不支持自动/相对）" : $"{estimator.Id} → {estimatorWeights} 个")}");
        if (weights == 0)
        {
            AppendLog("[校验] 缺少模型权重：Topaz 会直接崩溃，请先在 Topaz Video AI 中下载该模型。");
        }
        else if (estimator is not null && estimatorWeights == 0)
        {
            AppendLog($"[校验] 自动/相对模式不可用：估计器 {estimator.Id} 的权重缺失。");
        }
        else
        {
            AppendLog("[校验] 权重齐全。");
        }
    }

    private void RunEstimate()
    {
        var model = SelectedModel();
        if (model is null || _listQueue.SelectedItems.Count == 0)
        {
            AppendLog("[预估] 请先在队列中选中一个输入文件。");
            return;
        }

        if (model.AutoModel.Length == 0)
        {
            AppendLog($"[预估] 模型 {model.Id} 不支持自动估计（未声明 autoModel）。");
            return;
        }

        var input = ((QueueItem)_listQueue.SelectedItems[0].Tag!).Input;
        var sampleSeconds = (double)_numSampleSeconds.Value;
        RunBusy("预估中…", () =>
        {
            if (!AutoEstimator.TryEstimate(
                    _settings.TopazFfmpeg,
                    input,
                    model.AutoModel,
                    sampleSeconds,
                    _settings.BuildEnvironmentVariables(),
                    out var estimation,
                    out var failure))
            {
                AppendLog($"[预估] 失败：{failure}");
                return;
            }

            AppendLog($"[预估] 估计器 {estimation.Estimator}，{estimation.SampleCount} 个样本，中位数：");
            _syncingParameters = true;
            for (var index = 0; index < estimation.Values.Length; index++)
            {
                var name = TvaiFilterComposer.ParameterNames[index];
                AppendLog($"    {name} = {estimation.Values[index].ToString("0.######", CultureInfo.InvariantCulture)}");
                if (SelectedMode() == "manual" && _numbers.TryGetValue(name, out var number))
                {
                    number.Value = (decimal)Math.Clamp(estimation.Values[index], (double)number.Minimum, (double)number.Maximum);
                }
            }

            _syncingParameters = false;
        });
    }

    // ── 队列执行 ──

    private void StartQueue()
    {
        if (_running)
        {
            return;
        }

        if (_queue.Count == 0)
        {
            AppendLog("[队列] 队列为空，请先添加文件。");
            return;
        }

        var model = SelectedModel();
        if (model is null)
        {
            return;
        }

        if (!_settings.IsRunnable(out var problem))
        {
            AppendLog($"[队列] {problem}");
            return;
        }

        var mode = SelectedMode();
        if (mode is "auto" or "relative" && model.AutoModel.Length == 0)
        {
            AppendLog($"[队列] 模型 {model.Id} 不支持 {mode} 模式，请改用「手动」。");
            return;
        }

        // 快照当前界面设置，避免编码期间用户改动造成队列内前后项不一致。
        _settings.OutputDirectory = _txtOutputDir.Text.Trim();
        _settings.EncoderKey = SelectedEncoder().Key;
        _settings.Mode = mode;
        _settings.EstimateFrames = (int)_numEstimate.Value;
        _settings.SampleSeconds = (double)_numSampleSeconds.Value;
        _settings.Scale = (int)_numScale.Value;
        _settings.ForceCfr = _chkForceCfr.Checked;
        _settings.Save();

        var encoder = SelectedEncoder();
        var estimateFrames = (int)_numEstimate.Value;
        var scale = (int)_numScale.Value;
        var forceCfr = _chkForceCfr.Checked;
        var manualValues = mode == "manual" ? CurrentManualValues() : null;
        var relativeOffsets = mode == "relative" ? CurrentRelativeOffsets() : null;
        var items = _queue.ToList();

        _running = true;
        _btnStart.Enabled = false;
        _btnCancel.Enabled = true;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var pipeline = new EncodePipeline(_settings);

        Task.Run(() =>
        {
            var succeeded = 0;
            var failed = 0;
            var cancelled = 0;

            for (var index = 0; index < items.Count; index++)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled += items.Count - index;
                    break;
                }

                var item = items[index];
                var position = index;
                Post(() =>
                {
                    item.Status = "编码中";
                    RefreshRow(item);
                    _progress.Value = 0;
                    _lblProgress.Text = $"({position + 1}/{items.Count}) {Path.GetFileName(item.Input)}";
                });

                var request = new EncodeRequest(
                    item.Input,
                    item.Output,
                    model,
                    mode,
                    estimateFrames,
                    scale,
                    null,
                    null,
                    manualValues,
                    relativeOffsets,
                    null,
                    null,
                    null,
                    encoder,
                    forceCfr);

                Directory.CreateDirectory(Path.GetDirectoryName(item.Output) ?? _settings.OutputDirectory);
                Post(() => AppendLog($"[队列] 开始：{Path.GetFileName(item.Input)} → {Path.GetFileName(item.Output)}"));
                Post(() => AppendLog($"[命令行] {EncodePipeline.DescribeArguments(EncodePipeline.BuildArguments(request))}"));

                var exitCode = pipeline.Run(
                    request,
                    progress => Post(() => UpdateProgress(progress)),
                    line => Post(() => AppendLog(line)),
                    token,
                    out var encodeError);

                if (encodeError.Length > 0)
                {
                    Post(() => AppendLog($"[队列] 错误：{encodeError}"));
                }

                if (exitCode == EncodePipeline.CancelledExitCode)
                {
                    cancelled += items.Count - index;
                    DeletePartialOutput(item.Output);
                    Post(() =>
                    {
                        item.Status = "已取消";
                        RefreshRow(item);
                    });
                    break;
                }

                if (exitCode == 0)
                {
                    succeeded++;
                    Post(() =>
                    {
                        item.Status = "完成";
                        RefreshRow(item);
                    });
                }
                else
                {
                    failed++;
                    var code = exitCode;
                    Post(() =>
                    {
                        item.Status = $"失败({code})";
                        RefreshRow(item);
                    });
                }
            }

            Post(() =>
            {
                _running = false;
                _btnStart.Enabled = true;
                _btnCancel.Enabled = false;
                _cancellation?.Dispose();
                _cancellation = null;
                _progress.Value = 0;
                _lblProgress.Text = $"队列结束：成功 {succeeded}，失败 {failed}，取消 {cancelled}";
                AppendLog($"[队列] {_lblProgress.Text}");
            });
        }, token);
    }

    private IReadOnlyDictionary<string, double> CurrentManualValues()
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in TvaiFilterComposer.ParameterNames)
        {
            if (_numbers.TryGetValue(name, out var number))
            {
                values[name] = (double)number.Value;
            }
        }

        return values;
    }

    private IReadOnlyDictionary<string, double> CurrentRelativeOffsets()
        => CurrentManualValues();

    private void CancelQueue()
    {
        _cancellation?.Cancel();
        AppendLog("[队列] 已请求取消，正在终止 Topaz ffmpeg 进程树…");
    }

    private void RefreshRow(QueueItem item)
    {
        foreach (ListViewItem row in _listQueue.Items)
        {
            if (ReferenceEquals(row.Tag, item))
            {
                row.SubItems[2].Text = item.Status;
                return;
            }
        }
    }

    private void UpdateProgress(EncodeProgress progress)
    {
        var text = $"{progress.Stage}　帧 {progress.Frame}　{progress.Fps:0.0} fps　{progress.Time}";
        if (progress.Speed is not null)
        {
            text += $"　{progress.Speed:0.00}x";
        }

        if (progress.BitrateKbps is not null)
        {
            text += $"　{progress.BitrateKbps:0} kbps";
        }

        _lblProgress.Text = text;

        // 百分比由 stage 文本携带（只有已知总时长时才有）。
        var percentIndex = progress.Stage.IndexOf('%');
        if (percentIndex > 0)
        {
            var start = progress.Stage.LastIndexOf(' ', percentIndex) + 1;
            if (double.TryParse(progress.Stage[start..percentIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                _progress.Value = Math.Clamp((int)(percent * 10), 0, 1000);
            }
        }
    }

    // ── 工具 ──

    private void RunBusy(string caption, Action action)
    {
        var previous = Cursor;
        Cursor = Cursors.WaitCursor;
        var previousText = _lblProgress.Text;
        _lblProgress.Text = caption;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] {ex.Message}");
        }
        finally
        {
            Cursor = previous;
            if (!_running)
            {
                _lblProgress.Text = previousText;
            }
        }
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 窗口已关闭：丢弃 UI 更新。
        }
        catch (InvalidOperationException)
        {
            // 句柄已销毁：丢弃 UI 更新。
        }
    }

    private void AppendLog(string line)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _log.AppendText(line + Environment.NewLine);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cancellation?.Cancel();
        base.OnFormClosing(e);
    }

    /// <summary>取消后删除被中断的产物，避免留下 0 字节或半截文件误导用户。</summary>
    private static void DeletePartialOutput(string output)
    {
        try
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
        catch
        {
            // 文件被占用等情况下保留。
        }
    }

    private sealed class QueueItem(string input, string output)
    {
        public string Input { get; } = input;
        public string Output { get; } = output;
        public string Status { get; set; } = "待处理";
    }
}
