param(
    [string]$TitleLike = "CapsLock+",
    [string]$Out = "shot.png",
    [int]$DelayMs = 800
)

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class WinCap
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public static void EnableDpi()
    {
        try { SetProcessDpiAwareness(2); } catch (Exception) { }
        try { SetProcessDPIAware(); } catch (Exception) { }
    }

    public static List<IntPtr> Find(string titleLike)
    {
        List<IntPtr> found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.Length > 0 && sb.ToString().Contains(titleLike)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool Focus(IntPtr h)
    {
        ShowWindow(h, 9);
        uint pid;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        uint cur = GetCurrentThreadId();
        if (fg != cur) AttachThreadInput(cur, fg, true);
        bool ok = SetForegroundWindow(h);
        BringWindowToTop(h);
        if (fg != cur) AttachThreadInput(cur, fg, false);
        return ok;
    }
}
'@

Add-Type -TypeDefinition $src -ErrorAction Stop
[WinCap]::EnableDpi()

$handles = [WinCap]::Find($TitleLike)
if ($handles.Count -eq 0) { Write-Output "NOT_FOUND"; exit 2 }
$h = $handles[0]

# 已经在前台就不要抢焦点：Qbar 这类面板失焦会自动淡出，抢焦点会截到半透明画面。
if ([WinCap]::GetForegroundWindow() -ne $h) {
    [WinCap]::Focus($h) | Out-Null
}
Start-Sleep -Milliseconds $DelayMs

$r = New-Object WinCap+RECT
[WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L
$ht = $r.B - $r.T
if ($w -le 0 -or $ht -le 0) { Write-Output "BAD_RECT"; exit 3 }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $ht))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "OK $w x $ht -> $Out"
