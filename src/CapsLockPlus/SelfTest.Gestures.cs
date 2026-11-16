using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Gestures;
using CapsLockPlus.Core.Input;
using CapsLockPlus.UI;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus;

/// <summary>
/// 自检：鼠标手势的形状数学、WGestures 按键翻译与导入。
///
/// 这些用例不碰钩子、不碰界面，跑在纯内存对象上 —— 手势识别最容易出错的
/// 地方是"归一化和打分"，与有没有鼠标无关。
/// </summary>
public static partial class SelfTest
{
    private static void TestGestures()
    {
        // ---- 归一化：同样形状、不同位置与大小，应当几乎是同一条线 ----
        var big = new List<GestureMath.Pt> { new(100, 200), new(400, 200), new(700, 200) };
        var small = new List<GestureMath.Pt> { new(10, 10), new(40, 10), new(70, 10) };
        var normBig = GestureMath.Normalize(big);
        var normSmall = GestureMath.Normalize(small);
        Check("手势形状与画多大无关",
            GestureMath.Distance(normBig, normSmall) < 0.03,
            GestureMath.Distance(normBig, normSmall).ToString("0.000"));

        // ---- 方向不同不能认成同一笔 ----
        var down = new List<GestureMath.Pt> { new(200, 100), new(200, 400), new(200, 700) };
        Check("向右不会认成向下",
            GestureMath.Distance(normBig, GestureMath.Normalize(down)) > 0.25,
            GestureMath.Distance(normBig, GestureMath.Normalize(down)).ToString("0.000"));

        // ---- Match 只在阈值内返回结果 ----
        var right = new MouseGesture
        {
            Id = "right",
            Kind = "stroke",
            Action = "sendKeys",
            Arg = "ctrl+c",
            Path = GestureMath.ToFlat(normBig)
        };
        var downward = new MouseGesture
        {
            Id = "down",
            Kind = "stroke",
            Action = "sendKeys",
            Arg = "ctrl+v",
            Path = GestureMath.ToFlat(GestureMath.Normalize(down))
        };
        var (hit, _) = GestureMath.Match(new[] { right, downward }, big, 0.3);
        CheckEqual("手势匹配选中同方向的那条", hit?.Id, "right");

        var (miss, _) = GestureMath.Match(new[] { downward }, big, 0.1);
        Check("方向不符时宁可不认", miss == null);

        // ---- AutoHotkey 按键串 → 本软件的序列写法 ----
        CheckEqual("^v 翻译成 ctrl+v", WGesturesImporter.ConvertKeys("^v"), "ctrl+v");
        CheckEqual("{LCtrl}(c) 翻译成 ctrl+c", WGesturesImporter.ConvertKeys("{LCtrl}(c)"), "ctrl+c");
        CheckEqual("%{F4} 翻译成 alt+f4", WGesturesImporter.ConvertKeys("%{F4}"), "alt+f4");
        CheckEqual("粘贴后回车是两段", WGesturesImporter.ConvertKeys("^v{ENTER}"), "ctrl+v enter");
        CheckEqual("带次数的音量键展开", WGesturesImporter.ConvertKeys("{VOLUME_DOWN 2}"), "volumedown volumedown");

        // ---- 导入：一份最小配置，覆盖三种触发与两种命令 ----
        string json = """
        {
          "General": {
            "Intents": [
              {
                "Name": "粘贴",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:1" },
                  { "$type": "Stroke", "P": [ 100, 300, 110, 100 ] }
                ],
                "Command": { "$type": "SendKeys", "Keys": "^v" }
              },
              {
                "Name": "静音",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:2" }
                ],
                "Command": { "$type": "SendKeys", "Keys": "{VOLUME_MUTE}" }
              },
              {
                "Name": "任务管理器",
                "Gesture": [
                  { "$type": "MoveToEdgeCorner", "EdgeCorner": { "Value": 1 } },
                  { "$type": "KeyDown", "Key": "MOUSE:4" }
                ],
                "Command": { "$type": "RunApp", "Path": "taskmgr.exe", "Cmd": "" }
              },
              {
                "Name": "自家脚本",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:1" },
                  { "$type": "Stroke", "P": [ 100, 100, 300, 300 ] }
                ],
                "Command": { "$type": "Script", "Code": "WG.DoSomething()" }
              }
            ]
          },
          "Apps": []
        }
        """;

        string path = Path.Combine(Path.GetTempPath(), "clp-gestures-selftest.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));

        var settings = new AppSettings();
        var (imported, skipped) = WGesturesImporter.ImportFile(path, settings);
        CheckEqual("导入三条、跳过一条脚本", $"{imported}/{skipped}", "3/1");
        Check("粘贴导入成笔画 + sendKeys",
            settings.Gestures.Any(g => g.Name == "粘贴" && g.Kind == "stroke" &&
                                       g.Action == "sendKeys" && g.Arg == "ctrl+v" && g.HasPath));
        Check("静音导入成中键单击动作",
            settings.Gestures.Any(g => g.Name == "静音" && g.Kind == "click" &&
                                       g.Action == "volumeMute" && g.Button == "middle"));
        Check("角落 + 侧键导入成 corner",
            settings.Gestures.Any(g => g.Name == "任务管理器" && g.Kind == "corner" &&
                                       g.Corner == 1 && g.Button == "x2" && g.Action == "runProgram"));

        var again = WGesturesImporter.ImportFile(path, settings);
        CheckEqual("重复导入不产生重复手势", again.Imported, 0);

        // ---- 应用手势优先于全局手势 ----
        var scopedSettings = new AppSettings();
        var strokePts = new List<GestureMath.Pt> { new(100, 100), new(300, 100), new(500, 100) };
        var flat = GestureMath.ToFlat(GestureMath.Normalize(strokePts));
        scopedSettings.Gestures.Add(new MouseGesture
        {
            Id = "global", Name = "全局", Kind = "stroke", Button = "right", Path = flat, Action = "copy"
        });
        scopedSettings.Gestures.Add(new MouseGesture
        {
            Id = "scoped", Name = "应用", Kind = "stroke", Button = "right", Path = flat,
            Action = "winClose", AppPath = @"C:\Apps\browser.exe"
        });
        var inApp = GestureService.PickStroke(
            scopedSettings, 0x02, @"C:\Apps\browser.exe", strokePts, scopedSettings.GestureOptions);
        var elsewhere = GestureService.PickStroke(
            scopedSettings, 0x02, @"C:\Apps\notepad.exe", strokePts, scopedSettings.GestureOptions);
        Check("同一手势：应用里配了就用应用的那条", inApp.Hit?.Id == "scoped", inApp.Hit?.Name ?? "null");
        Check("同一个手势换到别的程序走全局", elsewhere.Hit?.Id == "global", elsewhere.Hit?.Name ?? "null");

        // ---- 改键回声的「手势通行证」一次性 ----
        InputSender.AllowGesturePass(0x04);
        Check("改键回声放行一次手势", InputSender.TakeGesturePass(0x04));
        Check("通行证用完即作废", !InputSender.TakeGesturePass(0x04));
        Check("没申请过的键不会被放行", !InputSender.TakeGesturePass(0x05));

        try { File.Delete(path); } catch { }
    }
}
