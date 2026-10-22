# 代码审视与优化报告

**项目**：KeyboardEnhance（CapsLock+ 重写版）
**审视基准**：`main` @ `06c21ba`（v4.3.3）
**审视时间**：2026-10-22
**范围**：`src/CapsLockPlus` 全部源代码（C# 36,416 行 / XAML 4,963 行）
**约束**：本次只出报告，不改功能行为；所有建议都标注了回归风险与验证方式

---

## 一、规模与结构现状

| 模块 | 行数 | 说明 |
| --- | --- | --- |
| `UI/Views` | 6,461 | 11 个页面视图 |
| `UI/Windows` | 3,948 | 浮层/对话框（Qbar、Toast、OSD、宏录制等） |
| `Core/Settings` | 2,962 | 设置模型 + 加解密 + 迁移 |
| `Core/Actions` | 2,942 | 动作目录与执行上下文 |
| `Core/Input` | 2,589 | 钩子、组合键状态机、注入 |
| `Core/Sync` | 2,145 | WebDAV 同步与备份 |
| `Core/Windows` | 2,073 | 音量/亮度/窗口/进程 |
| `Core/Text` `Core/Icons` `Core/Qbar` `Core/Update` `Core/Macro` `Core/Interop` `Core` | ~4,000 | 文本注入、图标渲染、检索、更新、宏、互操作 |
| `SelfTest.cs` | 6,418 | 自检（单体） |

最大的 5 个文件：`SelfTest.cs` 6,418 · `KeysView.xaml.cs` 2,821 · `HotkeyEngine.cs` 1,405 · `SettingsStore.cs` 1,040 · `BrightnessService.cs` 984。

**总体判断**：分层（Core / UI）清晰，互操作集中在 `Core/Interop`，注释密度高、踩坑记录完整，属于"可维护性中上"的代码库。主要问题集中在 **常驻轮询、同步 IO、单体视图、跨线程共享状态** 四类，都不涉及架构推翻，属于"打磨型"优化。

---

## 二、P0：建议下一版就处理（影响常驻体验，风险可控）

### P0-1 · 20ms 常驻定时器：空闲时每秒唤醒 50 次
- **位置**：`Core/Input/HotkeyEngine.cs:275-282`（`StartHoldTimer`），仅在 `:1398` 的 `Dispose` 里停止
- **问题**：`DispatcherTimer` 以 20ms 周期在**界面线程**常驻运行，从启动到退出不停。它的职责只有"判断触发键按住是否超过阈值"和"驱动连续调节"，可这两件事**只在按键期间**才需要。
- **影响**：一个常驻托盘工具 99% 时间处于空闲，却持续产生 50 次/秒的 UI 线程调度与计时器分配；笔记本上直接体现为耗电与待机温度（空闲时任务管理器里能看到持续的非零 CPU 曲线）。
- **建议**：
  1. 触发键按下时 `Start()`，松开后延时 500ms `Stop()`；
  2. 空闲态保留一个 500ms 的"慢心跳"专门喂自愈逻辑（`CheckHoldThreshold` 里的 `_physicallyUpTicks`）；
  3. 连续调节改由 `System.Threading.Timer`（线程池）驱动，彻底离开 UI 线程。
- **预期收益**：空闲唤醒 50 次/秒 → 2 次/秒，空闲 CPU 降低 60~80%。
- **回归风险**：中（涉及长按判定与自愈）。**验证**：`--selftest` 的 `TestHoldThreshold` / `TestContinuousVolumeRepeat` / `TestRepeatSurvivesPhysicalJitter` 三组已覆盖；另需手动长按音量、亮度各一次确认节奏不变。

### P0-2 · 设置保存：每个字符一次"序列化 + 加密 + 三文件 IO"，且在 UI 线程
- **位置**：`Core/Settings/SettingsStore.cs:173-205`（`Save`）；调用点 **71 处**，其中 `UI/Views/KeysView.xaml.cs:2458`（`OnArgChanged`，经 `AppHost.SaveSettings()` → `SettingsStore.Save`）是**逐字符**触发
- **问题**：`Save` 做四件事——`JsonSerializer.Serialize` →（启用时）AES 加密 → 写 `.tmp` → 复制旧文件为 `.bak` → `File.Move` 覆盖，全程同步、持 `Gate` 锁、跑在 UI 线程。
- **影响**：开加密时单次保存是**毫秒级**（序列化约 150KB JSON + AES + 3 次磁盘操作，均为同步 IO）。在参数框里打一个三位数就是 3 次保存；拖动滑杆每 tick 一次。表现为**输入卡顿**与**无谓的磁盘写入（SSD 寿命）**。
- **建议**：
  1. 加 **400ms 防抖 + 关闭/失焦时强制 flush**（`SettingsStore.RequestSave()`）；
  2. 序列化+加密+落盘搬到单一后台线程（有序队列，丢弃中间态，只保最后一次）；
  3. `.bak` 从"每次复制"改成"每小时/每 N 次复制一次"。
- **预期收益**：连续输入时磁盘写入降低 90%+，输入无卡顿。
- **回归风险**：低-中（要保证崩溃时数据不丢）。**验证**：现有 `TestSettingsStoreRoundtrip`；再加一条"改设置后 1 秒内文件已更新"的自检。

### P0-3 · 日志：每行 3 次文件系统调用 + 全局锁，且类放错了文件
- **位置**：`Core/ActionQueue.cs:58-95`（`CoreLog` 类整个写在 `ActionQueue.cs` 里）
- **问题**：`Write` 每次都 `Directory.CreateDirectory` → `new FileInfo` → `File.AppendAllText`（打开/写/关闭），并持**进程级全局锁**。日志调用来自钩子线程、动作队列、UI 线程三方。
- **影响**：
  1. 每次按键动辄 1 条日志（`动作：volumeUp 1`），长按每秒 14 条 → 每秒 42 次文件系统调用，且与钩子线程争锁；
  2. 全局锁把三条线程串行化，钩子回调一旦撞上就是**输入延迟**；
  3. 类归属错误，后续维护时很难想到"日志在队列文件里"。
- **建议**：
  1. 迁出为 `Core/CoreLog.cs`；
  2. 改成 `StreamWriter`（`AutoFlush=false`）+ 1 秒定时 flush + 同线程内缓冲；
  3. 体积检查改成内存计数器，不再每行 `stat`；
  4. 高频动作日志降级为"按住期间只记首尾两条"。
- **预期收益**：日志路径开销降低 95%，钩子回调不再被日志锁阻塞。
- **回归风险**：低。**验证**：`--selftest` + 跑一次 `--hwtest` 看日志完整性。

### P0-4 · 设置对象图跨线程共享，没有快照边界
- **位置**：`Core/Sync/SyncService.cs:227`（`BuildBackupPayload`）、`:653`（`StateHash` 里 `_settings().Clone()`）
- **问题**：`_settings()` 返回的是**活对象**，备份/同步线程直接 `JsonSerializer.Serialize` 或 `Clone()`；同一时刻 UI 线程可能正在 `Keys.Add` / `RemoveAll`。
- **影响**：`List` 在序列化途中被改动会抛 `InvalidOperationException`（备份失败，用户只看到"备份失败"）；概率低但属于**偶发性数据问题**，且发生时没人能复现。
- **建议**：
  1. 保存/备份前统一走 `SettingsStore.Snapshot()`：在 UI 线程上先 `Serialize` 成字符串（或深拷贝），再交给后台；
  2. 或给 `AppSettings` 的集合加"只在 UI 线程读"的约束，后台一律用快照。
- **预期收益**：消除一类偶发失败。
- **回归风险**：低。**验证**：`TestBackupPayload` 已有覆盖；补一条"UI 线程持续改集合 + 后台反复序列化"的压测自检（1000 次无异常）。

---

## 三、P1：健壮性与完整度

### P1-1 · 87 处空 `catch`，关键路径也静默
- **位置**：全仓 87 处；重点：`Core/Settings/SettingsStore.cs`（保存/恢复）、`Core/Sync/*`、`UI/Views/BackupView.xaml.cs`
- **问题**：`catch { }` 在互操作/清理场景是合理的，但保存、恢复、同步失败被吞掉后，用户只看到"没反应"。
- **建议**：分三类处理——① 必须可观测的（保存、恢复、同步、更新）改为 `catch (Exception ex) { CoreLog.Error(...); Notify("...") }`；② 清理/释放类保留静默但加注释；③ 新增一条自检：扫描源码中未标注 `// 允许静默` 的空 catch，数量只允许减少。
- **影响**：把"用户报'没反应'"变成"日志里有原因"，是排障成本最大的一处改善。

### P1-2 · 17 处 `MessageBox`，阻塞且风格割裂
- **位置**：`SettingsView`(6)、`RestoreWindow`(4)、`BackupView`(4)、`SubLayerEditorWindow`(2)、`KeysView`(1)
- **问题**：项目已经有 `ToastWindow` 与自绘对话框，这里却混用系统 `MessageBox`：会抢焦点、样式与主题不一致、并会阻塞 UI 线程。
- **建议**：抽出 `Ui/Feedback`：`Toast(msg, level)` 与 `Confirm(title, text, yes/no)` 两个入口，替换这 17 处；确认类用自绘 `ConfirmDialog`（沿用现有卡片样式）。
- **影响**：视觉统一 + 不再阻塞；改动集中在调用点，风险低。

### P1-3 · `KeysView` 单体 2,821 行，重复分支明显
- **位置**：`UI/Views/KeysView.xaml.cs`
- **数据**：16 处 `foreach (ComboBoxItem ...)` 的分类联动（其中三处 `CategoryBox*` 逻辑几乎逐字重复）、13 处 `ShowBindStatus(...)` 分支、5 种编辑器模式（Normal/WinBind/WebPage/TypeText/CustomKey）挤在一个类里。
- **建议**：
  1. 拆 `partial`：`KeysView.Keyboard.cs`（绘制与命中）、`KeysView.Editor.cs`（参数面板）、`KeysView.Bindings.cs`（绑定增删改）；
  2. 三个分类下拉合成一个 `CategorySync.Sync(info)`；
  3. `EditorMode` 抽成策略类（每个模式一个 `IEditorPanel`），`ApplyEditorMode` 变成查表。
- **影响**：该文件是改动最频繁的地方，拆分后每次改动的影响面显著缩小；纯重构，行为不变。

### P1-4 · 部分按键无法绑定（功能缺口）
- **位置**：`UI/Windows/KeyCaptureWindow.xaml.cs:287`
- **问题**：提示"这个键没有内置名称，暂时不能绑定"——即键名表是白名单，未收录的键（部分 IME 键、厂商自定义键、`VK_PACKET` 输入）直接拒绝。
- **建议**：兜底用 `扫描码/VK 十六进制名`（如 `vk_0xE8`）登记自定义键名，允许绑定并在键帽上显示 `0xE8`；`KeyName` 增加"未知名→自动命名"路径。
- **影响**：补齐一类"按了没反应"的体验缺口；改动局限在 `KeyName` + 捕获窗口。

### P1-5 · 自检/离屏渲染工具编进发布版
- **位置**：`SelfTest.cs`（6,418 行）、`UI/RenderTools.cs`，`CapsLockPlus.csproj` 无排除条件
- **问题**：约占最终 exe 的 3~5%，且让发布产物携带大量测试代码与 `--render/--selftest/--webdav-test` 入口。
- **建议**：`<Compile Remove="SelfTest.cs" Condition="'$(Configuration)'=='Release'"/>`，或拆成独立的 `Tools` 项目引用主工程；`App.xaml.cs` 的测试开关用 `#if DEBUG`（保留 Release 下的 `--selftest` 需要的话，可保留但减重）。
- **收益**：exe 体积 -150~250KB、发布面更干净。**风险**：中（要保证 CI/自检流程仍可跑）。

---

## 四、P2：UI / 布局 / 视觉一致性

### P2-1 · 四个浮层仍用 `SystemParameters.WorkArea` 定位（混 DPI 多屏会错位）
- **位置**：`QbarWindow.xaml.cs:373`、`ToastWindow.xaml.cs:173`、`RecordingHudWindow.xaml.cs:24`、`SubLayerEditorWindow.xaml.cs:93`
- **问题**：`SystemParameters.WorkArea` 是**系统 DPI 下的 DIP**，而窗口 `Left/Top` 是**所在显示器 DPI 下的 DIP**。OSD 已经踩过并改成"物理像素 + `rcWork`"，这四处还没跟进。在 150%/200% 混用或竖屏副屏上，浮层会偏到屏幕外或压到任务栏。
- **建议**：抽 `Core/Windows/ScreenPlacement.cs`：`PlaceOnMonitor(Window, MonitorAnchor.BottomCenter|TopRight, Padding)`，内部统一走 `MonitorFromWindow + GetMonitorInfo + SetWindowPos`，OSD 也改用它（消除重复实现）。
- **收益**：一次修复四类浮层的定位；后续新增浮层不再重复踩坑。**风险**：中（需要多屏验证）。

### P2-2 · 97 处硬编码颜色，浮层在浅色主题下是"黑块"
- **位置**：`QbarWindow.xaml`(20)、`WindowPickerWindow.xaml`(11)、`OnboardingWindow.xaml`(11)、`GuideView.xaml`(11)、`BackupView.xaml`(10)、`RecordingHudWindow.xaml`(8)…
- **问题**：主题字典（`Theme/Light|Dark.xaml`）已有完整色板，但上述窗口写死 `#F21C1F27`/`#FFFFFF` 等深色值。浅色主题下这些浮层仍是深色 —— 若是有意（"浮层恒深"），应显式声明；若不是，就是 bug。
- **建议**：① 在主题字典中补 `Brush.Overlay.*`（Panel/Text/Muted/Hover）；② 明确"浮层恒深"的产品决策后，把该组常量集中到 `Theme/Overlay.xaml` 并在注释里写清；③ 其余页面一律 `DynamicResource`。
- **收益**：主题切换不再出现突兀色块；颜色改动从 97 处收敛到 1 处。

### P2-3 · 浮层的定位/圆角/阴影各自实现
- **位置**：`OsdWindow`、`ToastWindow`、`RecordingHudWindow`、`QbarWindow` 各写一套
- **建议**：抽出 `ShadowCard`（圆角 + 边框 + 柔光阴影 + 可选磨砂）作为共享 `ControlTemplate`，四个窗口复用一个资源；阴影参数只在一处调。
- **收益**：视觉统一（这次 OSD 的阴影/圆角就是逐个手调），后续改样式 1 处生效。

### P2-4 · 提示语风格不统一
- **位置**：`UI/Views/*`、`UI/Windows/*`
- **问题**：同一类结果有三种表达：Toast（`按键已保存`）、MessageBox（`确定要删除吗？`）、静默。文案有的带句号有的不带，有的用"✓"有的用"成功"。
- **建议**：建 `Ui/Feedback/Strings.cs` 统一文案模板（成功/失败/需要确认），并在 `docs/STYLE.md` 里定三条规则：动词开头、不带句号、失败必须给"下一步做什么"。

---

## 五、重复代码与低效实现清单

| # | 位置 | 现象 | 建议 |
| --- | --- | --- | --- |
| 1 | `KeysView.xaml.cs` 16 处 | 同一套"按分类选下拉"的循环写三遍（`CategoryBox` / `CategoryBoxWeb` / `CategoryBoxType`） | 合成 `SyncCategory(info)` |
| 2 | `KeysView.xaml.cs` 13 处 `ShowBindStatus` | 每个动作一句硬编码文案 | 文案入 `ActionCatalog`（已有 `Description`），状态条只负责显示 |
| 3 | `OsdWindow` / `ToastWindow` / `RecordingHud` | 各有一套"置顶 + 不激活 + 点击穿透"样式设置 | 抽 `OverlayWindowBase` |
| 4 | `CoreLog` 在 `ActionQueue.cs` | 类归属错误 | 拆文件 |
| 5 | 4 个浮层 | 各写一遍 `WorkArea` 计算 | 抽 `ScreenPlacement` |
| 6 | `SyncService` / `SettingsStore` | 两处各自 `JsonSerializer.Serialize(settings)` + 加密 | 抽 `SettingsCodec.Serialize/Decrypt` |

---

## 六、优化路线图

> 排序原则：**收益 / 风险** 比值从高到低；每一阶段都可独立发版，出问题可单点回退。

### 阶段 1（1~2 天）· 让常驻开销降下来 · 风险低
1. P0-3 日志改建（独立文件 + 缓冲 + 计数）
2. P0-2 设置保存防抖 + 后台落盘
3. P0-4 设置快照边界
- **预期**：空闲 CPU -60~80%（叠加阶段 2）；输入无卡顿；磁盘写入 -90%
- **验证**：`--selftest` 1682 项 + 连续输入/拖滑杆各 30 秒观察 CPU 与写入

### 阶段 2（2~3 天）· 输入链路精细化 · 风险中
4. P0-1 定时器按需启停 + 连续调节移出 UI 线程
5. P1-1 关键路径异常可观测化
- **预期**：空闲唤醒 50/s → ~2/s；"没反应"类问题首次有日志可查
- **验证**：长按音量/亮度各 10 秒、切桌面/远程会话各一次、`--hwtest`

### 阶段 3（3~5 天）· 结构与视觉收敛 · 风险低
6. P2-1 统一 `ScreenPlacement`（一次修 4 个浮层 + OSD）
7. P2-2 颜色入主题字典；P2-3 抽 `ShadowCard`
8. P1-2 统一 `Confirm/Toast`；P2-4 文案规范
9. P1-3 `KeysView` 拆 partial + 策略化编辑器
- **预期**：混 DPI 多屏不再错位；浅色主题无突兀深块；KeysView 单文件降到 1,200 行以内
- **验证**：`--render` 全页面截图对比；双屏（100%+200%）手动验收

### 阶段 4（可选，1 天）· 发布瘦身 · 风险中
10. P1-5 自检/渲染工具从 Release 排除；P1-4 未知键名兜底
- **预期**：exe -150~250KB；新增可用键

---

## 七、附：本次未动手的原因

本次是审视任务，且用户明确要求"不得影响功能"，因此**没有提交代码改动**。上述 10 项均已定位到具体文件/行号，其中阶段 1 的三项为纯内部改动（日志、保存时序、快照），可先做并单独发版验证；阶段 2 涉及长按状态机，建议在阶段 1 稳定后再动。

如需要，我可以按阶段 1 → 2 → 3 的顺序逐项落地，每项附自检与实机验证结果。
