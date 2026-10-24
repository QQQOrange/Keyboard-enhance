using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

public sealed class MouseService
{
    private int _streak;
    private DateTime _lastMove = DateTime.MinValue;
    public int Speed { get; set; } = 3;
    public int Step { get; set; } = 10;

    public bool Move(int dx, int dy)
    {
        var now = DateTime.Now;
        if ((now - _lastMove).TotalMilliseconds < 350) _streak++;
        else _streak = 0;
        _lastMove = now;
        double factor = 1 + Math.Min(_streak, 24) * 0.12;
        int baseStep = Math.Max(1, Step) * Math.Max(1, Speed) / 3;
        int dist = (int)Math.Round(baseStep * factor);
        InputSender.MoveRelative(dx * dist, dy * dist);
        return true;
    }
}