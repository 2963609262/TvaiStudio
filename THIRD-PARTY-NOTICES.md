# 第三方来源说明

## 共享内核（third_party/videoenhancer-core/）

本目录下的 C# 源码与 `PresetTemplate.json` 复制自
[VideoEnhancer](https://github.com/maxzrb/VideoEnhancer) 的 tvai 子系统，采用 MIT 许可。

| 本仓库文件 | 源仓库路径 |
|---|---|
| `TopazModelCatalog.cs` | `orchestrator/TopazModelCatalog.cs` |
| `TvaiFilterComposer.cs` | `orchestrator/TvaiFilterComposer.cs` |
| `AutoEstimator.cs` | `orchestrator/AutoEstimator.cs` |
| `RouterConfig.cs` | `router/RouterConfig.cs` |
| `ChildProcessRelay.cs` | `router/ChildProcessRelay.cs` |
| `JobObject.cs` | `router/JobObject.cs` |
| `EncoderProfile.cs` | `tools/VideoEnhancer.TvaiTuner/EncoderProfile.cs` |
| `TunerViewModel.cs` | `tools/VideoEnhancer.TvaiTuner/TunerViewModel.cs` |
| `PresetWriter.cs` | `tools/VideoEnhancer.TvaiTuner/PresetWriter.cs` |
| `PresetTemplate.json` | `tools/VideoEnhancer.TvaiTuner/PresetTemplate.json` |

精确来源提交与逐文件 SHA-256 见 `MANIFEST.json`；同步与校验脚本为 `tools/sync-tvai-core.ps1`。

## 运行期依赖（不分发）

- **Topaz Video AI**（Topaz Labs）：本程序在运行期调用用户自行安装的 Topaz `ffmpeg.exe`，
  以使用其私有的 `tvai_up` / `tvai_pe` 滤镜。本仓库不分发 Topaz 的任何可执行文件、库或模型权重。
  使用须遵守 Topaz Labs 的最终用户许可协议。
- **.NET 10 运行时**：单文件自包含发布已内置，无需单独安装。

## 未使用的外部代码

本项目未引入任何 NuGet 第三方包；界面为纯 WinForms（`System.Windows.Forms`）。
