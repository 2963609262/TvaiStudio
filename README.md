# TvaiStudio

面向 **Topaz Video AI** 超分滤镜 `tvai_up` 的批量转码工具（Windows / .NET 10 / WinForms）。

它直接调用**你自己安装的 Topaz Video AI 自带的 `ffmpeg.exe`** —— `tvai_up`（超分）与 `tvai_pe`（参数估计）是 Topaz 私有的 ffmpeg 滤镜，只存在于该构建中。程序提供图形界面与无窗口命令行两种用法。

> 本仓库**不包含**任何 Topaz Labs 的可执行文件或模型权重。你需要自行安装并授权 Topaz Video AI。

## 功能

- **批量队列**：添加多个文件（或拖放到窗口），顺序编码，逐项显示状态；支持移除/清空。
- **三种参数模式**：
  - 手动：6 个参数取绝对值；
  - 自动：`estimate=N`，由 Topaz 自行估计；
  - 相对：自动基线 + 6 项偏移（0 为中性）。
- **预览估计**：对选中项取中段采样跑 `tvai_pe`，逐帧解析后取中位数；手动模式会回填滑条。
- **模型与权重校验**：列出本地模型与权重数量，缺权重提前拦截（Topaz 缺权重会直接段错误退出）。
- **编码器档位**：H.264 NVENC CQP 18 / HEVC NVENC VBR CQ 28 / AV1 NVENC VBR CQ 38，像素格式强制 `yuv420p`。
- **实时进度与取消**：解析 ffmpeg 进度行；取消时按进程树终止，并删除被中断的半成品文件。
- **导出预设文件**：把当前参数写成一个 JSON 预设，便于复用同一套配置。
- **无窗口模式**：`--self-test` / `--models` / `--check` / `--estimate-only` / `--encode` / `--batch` / `--export-preset`，便于脚本化与自动化验收。

## 为什么必须用 Topaz 自带的 ffmpeg

`tvai_up` / `tvai_pe` 是 Topaz 私有的 ffmpeg 滤镜，官方 ffmpeg 构建里没有。因此本程序：

1. 自动探测常见安装位置（`D:\Topaz Labs LLC\Topaz Video*`、`C:\Program Files\Topaz Labs LLC\Topaz Video*`）寻找 `ffmpeg.exe`；
2. 探测模型定义目录与权重目录（`%ProgramData%\Topaz Labs LLC\Topaz Video\models` 与安装目录下的 `models`）；
3. 探测不到时可在界面里手动指定；设置保存在 EXE 同目录的 `tvaistudio.config.json`。

## 使用

图形界面（无参数）：

```
tvaistudio.exe
tvaistudio.exe a.mp4 b.mp4        # 打开界面并把文件预置进队列
```

无窗口模式：

```
tvaistudio.exe --self-test
tvaistudio.exe --models
tvaistudio.exe --check --model=rhea-1 --mode=auto
tvaistudio.exe --estimate-only --model=rhea-1 --input=in.mp4
tvaistudio.exe --encode --input=in.mp4 --output=out.mp4 --model=rhea-1 --mode=auto --encoder=hevc
tvaistudio.exe --batch --input=a.mp4 --input=b.mp4 --output-dir=out --model=rhea-1 --mode=auto
tvaistudio.exe --export-preset=out.json --model=rhea-1 --mode=auto --encoder=hevc
```

退出码：`0` 成功；`2` 用法/配置错误；`3` 缺少依赖（Topaz ffmpeg 或模型权重）；其余为 ffmpeg 原始退出码。

## 构建

需要 .NET SDK 10：

```
dotnet publish src/TvaiStudio/TvaiStudio.csproj -c Release
```

产物为单文件自包含 `tvaistudio.exe`（约 52 MB，未裁剪——WinForms 不能裁剪）。

## 已知限制

- 仅 Windows；编码器档位面向 NVIDIA NVENC（已在 RTX 5060 Laptop 上验证，支持 AV1 NVENC 4:2:0）。
- 目标尺寸（`--width/--height`）实现为在 `tvai_up` 之后串一个标准 `scale` 滤镜：实测
  `tvai_up` 自身的 `scale=0:w=W:h=H` 写法会被静默忽略（仍按模型原生倍率输出）。
- 相对偏移的 clamp 细节与 Topaz GUI 的逐值对拍（±0.005）尚未完成。
- 不下载模型权重：请先在 Topaz Video AI 中让对应模型完成一次导出/Estimate 以获取权重。
- 参数为整批共用（尚未支持逐项覆盖）。

## 许可证

本项目为 MIT（见 [LICENSE](LICENSE)）。Topaz Video AI 及其模型、可执行文件归 Topaz Labs 所有，
不在本项目分发范围内，使用时须遵守其许可协议。

本项目包含少量来自第三方的 MIT 授权源码，来源与许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
