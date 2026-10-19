param(
    [string]$Exe = "",
    [string]$OutDir = "shots",
    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path $root "src\CapsLockPlus\bin\Release\net10.0-windows\win-x64\publish\CapsLockPlus.exe"
    if (-not (Test-Path $Exe)) {
        $Exe = Join-Path $root "src\CapsLockPlus\bin\Debug\net10.0-windows\CapsLockPlus.exe"
    }
}
if ([string]::IsNullOrEmpty($DataDir)) { $DataDir = Join-Path $env:TEMP "clp_shots" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$env:CAPSLOCKPLUS_DATA = $DataDir

$capture = Join-Path $root "tools\capture-window.ps1"
$pages = @(
    @{ name = "01-guide"; arg = "--page=guide" },
    @{ name = "02-keys"; arg = "--page=keys" },
    @{ name = "03-qbar"; arg = "--page=qbar" },
    @{ name = "04-bindings"; arg = "--page=bindings" },
    @{ name = "05-clipboard"; arg = "--page=clipboard" },
    @{ name = "06-translate"; arg = "--page=translate" },
    @{ name = "07-settings"; arg = "--page=settings" },
    @{ name = "08-help"; arg = "--page=help" }
)

foreach ($page in $pages) {
    Get-Process CapsLockPlus -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = "$($page.arg) --noelevate"
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 350
        $proc.Refresh()
        if ($proc.HasExited) { break }
        if ($proc.MainWindowHandle -ne 0) { break }
    }
    $out = Join-Path $OutDir "$($page.name).png"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -TitleLike "CapsLock+" -Out $out -DelayMs 900
}

Get-Process CapsLockPlus -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Output "DONE"
