# 构建与运行（Windows 11 x64 / .NET 10）

构建需要 **.NET 10 SDK**；**运行不需要任何运行时** —— 发布产物自带。

```powershell
# 还原 & 构建
dotnet build src\CapsLockPlus\CapsLockPlus.csproj

# 单文件发布（框架依赖，约 2.8 MB；运行前需装 .NET 10 桌面运行时）
dotnet publish src\CapsLockPlus\CapsLockPlus.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist

# 需要更小（放弃内置自检与离屏渲染，再省约 0.5 MB）
dotnet publish src\CapsLockPlus\CapsLockPlus.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeDiagnostics=false -o dist-lean
```

### 一次出齐四个发布形态

发版用的就是这四条（Win10/11 框架依赖、Win7 自包含，各 x64/x86）。
**每个形态之间要先清掉 `obj` 与 `bin`** —— 不同 RID 的中间产物混在一个目录里会互相干扰。
这一步已经封进脚本，不要手敲：

```powershell
# 出齐四个形态 + 哈希清单 + 发布说明 + manifest（约 40 秒，产物在 dist\<版本>\）
powershell -ExecutionPolicy Bypass -File tools\release.ps1

# 只出一个形态（改动小、只验证发布链路时用）
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Only win10-x64

# 不重新构建，只按目录里已有的 exe 重算哈希与清单（上传前复核）
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -SkipBuild
```

产物（文件名与 GitHub Release 的资产名一致，可直接上传）：

```
dist\4.20.5\
  KeyboardEnhance.exe                                    Win10/11 x64
  KeyboardEnhance-4.20.5-x86-Win10-Win11.exe             Win10/11 x86
  KeyboardEnhance-4.20.5-x64-Win7-SelfContained.exe      Win7 SP1 x64
  KeyboardEnhance-4.20.5-x86-Win7-SelfContained.exe      Win7 SP1 x86
  SHA256SUMS.txt     每行「哈希  文件名」，certutil / 7-Zip 都能校验
  manifest.json      版本 / 大小 / 哈希 / 缺了哪个形态（CI 与上传脚本用）
  release-notes.md   从 CHANGELOG.md 抽出的本版说明（Release 正文）
```

> 版本号只认 `src\CapsLockPlus\CapsLockPlus.csproj` 里的 `<Version>`；
> 文件名、页脚版本、更新检查全都跟它走，所以**发版第一步是改 csproj**。
> 标签和 csproj 版本不一致时，CI 会直接失败（避免用错版本号覆盖已发布资产）。

### 持续集成

`.github/workflows/build.yml`：每次推 main / 开 PR 跑两件事 ——
两个 TFM 各构建一遍（必过），再用发布产物跑 `--selftest`（提醒性，
CI 机器没有真实桌面，个别用例会因环境限制跳过）。

`.github/workflows/release.yml`：推 `v*` 标签或手动 Run workflow 时，
用同一份 `tools/release.ps1` 出齐四个形态并创建 / 更新 GitHub Release，
附带 `SHA256SUMS.txt` 与 `manifest.json`。

> **只有一个 exe**。曾经拆成"启动器 + 主程序"两个文件来解决
> "没装 .NET 点了没反应"，但那让用户不知道该双击哪个；
> 也试过把运行时整个打进去，体积从 5 MB 涨到 75 MB。
> 现在是一个**框架依赖的单文件**（约 3.8 MB）：体积和"只有一个 exe"都保住了，
> 代价是机器上要先装 .NET 10 桌面运行时（README 顶部有下载地址）。

## 自检

程序内置 2000+ 项自检，覆盖按键模型、表达式求值、Qbar 检索与标签、
设置读写、旧版 ini 迁移、窗口与进程、输入注入、键盘钩子、组合键状态机、
加密与备份、横条与连续调节。

```powershell
dist\KeyboardEnhance.exe --selftest
```

退出码 0 表示全部通过。自检日志写在数据目录（默认在 exe 同级的
`KeyboardEnhance Data`，不可写时回落 `%LocalAppData%\KeyboardEnhance`），
可用环境变量 `CAPSLOCKPLUS_DATA` 覆盖（便于测试与截图，互不干扰）。

## 界面截图 / 回归

离屏渲染所有页面与浮层，结果不受桌面遮挡、DPI 缩放和焦点动画影响，可复现：

```powershell
dist\KeyboardEnhance.exe --render docs\screenshots
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
| `--page=<页面>` | 启动并直接打开某页（guide/keys/qbar/clipboard/macros/layers/backup/settings/test/help/about） |
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
├─ UI/                    WPF 界面
│  ├─ Windows/            主窗口、Qbar、计算器、引导、Toast、更新提示
│  ├─ Views/              9 个页面
│  ├─ Theme/              主题、Win11 窗口外观
│  └─ AppHost.cs          装配中心：引擎 + 托盘 + 窗口
├─ Theme/                 浅色/深色调色板与控件模板
└─ SelfTest.cs            自检
```

新增一个可绑定动作只需在 `Core/Actions/ActionCatalog.cs` 里加一行，
界面（按键映射页、快捷键速查页）会自动出现该动作。
