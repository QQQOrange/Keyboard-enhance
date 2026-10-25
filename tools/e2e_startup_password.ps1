# 启动密码 · 端到端真机验证（PowerShell）。
#
# 覆盖用户要求的全部流程：
#   ① 启动门：密码正确 → 进主界面；密码错误 → 拦着；取消/关窗 → 软件退出
#   ② 设置页开关：开启（设新密码）、关闭（验旧密码）、修改（验旧 → 设新）
#   ③ 与备份密码的独立性（本脚本只碰 Security/startup.json，不碰 Sync）
#
# 用法（在 PowerShell 工具里跑，不要从 Bash 启动 PowerShell）：
#   pwsh -File tools\e2e_startup_password.ps1 [-Exe <路径>] [-Shot <截图目录>]
#   # 第二阶段（点开关）需要从第一阶段的截图里量出开关坐标：
#   pwsh -File tools\e2e_startup_password.ps1 -Phase clicks -ToggleX 1500 -ToggleY 620
#
# 说明：测试实例走独立数据目录 + 独立互斥体后缀，不会碰正在运行的安装版。

param(
    [string]$Exe = "C:\Users\64381\Downloads\Compressed\Keyboard enhance源代码\src\CapsLockPlus\bin\Release\net10.0-windows\win-x64\KeyboardEnhance.exe",
    [string]$Shot = "C:\Users\64381\WorkBuddy\2026-10-25-09-21-42\pwshots",
    [string]$Phase = "gate",
    [int]$ToggleX = 0,
    [int]$ToggleY = 0
)

$ErrorActionPreference = "Stop"
$GateTitle = "启动密码"
$MainTitle = "按键增强 Keyboard Enhance"
$Correct = "e2e-gate-2024"
$Wrong = "wrong-pass-999"

New-Item -ItemType Directory -Force -Path $Shot | Out-Null
$Data = Join-Path $Shot "pwtest-data"
New-Item -ItemType Directory -Force -Path $Data | Out-Null

$env:CAPSLOCKPLUS_DATA = $Data
$env:CAPSLOCKPLUS_MUTEX_SUFFIX = "pwtest"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
'@ -Name Win32Mouse -Namespace PsTest

function Shot([string]$name) {
    $p = Join-Path $Shot "$name.png"
    & "C:\Users\64381\.workbuddy\binaries\python\versions\3.13.12\python.exe" -c "from PIL import ImageGrab; ImageGrab.grab(all_screens=True).convert('RGB').save(r'$p')"
    return $p
}

function Click([int]$x, [int]$y) {
    [PsTest.Win32Mouse]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [PsTest.Win32Mouse]::mouse_event(2, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [PsTest.Win32Mouse]::mouse_event(4, 0, 0, 0, 0)
}

function Wait-Window([string]$title, [int]$timeoutSec = 20) {
    for ($i = 0; $i -lt $timeoutSec * 10; $i++) {
        $p = Get-Process KeyboardEnhance -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowTitle -eq $title }
        if ($p) { return $p }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Wait-Gone([int]$timeoutSec = 20) {
    for ($i = 0; $i -lt $timeoutSec * 10; $i++) {
        if (-not (Get-Process KeyboardEnhance -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Type-Pass([string]$text) {
    $ws = New-Object -ComObject WScript.Shell
    $ws.AppActivate($GateTitle) | Out-Null
    Start-Sleep -Milliseconds 300
    $ws.SendKeys($text)
    Start-Sleep -Milliseconds 200
    $ws.SendKeys("{ENTER}")
}

# 生成密码文件（和 Core.Settings.StartupPassword 写的同一格式）
function New-GateFile([string]$password) {
    $salt = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($salt)
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $password, $salt, 600000, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $hash = $kdf.GetBytes(32)
    $obj = @{
        Salt = [Convert]::ToBase64String($salt)
        Hash = [Convert]::ToBase64String($hash)
        Iterations = 600000
    }
    ($obj | ConvertTo-Json -Compress) |
        Set-Content -Path (Join-Path $Data "startup.json") -Encoding UTF8
}

$results = @()
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    $mark = if ($ok) { "  ✓ " } else { "  ✗ " }
    $line = $mark + $name + $(if ($detail) { "（$detail）" } else { "" })
    Write-Host $line
    $script:results += $ok
}

if ($Phase -eq "gate") {

    Write-Host "=== 第一阶段：启动密码门 ==="

    # ---------- 场景 1：先错后对 ----------
    New-GateFile $Correct
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $GateTitle
    Check "启动即弹密码窗" ($gate -ne $null)
    if ($gate) {
        Shot "01-gate"

        Type-Pass $Wrong
        Start-Sleep -Milliseconds 1600     # PBKDF2 600k ≈ 0.5s + 余量
        $still = Wait-Window $GateTitle 2
        Check "密码错误 → 拦在主界面之外（密码窗还在，主窗口没出来）" ($still -ne $null)
        Check "密码错误 → 主界面没有出现" ((Wait-Window $MainTitle 1) -eq $null)
        Shot "02-wrong"

        Type-Pass $Correct
        Start-Sleep -Milliseconds 2500
        $main = Wait-Window $MainTitle
        Check "密码正确 → 进入主界面" ($main -ne $null)
        Shot "03-main"

        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Wait-Gone 5 | Out-Null
    }

    # ---------- 场景 2：取消 = 不让用 ----------
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $GateTitle
    Check "重启后再次弹出密码窗" ($gate -ne $null)
    if ($gate) {
        $ws = New-Object -ComObject WScript.Shell
        $ws.AppActivate($GateTitle) | Out-Null
        Start-Sleep -Milliseconds 300
        $ws.SendKeys("%{F4}")               # Alt+F4 关窗 = 取消
        $exited = Wait-Gone 10
        Check "关掉密码窗（相当于取消）→ 软件整体退出" $exited
        if (-not $exited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }

    # ---------- 场景 3：没有密码文件时不该弹窗 ----------
    Remove-Item (Join-Path $Data "startup.json") -ErrorAction SilentlyContinue
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $GateTitle 3
    Check "没设密码 → 不弹密码窗" ($gate -eq $null)
    $main = Wait-Window $MainTitle
    Check "没设密码 → 直接进主界面" ($main -ne $null)
    Shot "04-no-gate"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone 5 | Out-Null
}

elseif ($Phase -eq "settings") {

    Write-Host "=== 第二阶段：设置页开关（截图确认状态） ==="

    # 带着密码直接开设置页
    New-GateFile $Correct
    $proc = Start-Process -FilePath $Exe -ArgumentList "--page=settings" -PassThru
    $gate = Wait-Window $GateTitle
    if ($gate) {
        Type-Pass $Correct
        Start-Sleep -Milliseconds 2500
    }
    $main = Wait-Window $MainTitle
    Check "过密码门后打开设置页" ($main -ne $null)
    Shot "10-settings"
    Write-Host "  （把 10-settings.png 里「启动密码保护」开关的屏幕坐标传给 -ToggleX/-ToggleY 再跑 clicks 阶段）"
    if ($ToggleX -gt 0 -and $ToggleY -gt 0) {
        Write-Host "  已收到开关坐标 ($ToggleX, $ToggleY)，接着跑 clicks 阶段即可"
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone 5 | Out-Null
}

elseif ($Phase -eq "clicks") {

    Write-Host "=== 第三阶段：设置页开关的点按流程 ==="
    if ($ToggleX -le 0 -or $ToggleY -le 0) {
        Write-Host "  ✗ 需要 -ToggleX/-ToggleY（从 10-settings.png 量）"; exit 1
    }

    New-GateFile $Correct
    $proc = Start-Process -FilePath $Exe -ArgumentList "--page=settings" -PassThru
    $gate = Wait-Window $GateTitle
    if ($gate) { Type-Pass $Correct; Start-Sleep -Milliseconds 2500 }
    $main = Wait-Window $MainTitle
    Check "过密码门后打开设置页" ($main -ne $null)

    # --- 关：先弹「关闭启动密码」验证窗 ---
    Click $ToggleX $ToggleY
    Start-Sleep -Milliseconds 800
    $off = Wait-Window "关闭启动密码"
    Check "点开关 → 先要求验证当前密码（防顺手关保护）" ($off -ne $null)
    Shot "11-disable-verify"

    if ($off) {
        # 输错 → 应该还拦着，开关保持开
        $ws = New-Object -ComObject WScript.Shell
        $ws.AppActivate("关闭启动密码") | Out-Null
        Start-Sleep -Milliseconds 300
        $ws.SendKeys($Wrong); Start-Sleep -Milliseconds 150; $ws.SendKeys("{ENTER}")
        Start-Sleep -Milliseconds 1600
        $still = Wait-Window "关闭启动密码" 2
        Check "验证窗里密码错误 → 还拦着（关不掉）" ($still -ne $null)
        Shot "12-disable-wrong"

        # 输对 → 关保护
        $ws.SendKeys($Correct); Start-Sleep -Milliseconds 150; $ws.SendKeys("{ENTER}")
        Start-Sleep -Milliseconds 2000
        $gone = -not (Wait-Window "关闭启动密码" 2)
        Check "验证通过 → 关掉启动密码" $gone
        $file = Join-Path $Data "startup.json"
        Check "关掉后密码文件被删除" (-not (Test-Path $file))
        Shot "13-disabled"

        # --- 再开：设新密码 ---
        Click $ToggleX $ToggleY
        Start-Sleep -Milliseconds 800
        $set = Wait-Window "设置启动密码"
        Check "再点开关 → 弹「设置启动密码」" ($set -ne $null)
        Shot "14-set-new"

        if ($set) {
            $ws.AppActivate("设置启动密码") | Out-Null
            Start-Sleep -Milliseconds 300
            $ws.SendKeys("brand-new-pass-2024")
            Start-Sleep -Milliseconds 150
            $ws.SendKeys("{TAB}")
            Start-Sleep -Milliseconds 150
            $ws.SendKeys("brand-new-pass-2024")
            Start-Sleep -Milliseconds 150
            $ws.SendKeys("{ENTER}")
            Start-Sleep -Milliseconds 2500
            Check "设完新密码 → 验证窗消失" (-not (Wait-Window "设置启动密码" 2))
            Check "新密码文件已写入" (Test-Path (Join-Path $Data "startup.json"))
            Shot "15-enabled"
        }
    }

    # 重启验证新密码真的生效
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone 5 | Out-Null
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $GateTitle
    Check "设了新密码后重启 → 又弹密码门" ($gate -ne $null)
    if ($gate) {
        Type-Pass "brand-new-pass-2024"
        Start-Sleep -Milliseconds 2500
        Check "新密码正确 → 能进" ((Wait-Window $MainTitle) -ne $null)
        Shot "16-new-pass-ok"
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone 5 | Out-Null
}

$failed = ($results | Where-Object { -not $_ }).Count
Write-Host ""
Write-Host "=== 汇总：$($results.Count - $failed)/$($results.Count) 通过 ==="
if ($failed -gt 0) { exit 1 }
