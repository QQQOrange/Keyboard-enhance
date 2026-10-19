param(
    [string]$Exe = "",
    [int]$Seconds = 8
)

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path $root "src\CapsLockPlus\bin\Release\net10.0-windows\win-x64\publish\CapsLockPlus.exe"
    if (-not (Test-Path $Exe)) { $Exe = Join-Path $root "src\CapsLockPlus\bin\Debug\net10.0-windows\CapsLockPlus.exe" }
}
$env:CAPSLOCKPLUS_DATA = Join-Path $env:TEMP "clp_bench"

Get-Process CapsLockPlus -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = "--page=guide --noelevate"
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)

while ($sw.ElapsedMilliseconds -lt 20000) {
    Start-Sleep -Milliseconds 50
    $proc.Refresh()
    if ($proc.HasExited) { break }
    if ($proc.MainWindowHandle -ne 0) { break }
}
$firstWindow = $sw.ElapsedMilliseconds

Start-Sleep -Milliseconds 1200
$proc.Refresh()
$ws1 = [math]::Round($proc.WorkingSet64 / 1MB, 1)
$priv1 = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)

Start-Sleep -Seconds $Seconds
$proc.Refresh()
$ws2 = [math]::Round($proc.WorkingSet64 / 1MB, 1)
$cpu = [math]::Round($proc.TotalProcessorTime.TotalMilliseconds, 0)

$cpuBefore = $proc.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Seconds 5
$proc.Refresh()
$idleCpu = [math]::Round($proc.TotalProcessorTime.TotalMilliseconds - $cpuBefore, 0)

Write-Output "exe           : $Exe"
Write-Output "first window  : $firstWindow ms"
Write-Output "working set   : $ws1 MB -> $ws2 MB"
Write-Output "private bytes : $priv1 MB"
Write-Output "total CPU     : $cpu ms"
Write-Output "idle CPU 5s   : $idleCpu ms"

Get-Process CapsLockPlus -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
