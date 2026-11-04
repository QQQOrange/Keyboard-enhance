using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 层的"查找"部分（partial 拆分之五）。
///
/// 单独拆出来的是**纯查询**：给定层号与 combo，找出绑定；以及子层键的识别与拼装。
/// 它们不改变任何状态，是最容易单独验证的一块 —— 剩下的 `OnKeyDownInLayer`
/// 才是真正的状态机，仍然留在主文件里。
/// </summary>
public sealed partial class HotkeyEngine
{
    /// <summary>当前按住的非修饰键子层键（空 = 没有子层）。</summary>
    private string _subLayerKeyDown = "";

    /// <summary>当前激活的子层键（界面/OSD 用来显示"已切到 Tab 子层"）。</summary>
    public string ActiveSubLayer => _subLayerKeyDown;

    /// <summary>
    /// 这个键是不是当前层配置的子层键（且不是修饰键）。
    ///
    /// 只处理"非修饰键"的子层键；Ctrl/Shift/Alt/Win 由上面的分支单独记录，
    /// 它们通过位标记参与 combo 前缀。
    /// </summary>
    private bool IsSubLayerKey(AppSettings s, string keyName)
    {
        var layer = s.Layers.FirstOrDefault(l => l.Id == _activeLayer);
        if (layer == null) return false;

        foreach (var sub in layer.EffectiveSubLayers)
        {
            if (string.IsNullOrWhiteSpace(sub.Key)) continue;

            // 只取单键形式（多段的是 ctrl_shift 这类修饰组合，不在这里处理）
            if (sub.Key.Contains('_')) continue;

            if (string.Equals(sub.Key, keyName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>拼"子层键 + 修饰键 + 键"的 combo ID。</summary>
    private static string ComboForSubLayer(string subKey, KeyModifiers mods, string keyName)
        => KeyName.BuildComboEx(mods, subKey + "_" + keyName);
    /// <summary>当前按住的修饰键集合（引擎自己维护的状态）。</summary>
    private KeyModifiers HeldModifiers()
    {
        var m = KeyModifiers.None;
        if (_ctrlDown) m |= KeyModifiers.Ctrl;
        if (_shiftDown) m |= KeyModifiers.Shift;
        if (_laltDown) m |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) m |= KeyModifiers.Win;
        return m;
    }

    /// <summary>
    /// 层内绑定的查找缓存：层 ID → (组合键 ID → 绑定)。
    ///
    /// <see cref="FindInLayer"/> 在层模式下**每一次按键**都要跑。
    /// 原实现是 `Layers.FirstOrDefault(...)` 再 `Keys.FirstOrDefault(...)`，
    /// 即两次线性扫描（第 1 层通常有上百条绑定），而且用
    /// `StringComparison.OrdinalIgnoreCase` 做逐字符比较 ——
    /// 单次开销不大，但它在钩子回调路径上，累积起来会顶到
    /// LowLevelHooksTimeout（默认 300ms）的风险。
    ///
    /// 这里预先把每层的绑定建成**大小写不敏感的字典**，查找降为 O(1)。
    /// 用"层对象 + Keys 列表实例 + 条数"作为失效判据：
    /// 重新加载配置会换新实例，用户增删绑定会改变条数，两种情况都能自动重建。
    /// </summary>
    private readonly Dictionary<int, (Dictionary<string, KeyBinding> Map, List<KeyBinding> Src)>
        _layerMapCache = new();

    private KeyBinding? FindInLayer(AppSettings s, int layerId, string comboId)
    {
        var map = LayerBindingMap(s, layerId);
        return map != null && map.TryGetValue(comboId, out var hit) ? hit : null;
    }
}
