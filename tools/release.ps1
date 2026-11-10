<#
.SYNOPSIS
    一次出齐四个发布形态，并生成可直接上传 GitHub Release 的资产。

.DESCRIPTION
    发版要交四个单文件 exe：
      · KeyboardEnhance.exe                                  Win10/11 x64（框架依赖，约 4 MB）
      · KeyboardEnhance-<版本>-x86-Win10-Win11.exe           Win10/11 x86（框架依赖）
      · KeyboardEnhance-<版本>-x64-Win7-SelfContained.exe    Win7 SP1 x64（自带运行时，约 140 MB）
      · KeyboardEnhance-<版本>-x86-Win7-SelfContained.exe    Win7 SP1 x86（自带运行时，约 130 MB）

    为什么必须用脚本而不是手敲 dotnet publish：
      · 四个形态的 RID / TFM 不同，**每个形态之间必须先清 obj 与 bin**，
        否则不同 RID 的中间产物混在一起会互相干扰（BUILD.md 里记过这个坑）；
      · 资产文件名、版本号、SHA-256、发布说明都要和 csproj / CHANGELOG 对齐，
        手工做迟早有一处对不上，用户下到错包很难第一时间发现。

    产物目录：dist/<版本>/
        KeyboardEnhance.exe
        KeyboardEnhance-<版本>-x86-Win10-Win11.exe
        KeyboardEnhance-<版本>-x64-Win7-SelfContained.exe
        KeyboardEnhance-<版本>-x86-Win7-SelfContained.exe
        SHA256SUMS.txt          每行 "哈希  文件名"，可直接校验
        release-notes.md        从 CHANGELOG.md 抽出的本版说明（GitHub Release 正文）
        manifest.json           版本 / 文件 / 大小 / 哈希 / 构建时间（脚本或 CI 自检用）

.PARAMETER Only
    只构建其中几个形态，例如 -Only win10-x64,win10-x86。
    不传则四个全做。

.PARAMETER OutRoot
    输出根目录，默认仓库下的 dist。

.PARAMETER SkipBuild
    不重新发布，只对 dist/<版本>/ 里已有的 exe 重算哈希与清单
    （用于上传前补哈希，或在 CI 里二次校验）。

.PARAMETER KeepStaging
    保留每个形态的临时发布目录（排查"exe 里缺文件"时有用）。

.EXAMPLE
    pwsh tools/release.ps1

.EXAMPLE
    pwsh tools/release.ps1 -Only win10-x64 -KeepStaging
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [string]$OutRoot,
    [switch]$SkipBuild,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $repo 'src\CapsLockPlus\CapsLockPlus.csproj'
if (-not (Test-Path -LiteralPath $csproj)) { throw "找不到工程文件：$csproj" }
if (-not $OutRoot) { $OutRoot = Join-Path $repo 'dist' }

function Get-ProjectVersion {
    [xml]$xml = Get-Content -LiteralPath $csproj -Encoding utf8
    # 不用 $pg.Version：StrictMode 下"没有该子元素的 PropertyGroup"会直接抛异常，
    # 而 csproj 里本来就有多个不带 Version 的 PropertyGroup。
    foreach ($pg in $xml.Project.PropertyGroup) {
        $node = $pg.SelectSingleNode('Version')
        if ($node -and $node.InnerText.Trim().Length -gt 0) { return $node.InnerText.Trim() }
    }
    throw 'csproj 里没有 <Version>，无法确定发版号'
}

$version = Get-ProjectVersion
$stageRoot = Join-Path $OutRoot ('_staging-' + $version)
$outDir = Join-Path $OutRoot $version
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# 四个形态。Asset 名要与 README 的下载表、UpdateService.PickAsset 的识别规则一致。
$allShapes = @(
    [pscustomobject]@{
        Id = 'win10-x64'; Tfm = 'net10.0-windows'; Rid = 'win-x64'; SelfContained = $false
        Asset = 'KeyboardEnhance.exe'; Note = 'Win10/11 x64（框架依赖，需 .NET 10 桌面运行时）'
    },
    [pscustomobject]@{
        Id = 'win10-x86'; Tfm = 'net10.0-windows'; Rid = 'win-x86'; SelfContained = $false
        Asset = "KeyboardEnhance-$version-x86-Win10-Win11.exe"; Note = 'Win10/11 x86（框架依赖，需 .NET 10 桌面运行时 x86）'
    },
    [pscustomobject]@{
        Id = 'win7-x64'; Tfm = 'net7.0-windows'; Rid = 'win-x64'; SelfContained = $true
        Asset = "KeyboardEnhance-$version-x64-Win7-SelfContained.exe"; Note = 'Win7 SP1 x64（自带运行时）'
    },
    [pscustomobject]@{
        Id = 'win7-x86'; Tfm = 'net7.0-windows'; Rid = 'win-x86'; SelfContained = $true
        Asset = "KeyboardEnhance-$version-x86-Win7-SelfContained.exe"; Note = 'Win7 SP1 x86（自带运行时）'
    }
)

$shapes = $allShapes
if ($Only -and $Only.Count -gt 0) {
    $shapes = @($allShapes | Where-Object { $Only -contains $_.Id })
    if ($shapes.Count -eq 0) {
        throw ("-Only 里的名字都不认识。可用：" + (($allShapes | ForEach-Object { $_.Id }) -join ', '))
    }
}

function Clear-Intermediates {
    # 四个形态之间必须清干净，否则 RID 不同的中间产物会互相污染（BUILD.md 记录过的坑）。
    foreach ($rel in @(
            'src\CapsLockPlus\obj',
            'src\CapsLockPlus\bin',
            'src\CapsLockPlus.Localization\obj',
            'src\CapsLockPlus.Localization\bin')) {
        $path = Join-Path $repo $rel
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$results = @()
$failed = @()

Write-Host ''
Write-Host "Keyboard Enhance $version — 发布四个形态" -ForegroundColor Cyan
Write-Host ''

foreach ($shape in $shapes) {
    $stage = Join-Path $stageRoot $shape.Id
    Write-Host ("▶ {0}  ({1} / {2}, 自包含={3})" -f $shape.Id, $shape.Tfm, $shape.Rid, $shape.SelfContained) -ForegroundColor Yellow

    if (-not $SkipBuild) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Clear-Intermediates

        $selfContained = if ($shape.SelfContained) { 'true' } else { 'false' }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & dotnet publish $csproj -c Release -f $shape.Tfm -r $shape.Rid `
            --self-contained $selfContained -o $stage -v:minimal
        $code = $LASTEXITCODE
        $sw.Stop()

        if ($code -ne 0) {
            Write-Host ("  ✗ 发布失败（退出码 {0}）" -f $code) -ForegroundColor Red
            $failed += $shape.Id
            continue
        }
        Write-Host ("  构建完成，用时 {0:0.0} 秒" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
    }

    $exe = Join-Path $stage 'KeyboardEnhance.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        if ($SkipBuild) {
            # -SkipBuild 时从成品目录里找对应资产
            $exe = Join-Path $outDir $shape.Asset
        }
        if (-not (Test-Path -LiteralPath $exe)) {
            Write-Host ("  ✗ 找不到产物：{0}" -f $exe) -ForegroundColor Red
            $failed += $shape.Id
            continue
        }
    }

    $target = Join-Path $outDir $shape.Asset
    if (-not [string]::Equals((Resolve-Path -LiteralPath $exe).Path, $target, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $exe -Destination $target -Force
    }

    $item = Get-Item -LiteralPath $target
    $fileVersion = $item.VersionInfo.FileVersion
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash

    if ($fileVersion -and -not $fileVersion.StartsWith($version, [StringComparison]::Ordinal)) {
        Write-Host ("  ⚠ 文件版本 {0} 与工程版本 {1} 不一致" -f $fileVersion, $version) -ForegroundColor Yellow
    }

    $results += [pscustomobject]@{
        id            = $shape.Id
        asset         = $shape.Asset
        note          = $shape.Note
        targetFramework = $shape.Tfm
        rid           = $shape.Rid
        selfContained = $shape.SelfContained
        sizeBytes     = $item.Length
        fileVersion   = $fileVersion
        sha256        = $hash
    }

    Write-Host ("  ✓ {0}  {1:0.0} MB  {2}" -f $shape.Asset, ($item.Length / 1MB), $hash.Substring(0, 16)) -ForegroundColor Green
}

if (-not $KeepStaging -and (Test-Path -LiteralPath $stageRoot)) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 清单按**成品目录里真实存在的文件**生成 ----
#
# 不能用"本次构建了哪些"：`-Only win10-x64` 或 `-SkipBuild` 是部分运行，
# 拿本次结果写清单会把另外三个形态从 manifest / SHA256SUMS 里抹掉，
# 上传时就少三个资产。这里统一扫描四个已知资产名，谁在就登记谁。
$manifestShapes = @()
foreach ($shape in $allShapes) {
    $path = Join-Path $outDir $shape.Asset
    if (-not (Test-Path -LiteralPath $path)) { continue }

    $item = Get-Item -LiteralPath $path
    $manifestShapes += [pscustomobject]@{
        id              = $shape.Id
        asset           = $shape.Asset
        note            = $shape.Note
        targetFramework = $shape.Tfm
        rid             = $shape.Rid
        selfContained   = $shape.SelfContained
        sizeBytes       = $item.Length
        fileVersion     = $item.VersionInfo.FileVersion
        sha256          = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

# ---- 哈希清单：每行 "哈希 文件名"，Windows 的 certutil 与大多校验工具都认 ----
$sumsPath = Join-Path $outDir 'SHA256SUMS.txt'
$sumLines = $manifestShapes | Sort-Object asset | ForEach-Object { '{0}  {1}' -f $_.sha256, $_.asset }
[System.IO.File]::WriteAllLines($sumsPath, $sumLines, (New-Object System.Text.UTF8Encoding($false)))

# ---- manifest.json：CI / 上传脚本据此核对，不用再数文件 ----
$manifest = [pscustomobject]@{
    product    = 'Keyboard Enhance'
    version    = $version
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    shapes     = @($manifestShapes)
    missing    = @($allShapes | Where-Object { $id = $_.Id; -not ($manifestShapes | Where-Object { $_.id -eq $id }) } |
        ForEach-Object { $_.Id })
    failed     = @($failed)
}
$manifestPath = Join-Path $outDir 'manifest.json'
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 6),
    (New-Object System.Text.UTF8Encoding($false)))

# ---- 发布说明：从 CHANGELOG.md 里抽 "## v<版本>" 到下一个 "## v" 之前 ----
$changelog = Join-Path $repo 'CHANGELOG.md'
$notesPath = Join-Path $outDir 'release-notes.md'
if (Test-Path -LiteralPath $changelog) {
    $lines = [System.IO.File]::ReadAllLines($changelog, [System.Text.Encoding]::UTF8)
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^##\s+v?' + [regex]::Escape($version) + '(\s|—|-|$)')) { $start = $i; break }
    }
    if ($start -ge 0) {
        $end = $lines.Count
        for ($j = $start + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j] -match '^##\s+v?\d') { $end = $j; break }
        }
        $body = $lines[$start..($end - 1)] -join "`r`n"
        [System.IO.File]::WriteAllText($notesPath, $body.TrimEnd() + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ''
        Write-Host ("发布说明已生成：{0}" -f $notesPath) -ForegroundColor DarkGray
    }
    else {
        Write-Host ("⚠ CHANGELOG.md 里没有 v{0} 的小节，未生成发布说明" -f $version) -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host ("产物目录：{0}" -f $outDir) -ForegroundColor Cyan
$manifestShapes | Sort-Object asset | Format-Table -AutoSize `
    @{n = '文件'; e = { $_.asset } },
    @{n = '大小(MB)'; e = { [math]::Round($_.sizeBytes / 1MB, 1) } },
    @{n = '版本'; e = { $_.fileVersion } } | Out-String | Write-Host

if ($manifest.missing.Count -gt 0) {
    Write-Host ("⚠ 还没出的形态：{0}" -f ($manifest.missing -join ', ')) -ForegroundColor Yellow
}

if ($failed.Count -gt 0) {
    Write-Host ("✗ 有形态构建失败：{0}" -f ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

if ($manifestShapes.Count -lt $allShapes.Count) {
    Write-Host ("本次只覆盖了 {0}/{1} 个形态（部分构建）" -f $manifestShapes.Count, $allShapes.Count) -ForegroundColor Yellow
    exit 0
}

Write-Host '✓ 全部完成' -ForegroundColor Green
exit 0
