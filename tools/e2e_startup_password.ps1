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

# ---- 测试环境降噪：关掉托盘图标 ----
# 托盘图标每次启动都会弹一个「按键增强 已启动」的气泡提示（NotifyIcon.
# ShowBalloonTip）。.NET 传的 1800ms 超时在现代 Windows 上会被忽略，
# 实测逗留过半分钟，正好盖在设置页开关上，点开关全打它在身上。
# ShowTrayIcon=false 是应用原生设置：没有托盘图标就不建、也就不弹气泡，
# 对被测功能（密码门/开关流程）毫无影响。只改测试数据目录，不碰产品代码。
$settingsPath = Join-Path $Data "settings.json"
if (Test-Path $settingsPath) {
    $raw = Get-Content $settingsPath -Raw -Encoding UTF8
    $patched = $raw -replace '"ShowTrayIcon"\s*:\s*true', '"ShowTrayIcon": false'
    if ($patched -ne $raw) {
        Set-Content -Path $settingsPath -Value $patched -Encoding UTF8 -NoNewline
        Write-Host "  测试数据目录已关闭托盘图标（不再弹启动气泡）"
    }
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);
'@ -Name Win32Mouse -Namespace PsTest

# 屏幕取色：点开关之前先确认目标像素确实是那颗蓝色开关丸。
# 血的教训：桌面待办应用的提醒弹窗（周期弹出、无固定规律）曾正好盖在
# 开关上，鼠标点全打弹窗上，开关纹丝不动，测试还误以为"点了没反应"。
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
[DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
'@ -Name Win32Pix -Namespace PsTest

# 进程窗口枚举。为什么不用 Process.MainWindowTitle：本程序的启动密码窗是
# WPF 窗，实测 MainWindowTitle 恒为空（主线程首个顶层窗是 WPF 的隐藏
# 基础设施窗，.NET 的主窗口解析直接失败），EnumWindows 才看得见。
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
'@ -Name Win32Enum -Namespace PsTest

# 压住空闲计数，防止端到端跑到一半机器锁屏（锁屏后用户桌面隐藏，截图全是锁屏）
# ES_CONTINUOUS|ES_DISPLAY_REQUIRED|ES_SYSTEM_REQUIRED = 2147483651。
# 十进制字面量：5.1 的 -bor 会产出有符号 Int32，直接传给 uint 形参会报错。
[PsTest.Win32Mouse]::SetThreadExecutionState(2147483651) | Out-Null

# ---- 进程 DPI 感知化（点开关的前提） ----------------------------------
# 本机 200% 缩放，powershell.exe 默认 DPI-unaware：SetCursorPos 收到的
# 逻辑坐标会被系统 ×2 虚拟化成物理坐标（实测传 (1261,574) → 光标落在
# 物理 (2522,1148)）。第三阶段第一次跑就这么栽过：按截图量出的物理坐标
# (2522,1148) 传进去，实际落到 (5044,2296)，被钳到屏幕右下角，误触了
# 「显示桌面」，所有窗口瞬间最小化 —— 开关根本没被点到，设置窗口整个消失。
# 解决：把本进程提成 per-monitor 感知，之后传入坐标即物理像素，与截图
# （ImageGrab 抓的是物理像素）1:1 对应。提不上去则按系统 DPI 折算。
Add-Type -MemberDefinition @'
[DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
[DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);
'@ -Name Win32Dpi -Namespace PsTest

$hrDpi = [PsTest.Win32Dpi]::SetProcessDpiAwareness(2)   # 2 = PER_MONITOR_AWARE
$aware = -1
[PsTest.Win32Dpi]::GetProcessDpiAwareness([System.Diagnostics.Process]::GetCurrentProcess().Handle, [ref]$aware) | Out-Null
$script:DpiScale = 1.0
if ($hrDpi -ne 0 -or $aware -ne 2) {
    Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetDpiForSystem();' -Name Win32DpiSys -Namespace PsTest
    $sysDpi = [PsTest.Win32DpiSys]::GetDpiForSystem()
    $script:DpiScale = $sysDpi / 96.0
    Write-Host "  （注意）进程未提成 per-monitor 感知（hr=0x$($hrDpi.ToString('X8'))，awareness=$aware），点击坐标按 ÷$($script:DpiScale) 折算"
}
else {
    Write-Host "  进程 DPI 感知：per-monitor，点击坐标=物理像素（与截图 1:1）"
}

function Shot([string]$name) {
    $p = Join-Path $Shot "$name.png"
    & "C:\Users\64381\.workbuddy\binaries\python\versions\3.13.12\python.exe" -c "from PIL import ImageGrab; ImageGrab.grab(all_screens=True).convert('RGB').save(r'$p')"
    return $p
}

function Click([int]$x, [int]$y) {
    # 坐标按 DPI 折算：进程 per-monitor 感知时 ÷1 直通；否则把截图量出的
    # 物理坐标换算成逻辑坐标，免得被系统虚拟化点飞
    $cx = [int][math]::Round($x / $script:DpiScale)
    $cy = [int][math]::Round($y / $script:DpiScale)
    [PsTest.Win32Mouse]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 120
    [PsTest.Win32Mouse]::mouse_event(2, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [PsTest.Win32Mouse]::mouse_event(4, 0, 0, 0, 0)
    Write-Host "    点击 ($x,$y) → 进程坐标 ($cx,$cy)"
}

# 屏幕合成后的像素颜色（返回 [r,g,b]，物理坐标）。
# GetDC(NULL) 拿到的是合成屏，盖在上面的窗口是什么色就读到什么色。
function Get-ScreenPixel([int]$x, [int]$y) {
    $hdc = [PsTest.Win32Pix]::GetDC([IntPtr]::Zero)
    try {
        $c = [PsTest.Win32Pix]::GetPixel($hdc, $x, $y)
        return @(($c -band 0xFF), (($c -shr 8) -band 0xFF), (($c -shr 16) -band 0xFF))
    }
    finally {
        [PsTest.Win32Pix]::ReleaseDC([IntPtr]::Zero, $hdc) | Out-Null
    }
}

# 目标点此刻是不是一颗开启态的蓝色开关丸（与设置页开关同色的判定）。
function Test-BlueToggle([int]$x, [int]$y) {
    $r, $g, $b = Get-ScreenPixel $x $y
    return ($b -gt 150 -and ($b - $r) -gt 60 -and ($b - $g) -gt 40 -and $g -gt 80)
}

# 开关丸露着没：开启=蓝丸（91,147,255），关闭=深灰丸（57,65,84）。
# 被浅色弹窗盖住时两种都不是 —— 以此识别遮挡。
function Test-PillExposed([int]$x, [int]$y) {
    $r, $g, $b = Get-ScreenPixel $x $y
    $on  = ($b -gt 150 -and ($b - $r) -gt 60 -and ($b - $g) -gt 40 -and $g -gt 80)
    $off = ($r -lt 110 -and $g -lt 110 -and $b -lt 140 -and ($b - $r) -lt 60)
    return ($on -or $off)
}

# 等开关露出来：桌面提醒弹窗盖住它就等等（弹窗一般会自己消失）。
# 等到真蓝丸才允许点，绝不盲点。超时给到 90s：第三方提醒弹窗
# （滴答清单之类）带入场动画，停留时间从十几秒到一分多钟不等。
function Wait-ToggleVisible([int]$x, [int]$y, [int]$timeoutSec = 90) {
    for ($i = 0; $i -lt $timeoutSec * 2; $i++) {
        if (Test-BlueToggle $x $y) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# 等指定进程出现一个可见的、标题正好相符的顶层窗口。
# 按 PID 匹配而不是只按标题：安装版也可能开着同名主窗（隐藏，IsWindowVisible
# 会排掉；万一可见，PID 匹配也不会误判）。
function Test-Window([int]$procId, [string]$title) {
    $script:hit = $false
    $cb = [PsTest.Win32Enum+EnumWindowsProc] { param($h, $l)
        $pid2 = 0
        [PsTest.Win32Enum]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
        if ($pid2 -eq $procId) {
            $sb = New-Object System.Text.StringBuilder 256
            [PsTest.Win32Enum]::GetWindowTextW($h, $sb, 256) | Out-Null
            if ($sb.ToString() -eq $title -and [PsTest.Win32Enum]::IsWindowVisible($h)) {
                $script:hit = $true
                return $false
            }
        }
        return $true
    }
    [PsTest.Win32Enum]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return $script:hit
}

function Wait-Window([int]$procId, [string]$title, [int]$timeoutSec = 20) {
    for ($i = 0; $i -lt $timeoutSec * 10; $i++) {
        if (Test-Window $procId $title) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

# 按 PID 等进程消失（不能按进程名：安装版可能也在跑，会把等待拖满超时）
function Wait-Gone([int]$procId, [int]$timeoutSec = 20) {
    for ($i = 0; $i -lt $timeoutSec * 10; $i++) {
        if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { return $true }
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
    $gate = Wait-Window $proc.Id $GateTitle
    Check "启动即弹密码窗" $gate
    if ($gate) {
        Shot "01-gate"

        Type-Pass $Wrong
        Start-Sleep -Milliseconds 1600     # PBKDF2 600k ≈ 0.5s + 余量
        $still = Wait-Window $proc.Id $GateTitle 2
        Check "密码错误 → 拦在主界面之外（密码窗还在，主窗口没出来）" $still
        Check "密码错误 → 主界面没有出现" (-not (Wait-Window $proc.Id $MainTitle 1))
        Shot "02-wrong"

        Type-Pass $Correct
        Start-Sleep -Milliseconds 2500
        $main = Wait-Window $proc.Id $MainTitle
        Check "密码正确 → 进入主界面" $main
        Shot "03-main"

        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Wait-Gone $proc.Id 5 | Out-Null
    }

    # ---------- 场景 2：取消 = 不让用 ----------
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $proc.Id $GateTitle
    Check "重启后再次弹出密码窗" $gate
    if ($gate) {
        $ws = New-Object -ComObject WScript.Shell
        $ws.AppActivate($GateTitle) | Out-Null
        Start-Sleep -Milliseconds 300
        $ws.SendKeys("%{F4}")               # Alt+F4 关窗 = 取消
        $exited = Wait-Gone $proc.Id 10
        Check "关掉密码窗（相当于取消）→ 软件整体退出" $exited
        if (-not $exited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }

    # ---------- 场景 3：没有密码文件时不该弹窗 ----------
    Remove-Item (Join-Path $Data "startup.json") -ErrorAction SilentlyContinue
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $proc.Id $GateTitle 3
    Check "没设密码 → 不弹密码窗" (-not $gate)
    $main = Wait-Window $proc.Id $MainTitle
    Check "没设密码 → 直接进主界面" $main
    Shot "04-no-gate"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone $proc.Id 5 | Out-Null
}

elseif ($Phase -eq "settings") {

    Write-Host "=== 第二阶段：设置页开关（截图确认状态） ==="

    # 带着密码直接开设置页
    New-GateFile $Correct
    $proc = Start-Process -FilePath $Exe -ArgumentList "--page=settings" -PassThru
    $gate = Wait-Window $proc.Id $GateTitle
    if ($gate) {
        Type-Pass $Correct
        Start-Sleep -Milliseconds 2500
    }
    $main = Wait-Window $proc.Id $MainTitle
    Check "过密码门后打开设置页" $main
    Shot "10-settings"
    Write-Host "  （把 10-settings.png 里「启动密码保护」开关的屏幕坐标传给 -ToggleX/-ToggleY 再跑 clicks 阶段）"
    if ($ToggleX -gt 0 -and $ToggleY -gt 0) {
        Write-Host "  已收到开关坐标 ($ToggleX, $ToggleY)，接着跑 clicks 阶段即可"
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone $proc.Id 5 | Out-Null
}

elseif ($Phase -eq "clicks") {

    Write-Host "=== 第三阶段：设置页开关的点按流程 ==="
    if ($ToggleX -le 0 -or $ToggleY -le 0) {
        Write-Host "  ✗ 需要 -ToggleX/-ToggleY（从 10-settings.png 量）"; exit 1
    }

    New-GateFile $Correct
    $proc = Start-Process -FilePath $Exe -ArgumentList "--page=settings" -PassThru
    $gate = Wait-Window $proc.Id $GateTitle
    if ($gate) { Type-Pass $Correct; Start-Sleep -Milliseconds 2500 }
    $main = Wait-Window $proc.Id $MainTitle
    Check "过密码门后打开设置页" $main

    # --- 关：先弹「关闭启动密码」验证窗 ---
    # 先确认开关露着且是开启态（蓝丸）。桌面待办提醒弹窗曾正好盖住开关，
    # 鼠标点全打弹窗上，开关纹丝不动 —— 所以点之前必须先验像素，绝不盲点。
    $vis = Wait-ToggleVisible $ToggleX $ToggleY
    if (-not $vis) {
        $pr, $pg, $pb = Get-ScreenPixel $ToggleX $ToggleY
        Check "开关可见且为开启态（无弹窗遮挡）" $false "目标像素=($pr,$pg,$pb)，疑似被弹窗盖住"
    } else {
        Check "开关可见且为开启态（无弹窗遮挡）" $true
    }
    if ($vis) { Click $ToggleX $ToggleY }
    Start-Sleep -Milliseconds 800
    $off = Wait-Window $proc.Id "关闭启动密码"
    Check "点开关 → 先要求验证当前密码（防顺手关保护）" $off
    Shot "11-disable-verify"

    if ($off) {
        # 输错 → 应该还拦着，开关保持开
        $ws = New-Object -ComObject WScript.Shell
        $ws.AppActivate("关闭启动密码") | Out-Null
        Start-Sleep -Milliseconds 300
        $ws.SendKeys($Wrong); Start-Sleep -Milliseconds 150; $ws.SendKeys("{ENTER}")
        Start-Sleep -Milliseconds 1600
        $still = Wait-Window $proc.Id "关闭启动密码" 2
        Check "验证窗里密码错误 → 还拦着（关不掉）" $still
        Shot "12-disable-wrong"

        # 输对 → 关保护
        $ws.SendKeys($Correct); Start-Sleep -Milliseconds 150; $ws.SendKeys("{ENTER}")
        Start-Sleep -Milliseconds 2000
        $gone = -not (Wait-Window $proc.Id "关闭启动密码" 2)
        Check "验证通过 → 关掉启动密码" $gone
        $file = Join-Path $Data "startup.json"
        Check "关掉后密码文件被删除" (-not (Test-Path $file))
        Shot "13-disabled"

        # --- 再开：设新密码 ---（此时开关应为关闭态深灰丸；同样先确认没被盖住）
        $vis2 = $false
        for ($i = 0; $i -lt 60; $i++) {
            if (Test-PillExposed $ToggleX $ToggleY) { $vis2 = $true; break }
            Start-Sleep -Milliseconds 500
        }
        Check "开关可见（关闭态，无弹窗遮挡）" $vis2
        if ($vis2) { Click $ToggleX $ToggleY }
        Start-Sleep -Milliseconds 800
        $set = Wait-Window $proc.Id "设置启动密码"
        Check "再点开关 → 弹「设置启动密码」" $set
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
            Check "设完新密码 → 验证窗消失" (-not (Wait-Window $proc.Id "设置启动密码" 2))
            Check "新密码文件已写入" (Test-Path (Join-Path $Data "startup.json"))
            Shot "15-enabled"
        }
    }

    # 重启验证新密码真的生效
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone $proc.Id 5 | Out-Null
    $proc = Start-Process -FilePath $Exe -PassThru
    $gate = Wait-Window $proc.Id $GateTitle
    Check "设了新密码后重启 → 又弹密码门" $gate
    if ($gate) {
        Type-Pass "brand-new-pass-2024"
        Start-Sleep -Milliseconds 2500
        Check "新密码正确 → 能进" (Wait-Window $proc.Id $MainTitle)
        Shot "16-new-pass-ok"
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Wait-Gone $proc.Id 5 | Out-Null
}

$failed = ($results | Where-Object { -not $_ }).Count
Write-Host ""
Write-Host "=== 汇总：$($results.Count - $failed)/$($results.Count) 通过 ==="
if ($failed -gt 0) { exit 1 }
