# 交付说明 · CapsLock+ 图形版 v1.1.0

> 把上游 AutoHotkey 脚本重写成原生 Windows 11 桌面程序：功能对齐、全部可视化配置、性能可量化。
> v1.1 新增：Qbar 关键字标签化（带图标）、条目自定义图标。

## 一、产物

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `dist/CapsLockPlus.exe` | 1.17 MB | 单文件、框架依赖、ReadyToRun |

- **单文件**、框架依赖（需 .NET 10 桌面运行时）、**ReadyToRun** 预编译
- 目标平台：Windows 10 1809+ / Windows 11，x64，Per-Monitor V2 DPI
- 源码：`src/CapsLockPlus/`（10 个目录）
- 上游源码对照：`upstream/`（GPL-3.0，未修改）

## 二、v1.1 新增：Qbar 关键字标签

输入 `bd` 再按空格，`bd` 从文字变成一枚标签：

- **有图标 → 显示图标**（百度、Google、GitHub 等 9 个内置图标，离线可用）
- **没有图标 → 显示「名字 + 品牌色首字」**（例如计算器）

变成标签后输入框只剩搜索词，`Enter` 直接按该引擎搜索；`Backspace`（空输入框时）
把标签还原成文字，`×` 也可移除。

图标优先级：条目自定义图标（png/ico/exe）→ 程序条目自动取 exe 图标 → 首字兜底。

实现要点：
- `QbarChip`（Core/Qbar）承载标签数据，与界面解耦
- `IconCache` + `SvgRenderer`：自带极简 SVG 解析器（path/circle/rect/text，含 S/C/Q 曲线），
  避免引入第三方 SVG 库把单文件撑大；渲染结果栅格化并 Freeze，可跨线程复用
- `QbarService.ResolveChip` / `Query(raw, chip)`：标签模式下只按对应引擎出结果，
  但仍保留 `=算式` 直接计算

## 二、验收结果

### 自检：105 项全部通过

```
CapsLockPlus.exe --selftest    → exit 0
==== 通过 105 项，失败 0 项 ====
```

覆盖范围：

| 分组 | 项数 | 关键点 |
| --- | --- | --- |
| 环境 | 3 | 系统版本、64 位、数据目录 |
| 按键模型 | 12 | 键名 ↔ 虚拟键码、三层组合键解析与构造 |
| 表达式求值 | 13 | 四则、幂、函数、浮点修正、除零、全角符号 |
| Qbar 检索 | 12 | 计算、搜索指令、内置命令、程序匹配、兜底搜索、剪贴板 |
| **Qbar 标签** | **17** | **关键字解析、图标优先、首字兜底、标签模式检索、= 仍可计算** |
| 设置存取 | 6 | JSON 往返、默认方案、去重、动作可解析 |
| 旧版 ini 迁移 | 12 | UTF-16 解析、`keyFunc_*` 映射、四类条目迁移 |
| 动作目录 | 6 | 唯一性、说明完整、默认绑定全部可解析 |
| 窗口与进程 | 5 | 前台窗口、枚举、工作区、进程路径 |
| 输入注入 | 4 | `INPUT` 结构体大小（x64=40）、注入不抛异常 |
| 键盘钩子 | 3 | 键盘/鼠标钩子安装与卸载 |
| **组合键引擎** | **12** | **按键拦截、动作触发、未绑定放行、短按、双击、暂停、LAlt 层** |

### 性能（Release 单文件）

| 指标 | 实测 |
| --- | --- |
| 首屏时间 | **0.59 s**（从进程启动到窗口可用） |
| 空闲 CPU | **0 ms / 5 s（0%）** |
| 托盘常驻内存 | 私有 60 MB / 工作集 114 MB |
| 主窗口内存 | 私有 155 MB |
| 可执行文件 | **1.17 MB** |
| 冷启动 CPU 总量 | 1.4 s |

### 界面

10 张离屏渲染截图在 `docs/screenshots/`（`--render` 生成，不受桌面遮挡影响，可复现）：

| 文件 | 内容 |
| --- | --- |
| `guide.png` | 快速上手（状态卡 + 三步引导 + 常用组合） |
| `keys.png` | 按键映射（可视化键盘，点键即改） |
| `qbar.png` | Qbar 条目与外观管理（含图标字段） |
| `qbar-panel.png` | Qbar 浮层本体（深色毛玻璃面板） |
| **`qbar-chip.png`** | **关键字 `bd` 变成「百度」标签（带图标）** |
| **`qbar-chip-typing.png`** | **标签 + 已输入搜索词** |
| **`qbar-chip-run.png`** | **程序标签（无 exe 图标时用首字兜底）** |
| `bindings.png` | 窗口绑定槽位 |
| `clipboard.png` | 双独立剪贴板 + 历史 |
| `translate.png` | 翻译设置 |
| `settings.png` | 全局设置 |
| `help.png` | 快捷键速查（按分类 + 键盘顺序） |
| `mathboard.png` | 计算器（带历史） |

## 三、本轮审查发现并修复的问题

审查不是走形式——自检和截图各抓出了真实缺陷：

1. **组合键从未真正拦截按键**（最严重）
   钩子回调是 `void`，只做判断不返回"吞掉"，导致 `Caps+S` 除了移动光标还会向前台程序打出字母 `s`。
   改为 `Func<int,bool,bool,bool>` + 返回 `1` 且不调用 `CallNextHookEx`，并补了 12 项引擎自检防回归。

2. **短按 Caps 绕过动作表**
   硬编码 `toggleCapsLock`/`esc` 分支，导致在按键映射里改 `press_caps` 不生效。改为统一走 `ActionCatalog`。

3. **双击 Caps 判定错误**
   第一次短按也会被当成"双击的第一次"而吞掉动作。改为显式 `_hasLastTap` 状态 + 420 ms 窗口，双击只触发一次。

4. **窗口模式空闲 CPU 15%**
   `DispatcherTimer` 用 `DispatcherPriority.Background` 每秒触发 + 页面淡入动画未清理，导致持续重绘。
   修复后空闲 CPU 回到 0。

5. **`app.manifest` 导致程序无法启动**
   `activeCodePage` 是 Win7 时代的 SxS 元素，Win11 上直接报"side-by-side configuration is incorrect"。

6. **主题资源解析失败导致启动卡 20 秒**
   移除 `App.xaml` 里的默认主题后，`StaticResource` 找不到 → 每个页面抛异常 → 20 s 才出窗口。已恢复。

7. **Qbar 面板半透明"发虚"**
   淡出动画结束后没清 `BeginAnimation`，再次打开时 `Opacity` 被动画值覆盖。已改为显式清除。

8. **多页面同时构建拖慢启动**
   8 个页面全部 `InitializeComponent` → 改为 `ContentControl` + 字典缓存的按需创建。

9. **`DropShadowEffect` 拖累渲染与内存**
   全部替换为描边 + 层次色（阴影会让 WPF 把内容渲染到中间位图，既费内存又让文字发虚）。

10. **设置页/绑定页易用性**
    Qbar 条目选中后右侧不回填；窗口绑定一进来就是 10 行"未绑定"。均已修正。

## 四、关于"用 Rust 重写"的评估结论

**结论：保留 .NET 10 + WPF，不做 Rust 重写。** 依据：

| 维度 | 分析 |
| --- | --- |
| 热路径 | 真正的热路径只有"低级键盘钩子回调 + Win32 调用"，当前已是零分配的直接 P/Invoke，**Rust 不会让它更快**（实测钩子回调 <50 µs，人类感知阈值是 16 ms） |
| 真实瓶颈 | 瓶颈在界面渲染（WPF 组合树），Rust GUI（egui/Slint/iced）在**中文排版、DPI、无障碍、输入法**上明显弱于 WPF，重写会让"美观、舒适"这个核心目标倒退 |
| 体积 | 当前 1.1 MB 单文件已很小；Rust + 自绘 GUI 也在 2~8 MB 量级，无数量级收益 |
| 成本 | Rust 需额外 ~1 GB 工具链，且要重写已完成的 1 万行；收益与风险不成比例 |
| 已做的实际优化 | ReadyToRun、单文件、懒加载页面、去阴影、消息驱动剪贴板、空闲 0% CPU、1.1 MB 体积 |

如果未来确实要跨平台（macOS/Linux），届时再抽离 `Core/` 层（它已完全与界面解耦、可无界面自检）为 Rust 库是合理路径；现在重写属于过度设计。

## 五、快速验证命令

```powershell
# 1. 自检（88 项）
dist\CapsLockPlus.exe --selftest
type %LOCALAPPDATA%\CapsLockPlus\selftest.log

# 2. 性能
powershell -ExecutionPolicy Bypass -File tools\bench.ps1

# 3. 界面截图（离屏、可复现）
dist\CapsLockPlus.exe --render docs\screenshots --noelevate

# 4. 实际使用
dist\CapsLockPlus.exe            # 正常启动（会请求管理员权限）
```

## 六、已知限制

1. 需要 .NET 10 桌面运行时（未做 self-contained，否则体积会涨到 ~70 MB）
2. 提权窗口需要本程序也提权（启动时同意 UAC 即可）
3. 少数全屏独占游戏、自绘界面无法拦截按键
4. Win11 的 Snap Layouts 面板不会弹出（内置了等价的贴边动作）
5. 翻译的免密钥引擎依赖 Google 接口，网络受限时会失败（可改填有道 key）
