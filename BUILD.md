# 构建与运行（Windows 11 x64 / .NET 10）

需要 **.NET 10 SDK**（构建）与 **.NET 10 桌面运行时**（运行）。

```powershell
# 还原 & 构建
dotnet build src\CapsLockPlus\CapsLockPlus.csproj

# 单文件发布（框架依赖，约 1.2 MB）
dotnet publish src\CapsLockPlus\CapsLockPlus.csproj -c Release -r win-x64 --self-contained false -o dist
```

## 自检

程序内置 109 项自检，覆盖按键模型、表达式求值、Qbar 检索与标签、
设置读写、旧版 ini 迁移、窗口与进程、输入注入、键盘钩子、组合键状态机。

```powershell
dist\CapsLockPlus.exe --selftest
type %LocalAppData%\CapsLockPlus\selftest.log
```

退出码 0 表示全部通过。默认数据目录为 `%LocalAppData%\CapsLockPlus`，
可用环境变量 `CAPSLOCKPLUS_DATA` 覆盖（便于测试与截图，互不干扰）。

## 界面截图 / 回归

离屏渲染所有页面与浮层，结果不受桌面遮挡、DPI 缩放和焦点动画影响，可复现：

```powershell
dist\CapsLockPlus.exe --render docs\screenshots
```

## 性能基准

```powershell
tools\bench.ps1
```

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| `--selftest` | 跑自检并退出 |
| `--render <目录>` | 离屏渲染全部界面 |
| `--page=<页面>` | 启动并直接打开某页（guide/keys/qbar/bindings/clipboard/translate/settings/help/about） |
| `--qbar [--text=关键词]` | 直接打开 Qbar |
| `--tray` | 只驻留托盘，不显示窗口 |
| `--noelevate` | 不请求管理员权限（调试用） |
| `--elevated` | 内部使用：标记已提权，避免反复弹 UAC |

## 目录结构

```
src/CapsLockPlus/
├─ Core/                  与界面无关的核心逻辑（可无界面自检）
│  ├─ Input/              键盘钩子、Caps 组合键状态机、按键名映射、输入注入
│  ├─ Actions/            动作目录、动作上下文、剪贴板、窗口绑定
│  ├─ Windows/            窗口枚举 / 激活 / 贴边 / 置顶 / 透明度
│  ├─ Text/               选区读写、表达式求值
│  ├─ Qbar/               Qbar 检索、标签（Chip）
│  ├─ Icons/              图标缓存、极简 SVG 渲染器
│  ├─ Settings/           JSON 设置、旧版 ini 迁移、开机自启
│  ├─ Update/             检查 / 下载 / 替换更新
│  └─ Translate/          翻译服务
├─ UI/                    WPF 界面
│  ├─ Windows/            主窗口、Qbar、计算器、翻译、引导、Toast、更新提示
│  ├─ Views/              9 个页面
│  ├─ Theme/              主题、Win11 窗口外观
│  └─ AppHost.cs          装配中心：引擎 + 托盘 + 窗口
├─ Theme/                 浅色/深色调色板与控件模板
└─ SelfTest.cs            自检
```

新增一个可绑定动作只需在 `Core/Actions/ActionCatalog.cs` 里加一行，
界面（按键映射页、快捷键速查页）会自动出现该动作。
