# 第三方组件说明

## 第三方源码（third_party/videoenhancer-core/）

本目录下的 C# 源码与 `PresetTemplate.json` 是 MIT 许可的第三方代码，来自
[VideoEnhancer](https://github.com/maxzrb/VideoEnhancer) 项目。按 MIT 许可要求保留版权与许可声明。

| 本仓库文件 | 上游路径 |
|---|---|
| `TopazModelCatalog.cs` | `orchestrator/TopazModelCatalog.cs` |
| `TvaiFilterComposer.cs` | `orchestrator/TvaiFilterComposer.cs` |
| `AutoEstimator.cs` | `orchestrator/AutoEstimator.cs` |
| `JobObject.cs` | `router/JobObject.cs` |
| `EncoderProfile.cs` | `tools/VideoEnhancer.TvaiTuner/EncoderProfile.cs` |
| `TunerViewModel.cs` | `tools/VideoEnhancer.TvaiTuner/TunerViewModel.cs` |
| `PresetWriter.cs` | `tools/VideoEnhancer.TvaiTuner/PresetWriter.cs` |
| `PresetTemplate.json` | `tools/VideoEnhancer.TvaiTuner/PresetTemplate.json` |

精确来源提交与逐文件 SHA-256 见 `MANIFEST.json`；同步与校验脚本为 `tools/sync-tvai-core.ps1`。
这些文件属于第三方代码，**请勿在本仓库就地修改**：在上游修改后重跑同步脚本，或把它们替换为你自己的实现。

## 运行期依赖（不分发）

- **Topaz Video AI**（Topaz Labs）：本程序在运行期调用用户自行安装的 Topaz `ffmpeg.exe`，
  以使用其私有的 `tvai_up` / `tvai_pe` 滤镜。本仓库不分发 Topaz 的任何可执行文件、库或模型权重。
  使用须遵守 Topaz Labs 的最终用户许可协议。
- **.NET 10 运行时**：单文件自包含发布已内置，无需单独安装。

## 未使用的第三方包

本项目未引入任何 NuGet 第三方包；界面为纯 WinForms（`System.Windows.Forms`）。
