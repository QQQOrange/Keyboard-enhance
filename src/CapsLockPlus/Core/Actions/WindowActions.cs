using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

public sealed class WindowActions
{
    private IntPtr Target => WindowService.Foreground;

    public bool ToggleTopmost()
    {
        WindowService.ToggleTopmost(Target);
        return true;
    }

    public bool CycleOpacity()
    {
        byte a = WindowService.NextOpacity(Target);
        return true;
    }

    public bool Minimize() { WindowService.Minimize(Target); return true; }
    public bool ToggleMaximize() { WindowService.ToggleMaximize(Target); return true; }
    public bool Close() { WindowService.Close(Target); return true; }
    public bool Center() { WindowService.Center(Target); return true; }
    public bool Snap(SnapSide side) { WindowService.Snap(Target, side); return true; }
    public bool SendToBottom() { WindowService.SendToBottom(Target); return true; }
    public bool CycleSameApp(bool backward) => WindowService.CycleSameApp(backward);
    public bool ActivateDirectional(string dir) => WindowService.ActivateDirectional(dir);
}