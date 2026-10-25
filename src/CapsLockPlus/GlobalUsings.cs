// 显式声明全局 using，避免 WPF 与 WinForms 的隐式 using 互相打架（Clipboard / Point 等）。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// 界面文案：几乎每个界面文件都要 Loc.T("…")，逐个加 using 是纯噪音，
// 而且以后新加页面容易漏（要到编译报错才发现）。
global using CapsLockPlus.Core.Localization;
