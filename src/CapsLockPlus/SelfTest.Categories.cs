using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus;

/// <summary>
/// 「分类」下拉里的两组：键盘按键 / 鼠标功能。
///
/// 用户反馈（m02525）：分类里没有「键盘按键」，而「鼠标」那一组只有左键右键、
/// 名字也不该叫「鼠标」。这几条都是**看得见的东西**，靠自检钉住比靠肉眼可靠。
/// </summary>
public static partial class SelfTest
{
    private static void TestKeyAndMouseCategories()
    {
        var settings = DefaultSettings.Create();
        var ctx = new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = new WindowBindingService(),
            Mouse = new MouseService(),
            Notify = _ => { },
        };
        var cat = ActionCatalog.Create(ctx);

        /* ① 「键盘按键」分类存在，且真的是"键盘的所有按键"。
         * 数量用下限而不是精确值：以后键盘表增删键不该连带改断言。 */
        var keyboard = cat.Items.Where(i => i.Category == "键盘按键").ToList();
        Check("「键盘按键」分类存在", keyboard.Count > 0, keyboard.Count.ToString());
        Check("「键盘按键」覆盖整块键盘（>80 个键）", keyboard.Count > 80, keyboard.Count.ToString());

        Check("字母键 A 在「键盘按键」里", keyboard.Any(i => i.Id == "key:41"));
        Check("F5 在「键盘按键」里", keyboard.Any(i => i.Id == "key:74"));
        Check("删除键在「键盘按键」里", keyboard.Any(i => i.Id == "key:2E"));
        Check("小键盘 0 在「键盘按键」里", keyboard.Any(i => i.Id == "key:60"));

        // 鼠标键与纯修饰键不该混进来：前者归「鼠标功能」，后者单独发没意义
        var mouseIds = new[] { "key:01", "key:02", "key:04", "key:05", "key:06" };
        Check("「键盘按键」里没有鼠标键", keyboard.All(i => !mouseIds.Contains(i.Id)),
            string.Join(", ", keyboard.Where(i => mouseIds.Contains(i.Id)).Select(i => i.Id)));
        Check("「键盘按键」里没有单独的 Ctrl", keyboard.All(i => i.Id != "key:A2" && i.Id != "key:11"));
        Check("「键盘按键」里没有单独的 Win", keyboard.All(i => i.Id != "key:5B" && i.Id != "key:5C"));

        // 每条都要能在键帽上显示（长键名靠短名压到 5 字内）
        var tooLong = keyboard.Where(i => i.KeycapTitle.Length is 0 or > 5).ToList();
        Check("「键盘按键」每条键帽都够短", tooLong.Count == 0,
            string.Join(", ", tooLong.Take(3).Select(i => $"{i.Title}→{i.KeycapTitle}")));

        /* ② 鼠标这一组：名字是「鼠标功能」，中键与两个侧键都在。 */
        Check("分类叫「鼠标功能」", cat.Items.Any(i => i.Category == "鼠标功能"));
        Check("旧的「鼠标」分类已改名（没有残留）", cat.Items.All(i => i.Category != "鼠标"),
            string.Join(", ", cat.Items.Where(i => i.Category == "鼠标").Select(i => i.Id).Take(3)));

        foreach (string id in new[] { "clickLeft", "clickRight", "clickMiddle", "clickX1", "clickX2",
                                      "wheelLeft", "wheelRight" })
        {
            var action = cat.Get(id);
            Check($"鼠标动作 {id} 存在", action != null, "");
            Check($"鼠标动作 {id} 在「鼠标功能」分类", action?.Category == "鼠标功能", action?.Category ?? "null");
        }

        // 中键/侧键必须真的能发出鼠标事件（走 InputSender.Tap 的鼠标分支）
        var taps = new List<int>();
        try
        {
            CapsLockPlus.Core.Input.InputSender.SenderForTest = (vk, _) => taps.Add(vk);
            cat.Invoke("clickMiddle", "");
            cat.Invoke("clickX1", "");
            cat.Invoke("clickX2", "");
        }
        finally
        {
            CapsLockPlus.Core.Input.InputSender.SenderForTest = null;
        }
        Check("点中键发出 0x04", taps.Count >= 1 && taps[0] == 0x04,
            string.Join(",", taps.Select(v => v.ToString("X2"))));
        Check("点侧键 1 发出 0x05", taps.Contains(0x05), string.Join(",", taps.Select(v => v.ToString("X2"))));
        Check("点侧键 2 发出 0x06", taps.Contains(0x06), string.Join(",", taps.Select(v => v.ToString("X2"))));

        /* ③ 滚轮左右拨：Windows 没有现成的 VK，这里用 0x0A / 0x0B 当内部键码。
         * 名字、反查、鼠标判定三者必须一致，否则键帽显示出来是 "0x0A"。 */
        Check("滚轮键：wheelLeft 有名字", CapsLockPlus.Core.Input.KeyName.NameOf(0x0A) == "wheelLeft", CapsLockPlus.Core.Input.KeyName.NameOf(0x0A) ?? "null");
        Check("滚轮键：wheelRight 有名字", CapsLockPlus.Core.Input.KeyName.NameOf(0x0B) == "wheelRight", CapsLockPlus.Core.Input.KeyName.NameOf(0x0B) ?? "null");
        Check("滚轮键：反查一致",
            CapsLockPlus.Core.Input.KeyName.VkOf("wheelLeft") == 0x0A && CapsLockPlus.Core.Input.KeyName.VkOf("wheelRight") == 0x0B);
        Check("滚轮键：算鼠标键", CapsLockPlus.Core.Input.InputDevices.IsMouseButton(0x0A));
        Check("滚轮键：算鼠标发送目标", CapsLockPlus.Core.Input.InputSender.IsMouseVk(0x0B));
    }
}
