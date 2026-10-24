using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

public sealed class BrightnessAccel
{
    /// <summary>加速到顶需要多少步（45ms/步时约 0.7 秒）。</summary>
    public const int MaxStreak = 15;

    /// <summary>每多一步，步长增加多少（1.0 → 2.5 倍）。</summary>
    public const double PerStep = 0.10;

    private int _streak;
    private double _ideal;
    private int _sent;

    public int Streak => _streak;

    /// <summary>松开 / 单次按键后重新起步。</summary>
    public void Reset()
    {
        _streak = 0;
        _ideal = 0;
        _sent = 0;
    }

    /// <summary>连发的下一步该走几个百分点（正数，方向由调用方给）。</summary>
    public int Next()
    {
        _streak = Math.Min(_streak + 1, MaxStreak);

        double factor = 1 + _streak * PerStep;
        _ideal += factor;

        // 四舍五入到最近整数；Math.Floor(x+0.5) 避开 2.5→2 的银行家舍入
        int target = (int)Math.Floor(_ideal + 0.5);
        int step = Math.Max(1, target - _sent);

        _sent += step;
        return step;
    }
}