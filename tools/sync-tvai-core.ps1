# TvaiStudio 共享内核同步脚本
#
# 把 VideoEnhancer 仓库里的 tvai 内核源码复制到本仓库 third_party/videoenhancer-core/，
# 并记录每个文件的来源路径与 SHA-256 到 MANIFEST.json。
#
#   pwsh -File tools/sync-tvai-core.ps1                       # 从默认位置同步
#   pwsh -File tools/sync-tvai-core.ps1 -SourceRepo <path>    # 指定 VideoEnhancer 仓库
#   pwsh -File tools/sync-tvai-core.ps1 -Verify               # 只校验，不写入（CI 用）
#
# 设计取舍：这些文件在 VideoEnhancer 仓库里是「单一实现」（router/orchestrator/tuner 经
# <Compile Include> 共用同一份源码）。本仓库是下游消费者，因此采用「复制 + 清单校验」而不是
# 子模块：公开仓库的每个文件都带明确来源，漂移可用 -Verify 一行检出。
[CmdletBinding()]
param(
    [string]$SourceRepo = '',
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetDir = Join-Path $repoRoot 'third_party\videoenhancer-core'
$manifestPath = Join-Path $targetDir 'MANIFEST.json'

if ([string]::IsNullOrWhiteSpace($SourceRepo)) {
    # 默认与本仓库同级；可用环境变量覆盖。
    if ($env:TVAISTUDIO_CORE_SOURCE) {
        $SourceRepo = $env:TVAISTUDIO_CORE_SOURCE
    } else {
        $SourceRepo = Join-Path (Split-Path $repoRoot -Parent) 'VideoEnhancer\VideoEnhancer'
    }
}

if (-not (Test-Path -LiteralPath $SourceRepo)) {
    throw "找不到 VideoEnhancer 仓库：$SourceRepo（用 -SourceRepo 或 TVAISTUDIO_CORE_SOURCE 指定）"
}

# 同步清单：源仓库相对路径 → 本仓库文件名
# 只同步本程序真正用到的内核文件；源仓库里供其路由器/编排器使用的 RouterConfig、
# ChildProcessRelay 在本程序内没有引用，因此不同步。
$coreFiles = @(
    @{ Source = 'orchestrator\TopazModelCatalog.cs';      Target = 'TopazModelCatalog.cs' },
    @{ Source = 'orchestrator\TvaiFilterComposer.cs';     Target = 'TvaiFilterComposer.cs' },
    @{ Source = 'orchestrator\AutoEstimator.cs';          Target = 'AutoEstimator.cs' },
    @{ Source = 'router\JobObject.cs';                    Target = 'JobObject.cs' },
    @{ Source = 'tools\VideoEnhancer.TvaiTuner\EncoderProfile.cs'; Target = 'EncoderProfile.cs' },
    @{ Source = 'tools\VideoEnhancer.TvaiTuner\TunerViewModel.cs'; Target = 'TunerViewModel.cs' },
    @{ Source = 'tools\VideoEnhancer.TvaiTuner\PresetWriter.cs';   Target = 'PresetWriter.cs' },
    @{ Source = 'tools\VideoEnhancer.TvaiTuner\PresetTemplate.json'; Target = 'PresetTemplate.json' }
)

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SourceCommit([string]$repo) {
    try {
        $commit = & git -C $repo rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { return $commit.Trim() }
    } catch { }
    return ''
}

$entries = @()
$mismatches = @()
$copied = 0

if (-not $Verify) {
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
}

foreach ($item in $coreFiles) {
    $sourcePath = Join-Path $SourceRepo $item.Source
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "源文件缺失：$sourcePath"
    }

    $sourceHash = Get-Sha256 $sourcePath
    $targetPath = Join-Path $targetDir $item.Target

    if ($Verify) {
        if (-not (Test-Path -LiteralPath $targetPath)) {
            $mismatches += "缺失：$($item.Target)"
            continue
        }

        $targetHash = Get-Sha256 $targetPath
        if ($targetHash -ne $sourceHash) {
            $mismatches += "内容不一致：$($item.Target)（本地 $($targetHash.Substring(0,12)) vs 源 $($sourceHash.Substring(0,12))）"
        }

        continue
    }

    Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    $copied++
    Write-Host ("已同步 {0,-26} <- {1}" -f $item.Target, $item.Source) -ForegroundColor Green
}

if ($Verify) {
    # 校验模式下还要确认清单本身没有记录过期哈希。
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        Write-Host "未找到 MANIFEST.json：$manifestPath" -ForegroundColor Red
        exit 1
    }

    if ($mismatches.Count -gt 0) {
        Write-Host "共享内核与源仓库不一致（共 $($mismatches.Count) 项）：" -ForegroundColor Red
        foreach ($m in $mismatches) { Write-Host "  $m" -ForegroundColor Red }
        Write-Host "请运行 tools/sync-tvai-core.ps1 重新同步。" -ForegroundColor Yellow
        exit 1
    }

    Write-Host "共享内核校验通过：$($coreFiles.Count) 个文件与源仓库一致。" -ForegroundColor Green
    exit 0
}

$commit = Get-SourceCommit $SourceRepo
foreach ($item in $coreFiles) {
    $targetPath = Join-Path $targetDir $item.Target
    $entries += [ordered]@{
        file           = $item.Target
        sourcePath     = ($item.Source -replace '\\', '/')
        sha256         = (Get-Sha256 $targetPath)
    }
}

$manifest = [ordered]@{
    # 清单保持纯 ASCII：PowerShell 5.1 的 Get-Content 对无 BOM 文件按 ANSI 解码，
    # 非 ASCII 内容会导致 ConvertFrom-Json 失败（CI 用 pwsh 7 则无此问题）。
    description = 'Shared tvai core synced from the VideoEnhancer repository (single implementation; do not edit in place)'
    sourceRepo  = ($SourceRepo -replace '\\', '/')
    sourceCommit = $commit
    syncedAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    fileCount   = $entries.Count
    files       = $entries
}

$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "已同步 $copied 个文件；清单：$manifestPath" -ForegroundColor Green
if ($commit) { Write-Host "源提交：$commit" -ForegroundColor DarkGray }
