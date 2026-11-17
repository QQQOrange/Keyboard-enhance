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
                "Name": "右键加左键",
                "Gesture": [
                  { "$type": "KeyDown", "Key": "MOUSE:1" },
                  { "$type": "KeyDown", "Key": "MOUSE:0" }
                ],
                "Command": { "$type": "SendKeys", "Keys": "^v{ENTER}" }
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
        CheckEqual("导入四条、跳过一条脚本", $"{imported}/{skipped}", "4/1");
        Check("粘贴导入成笔画 + sendKeys",
            settings.Gestures.Any(g => g.Name == "粘贴" && g.Kind == "stroke" &&
                                       g.Action == "sendKeys" && g.Arg == "ctrl+v" && g.HasPath));
        Check("静音导入成中键单击动作",
            settings.Gestures.Any(g => g.Name == "静音" && g.Kind == "click" &&
                                       g.Action == "volumeMute" && g.Button == "middle"));
        Check("右键+左键导入成组合单击，两颗键都不丢",
            settings.Gestures.Any(g => g.Name == "右键加左键" && g.Kind == "click" &&
                                       g.Button == "right" && g.Chord == "left"));
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

        // ---- 方向串：识别真正用的键 ----
        var rightPts = new List<GestureMath.Pt>();
        for (int x = 100; x <= 420; x += 20) rightPts.Add(new GestureMath.Pt(x, 300));
        CheckEqual("向右一笔读成 →",
            GesturePattern.FromPoints(rightPts, 20, 0.10, 120), "→");

        var upRight = new List<GestureMath.Pt>();
        for (int y = 600; y >= 300; y -= 20) upRight.Add(new GestureMath.Pt(400, y));
        for (int x = 420; x <= 720; x += 20) upRight.Add(new GestureMath.Pt(x, 300));
        CheckEqual("先上再右读成 ↑→",
            GesturePattern.FromPoints(upRight, 20, 0.10, 120), "↑→");

        // 手抖：来回 4px 不该被读成一个新方向
        var shaky = new List<GestureMath.Pt> { new(100, 300) };
        for (int i = 0; i < 20; i++) shaky.Add(new GestureMath.Pt(100 + i * 10, 300 + (i % 2 == 0 ? 4 : -4)));
        CheckEqual("横向手抖不会多出一段", GesturePattern.FromPoints(shaky, 20, 0.10, 120), "→");

        Check("方向串能画成箭头", GesturePattern.Describe("↑→") == "↑ →", GesturePattern.Describe("↑→"));

        // ---- 通配 * 兜底，但精确串优先 ----
        var wildSettings = new AppSettings();
        var anyFlat = new List<int> { 0, 0, 1000, 0, 1000, 1000, 0, 1000 };
        wildSettings.Gestures.Add(new MouseGesture
        {
            Id = "wild", Name = "通配", Kind = "stroke", Button = "right",
            Pattern = GesturePattern.Wildcard, Path = anyFlat, Action = "copy"
        });
        wildSettings.Gestures.Add(new MouseGesture
        {
            Id = "exact", Name = "精确", Kind = "stroke", Button = "right",
            Pattern = "→", Path = flat, Action = "paste"
        });
        var exactHit = GestureService.PickStroke(wildSettings, 0x02, "", "→", rightPts, wildSettings.GestureOptions);
        Check("有精确方向串时不用通配", exactHit.Hit?.Id == "exact", exactHit.Hit?.Id ?? "null");
        var wildHit = GestureService.PickStroke(wildSettings, 0x02, "", "↑↓", rightPts, wildSettings.GestureOptions);
        Check("没配过的串交给通配", wildHit.Hit?.Id == "wild", wildHit.Hit?.Id ?? "null");

        // ---- 改键回声的「手势通行证」一次性 ----
        InputSender.AllowGesturePass(0x04);
        Check("改键回声放行一次手势", InputSender.TakeGesturePass(0x04));
        Check("通行证用完即作废", !InputSender.TakeGesturePass(0x04));
        Check("没申请过的键不会被放行", !InputSender.TakeGesturePass(0x05));

        try { File.Delete(path); } catch { }
    }
}
