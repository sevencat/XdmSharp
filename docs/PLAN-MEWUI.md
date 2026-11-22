# MewUI 前端迁移计划

> **状态：已完成并归档（S1–S5 全部交付）。** 2026-11-22 起 MewUI 是唯一前端，
> Avalonia 前端（`Xdm.App` / `Xdm.Ui` / `Xdm.Ui.Tests`）已删除，产物统一 `xdm.exe`。
> 本文保留「为什么这么迁移、踩过哪些坑」，后续不要再按本文的「并行双前端」口径实施。

目标：在保留现有 Avalonia 前端的前提下，新增一套基于 **MewUI + Win32 + Direct2D** 的前端，
共享同一份 ViewModel / 引擎，通过构建开关出两个 exe。本文只写结论、现状、阶段与风险，
具体代码在阶段内落地。

参考：[MewUI 文档](https://mewui.aprillz.net/docs) / [GitHub](https://github.com/aprillz/MewUI)（MIT）。

---

## 一、为什么当初是「并行新增」而不是「整体替换」

> 事后回看：并行跑通（B）是对的——先用 S1–S5 把 MewUI 侧做到可用、有测试、能量产，
> 再在 2026-11-22 一次性删掉 Avalonia 侧。若一开始就整体替换，会出现"两边都不可用"的窗口期。

| 方案 | 内容 | 结论 |
|---|---|---|
| A 整体替换 | `Xdm.Ui` 里的 Avalonia 视图全删，改写成 MewUI | ✗ 当时不做（现在是完成态：先并行、后删除） |
| B 并行新增 | Avalonia 前端原样保留，新增 `Xdm.Ui.Mew` + `Xdm.App.Mew` | ✓ 采用 |

理由：

1. MewUI 当前 **0.22.1**，官方明确「次版本之间可能出现破坏性变更」，且非目标里写明不是
   WPF/Avalonia 的 drop-in 替代。整体替换等于把稳定产物押在一个还在收敛的 API 上。
2. 现有 ViewModel 层**零 Avalonia 依赖**（实测：`ViewModels/`、`Services/`、`Formatting/`
   共 903 行，无一条 `using Avalonia`），本来就可以被两个前端共用；替换方案省不下这部分工作，
   却失去了回退面。
3. 项目已经能出 `publish\win-x64-aot` 的单文件 exe（30.1 MB），MewUI 侧可以先并行跑到可用，
   再决定主次，不必中途出现「两边都不可用」的窗口期。

## 二、现状盘点

| 层 | 文件 | 行数 | MewUI 侧怎么办 |
|---|---|---|---|
| ViewModel / Service / Formatting | 9 个 .cs | 903 | **原样复用**（仅命令与通知两处适配） |
| View（主窗口、设置、新建、关于、确认框） | 6 组 axaml + cs | ~880 | 用 MewUI 链式 C# 重写 |
| 自定义控件（`TitleBar`、`DownloadRow`、`SegmentBar`、`EditTextBox`） | 4 个 | ~370 | `SegmentBar` 换 `IGraphicsContext` 重画；其余重写 |
| 界面测试 | `Xdm.Ui.Tests` | 65 条 | 行为类平移，控件树类重写 |

Avalonia 特性使用面（决定重写成本）：样式选择器与伪类（`Button.cap:pointerover /template/`）、
`Classes.completed`、`VirtualizingStackPanel`、`DrawingContext.Render`、`ShowDialog<T>`、
`StorageProvider`、`TopLevel`、`AvaloniaResource`、`Avalonia.Headless`。

## 三、MewUI 能力对照

| 需求 | MewUI | 说明 |
|---|---|---|
| UI 虚拟化 | ✔ | `ListBox`/`TreeView`/`GridView`/`ComboBox` 共用 `VirtualizedItemsPresenter`，容器复用 + 回收 |
| 表格 | △ | 有 `GridView`（列模板 + 行容器），**无 DataGrid**，无列排序/列宽拖拽。本项目本来就是 `ListBox` + 行模板，够用 |
| 自绘 | ✔ | `IGraphicsContext` / `IGraphicsFactory`，`SegmentBar` 直接迁移 |
| 样式 | △ | `Style` + `StateTrigger(VisualStateFlags)` + `StyleSheet.Define(名字/类型)`；**没有 XAML 选择器与模板部件选择**，`/template/` 那套要改成重写 `Template` |
| 主题 | ✔ | `Theme.Palette` + `Func<Theme,T>` setter，可热切换；现有 `#1F2B3B` 等色值映射到自定义 Theme |
| 数据绑定 | ✔ | `ObservableValue<T>` / **INPC ViewModel** / `MewProperty` 三类数据源混用，弱订阅 |
| 命令 | ✗ | **没有 `System.Windows.Input.ICommand`**：`Command`（身份+呈现）+ `CommandScope.Register` + `InputMap`。需一层 ~40 行桥接 |
| 无边框窗口 | △ | `Borderless()` / `ExtendClientAreaTitleBarHeight(h)` 都有，但**没有 `DragMove`**，标题栏拖动要自己接（见风险 1） |
| 对话框 | ✔ | `ShowDialogAsync(owner)` + `StartCenterOwner`；`MessageBox`、`FileDialog`（默认原生） |
| 平台菜单 | △ | `ContextMenu`/`MenuBar` 是 **in-window popup**，不是系统菜单；列表项右键与输入框中文菜单要实测 |
| 键盘 | ✔ | `InputMap` + `KeyGesture`/`KeySequence`；`StandardCommands` 由 TextBox 族自带 |
| 无头测试 | ✔ | 有 `docs/Headless.md`，可平移现有测试思路 |
| NativeAOT | ✔ | 首要目标；只引 Direct2D 后端包，发布产物里不带 Gdi / MewVG |

## 四、目标结构

```
Xdm.Core                       引擎（不变）
  └── Xdm.Ui.Shared            ViewModels / Services / Formatting（阶段 0 抽出，命名空间不变）
        ├── Xdm.Ui             Avalonia 视图（现状，保留）
        │     └── Xdm.App      Avalonia 壳 + SimdAIO（现状，保留）
        └── Xdm.Ui.Mew         MewUI 视图（新）
              └── Xdm.App.Mew  Win32 + Direct2D 壳（新）
```

- 包：**不引 `Aprillz.MewUI.Windows` 元包**（它把 Gdi、MewVG 一起拖进产物，`MewUIBackend` 的发布期
  裁剪只会移掉两个后端 dll，仍残留 `Aprillz.MewVG.Core` + `Aprillz.MewVG.GL`）。改为：
  `Xdm.Ui.Mew` 引 `Aprillz.MewUI.Core`（控件层），`Xdm.App.Mew` 引
  `Aprillz.MewUI.Backend.Direct2D` + `Aprillz.MewUI.Platform.Win32`，版本在
  `Directory.Packages.props` 里锁死。实测 publish 的 MewUI 相关 dll：2604 KB（三个），
  meta 方案 2800 KB（多出 MewVG.Core 115 KB + MewVG.GL 81 KB）。
- 启动：`Win32Platform.Register(); Direct2DBackend.Register();` 必须在 `Application.Run` 之前，
  否则第一个量文本的控件处抛 `InvalidOperationException`。
- 构建：`build.ps1` 增加 `-Ui` 参数（`avalonia` 默认 / `mew`），产物分别落
  `publish\win-x64-aot` 与 `publish\win-x64-aot-mew`，便于体积/启动对比。

## 五、阶段

### S0 抽 `Xdm.Ui.Shared`（0.5–1 天）✅ 已完成

- 平移 `ViewModels/`、`Services/`、`Formatting/`，**命名空间保持 `Xdm.Ui.*` 不变**，
  工程只引 `Xdm.Core`；`Xdm.Ui` 改 csproj 引用它，`Assets/` 与视图留在原处。
- 验收：现有 199 测试全绿、Avalonia 前端行为零变化（这条是 A/B 两方案共同的前置）。

### S1 Spike：空壳 + 自绘标题栏（1–2 天）✅ 已完成

`src/Xdm.Ui.Mew/SpikeWindow.cs`（S5 已删除）+ `src/Xdm.App.Mew/Program.cs`，`Aprillz.MewUI.Core` +
`Aprillz.MewUI.Backend.Direct2D` + `Aprillz.MewUI.Platform.Win32` 0.22.1。

| 验证点 | 结果 |
|---|---|
| `Borderless` + 30px 自绘标题栏拖动 | ✔ 不需要 `ExtendClientAreaTitleBarHeight`：标题栏 `MouseDown` 里 `ReleaseCapture()` + `SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0)`，实测窗口位移 (+120,+72) 与模拟鼠标位移完全一致，由窗口管理器接管（双击最大化、贴边吸附随之免费获得） |
| Direct2D 中文 | ✔ 13px / 11px 两行、全角标点、`·` 分隔符都清晰，无字体回退或方块 |
| 1000 行虚拟化 | ✔ 只创建可视区容器（bind 计数远小于 1000）；滚 30 格到 `文件_0034` 区间内容正确、无撕裂；**必须** 显式 `ItemHeight` |
| 列表项右键菜单 | ✔ 定位在鼠标右下方，中文正常；是**独立分层窗口**（`PrintWindow` 抓主窗口抓不到、全屏可见），S2 的截图与 Headless 测试都要按「popup 不是主窗口子元素」处理 |
| DPI | 进程 `PerMonitorAware`，窗口 dpi=168（175%）；但 `Resizable(960,640)` 落地成 **960 物理像素**（549 DIP），而布局与字号是 DIP 语义 |

**实机踩到的坑（S2 直接照此写，不要重踩）**

1. `ListBox`/`TreeView` 的 `ItemHeight` 是**行高真值**，模板里 `Border.Height(44)` 不参与行高计算；
   不给 `ItemHeight` 时行与行会重叠（默认行高远小于 44）。
2. `Content`、`Borderless` 是**属性**不是方法；链式只存在于 `ElementExtensions`/`ControlExtensions`。
3. 行模板里的文字用 `TextBlock`：`Label` 放进模板后两个元素会叠到同一坐标（换 `TextBlock` 即正常）；
   `Label` 留给标题栏/状态栏这类常规面板。
4. 模板写法：`build: ctx => { ctx.Register("name", el); ... }`，`bind:` 用 4 参重载
   `(FrameworkElement root, T item, int index, TemplateContext ctx)`，取元素 `ctx.Get<TextBlock>("name")`。
5. `.Padding()` 只对 `Panel` 有效，`TextBlock`/`Label` 要套一层 `StackPanel`。
6. `SetScrollOffsets`、`VisitRealizedContainers` 等容器级 API 不是 public：滚动只能靠真实输入或
   `ScrollViewer`，自动化测试要按 MewUI Headless 的方式做。
7. `Window.Loaded` 是事件（`+=`），没有 `OnLoaded` 扩展方法（那是 `Control` 的）。
8. `ContextMenu` 用 `.ContextMenu(menu)` 绑定，菜单项是 `MenuItem.Text` + `Command`
   （**没有 `OnClick`**）→ 印证了 S4 的命令桥必要性。
9. ~~窗口尺寸按物理像素：175% 屏上照抄 960×640 只有 549 DIP 宽~~
   **S2 更正**：`WindowSize` 是 DIP 语义（`InitSize`/`Resizable`/`SetSize` 均 DIP），
   S1 看到的「960 物理像素」是 DPI 不感知的进程读到虚拟化坐标导致的误判。

结论：**四项硬点全部通过，可以进 S2。** 风险 1（拖不动）与风险 5（字体）可以从风险表里划掉。

### S2 主窗口（3–4 天）✅ 已完成

工具栏（新建/暂停/继续/删除/设置）、分类栏、任务列表（`DownloadRow` 模板 + `SegmentBar` +
徽章状态）、空态、状态栏、搜索过滤、拖入 URL、快捷键（`Del`/`Ctrl+N`）。
验收：1000 行滚动流畅；选中/悬停/行内按钮命中；删除走确认框。

**已完成**：`src/Xdm.Ui.Mew/MainWindow.cs` 全量重写——自绘标题栏（拖动/三键）、工具栏（左四钮 +
居中搜索框 + 右三钮）、分类栏（四项 + 计数 + 选中高亮）、任务列表行模板（扩展名徽标 / 文件名 /
`SegmentBar` / 进度与详情 / 大小 / 速度与剩余 / 状态徽章 / 暂停·目录·删除）、空态提示、状态栏；
`Del` 删除、`Ctrl+N` 新建；`XDM_DEMO=<n>` 造演示数据（`0` 验空态）。实机截图核对通过。

**S2 右键菜单与命令桥（已接通）**：`ListBox.ContextMenu(menu)` + 四个 `MenuItem`（暂停继续/打开目录/
复制链接/删除），命令读 `ViewModel.SelectedRow`；右键按下先把该行设为选中。菜单项只能挂 `Command`，
处理器注册在窗口的 `Commands`（`CommandScope`）上——这就是 S4「命令桥」的雏形，两个按钮组以后可复用。
拖入 URL 已实现（`AllowDrop` + `DragOver`/`Drop` → 按行切分 http(s) 链接建任务），
但**自动化验证不了**：用 PowerShell + WinForms `DoDragDrop` 模拟 OLE 拖放时目标端一个事件都收不到，
只能是真机手工拖一次才算验过。

**S2 已收尾**：删除前确认框接 `Dialogs/ConfirmDialog`（未完成提示临时文件、已完成带「同时删除已下载的文件」勾选）、
搜索框右侧 `✕` 清空按钮（有输入才显示）、「关于」接 `Dialogs/AboutDialog`、「设置」接 `Dialogs/SettingsDialog`。
删除后分类计数不刷新也一并修掉：`CategoryViewModel.PropertyChanged(Count)` → 重建 `ItemsView` 并恢复选中
（实机 4→3）。搜索过滤/清空、设置四页签、确认框、关于框均已截图核对。

**S2 实机踩坑（比 S1 更隐蔽，全部有实测定位）**

1. **`DockPanel` 的 `DockLeft/DockRight` 在无填充子元素时不生效**：标题栏三键、状态栏右侧路径
   全挤到左边。改用 `Grid.Columns("*,Auto")` / `("Auto,*,Auto")` 显式列定位。S1 记的
   「`DockTop/DockLeft` 无效」不是个案，直接弃用 `DockPanel`。
2. **`WindowSize` 全程是 DIP**（S1 坑 9 的结论要改写）：`Resizable(1000,620)` 落地 1750×1085 物理
   像素、dpi=168，布局与字号都正确。之前 `×DpiScale` 的写法把窗口放到 1750 DIP（3062 px），
   内容右半整块跑到屏幕外。`StartupLocation = CenterScreen` 即可，**不要再手工 `MoveTo`/`SetWindowPos`**。
3. **列表条目容器铺满整行，但按模板根的 `DesiredSize` 摆放内容**：带星号列的 `Grid` 测量时星号列
   只算内容宽度，整行于是只有 ~600 DIP，右侧定宽列被摆到行外（截图里就是「右列整块不见了」）。
   解法：`Controls/RowHost.cs`（`Grid` 子类）把测量宽度报到 `availableSize.Width`。
4. **分类栏不能用 `RowHost`**：条目容器的可用宽度会沿用上一轮的期望宽度、越测越宽（实测 236 DIP
   而视口只有 178），星号列反而把计数顶到栏外。分类栏宽度固定，直接 `.Width(CategoryPaneWidth-12)`。
5. **`ListBox.HorizontalScroll = ScrollMode.Auto` 会常驻一条滚动条**（浮点取整的亚像素溢出），
   两个列表都设 `Disabled` 才能干净。
6. **`Control` 默认 `MeasureOverride` 返回 0**：自绘控件（`SegmentBar`）不写 `MeasureOverride`
   就完全不显示；`OnRender` 里必须用 `Bounds.X/Y` 当原点，`FillRectangle(new Rect(0,0,...))`
   画在控件左上角的父坐标系外。
7. **截图要用 DPI 感知的进程**：PowerShell 不声明 DPI 感知时 `GetWindowRect`/`PrintWindow` 拿到的是
   虚拟化后的 1000×620，只能抓到窗口左上 57%，右侧列「看起来缺失」全是假的。脚本里先
   `SetProcessDpiAwarenessContext(-4)`。
8. **行内按钮用 `Tag` 回填 + `OnClick` 里读 `Tag`**（模板 build 时无数据），不要自引用闭包变量。
9. **弹出菜单必须显式 `SetCommandTarget(CommandTarget.From(Commands))`**：`ContextMenu` 是分层窗口里的
   独立元素树，从它自己往上找不到窗口的命令作用域，不设就直接「点菜单项毫无反应」（实测右击→点第一项
   前后状态栏数字不变，设完再点就正常）。另外菜单**拿不到键盘焦点**：`Down`/`Enter` 会被下面的
   `ListBox` 吃掉（表现为选中行往下跳），只能鼠标点。
10. **`AllowDrop` 是逐元素命中**：只给 `Window` 设，指针落在 `ListBox` 上时收不到 `DragEnter/Drop`
   （实测 stderr 一条日志都没有）；列表自己也要 `AllowDrop = true`。

### S3 对话框（2–3 天）✅ 已完成

新建下载、设置（`TabControl` 四页）、关于、确认框（含「同时删除已下载的文件」勾选）；
文件夹选择走 `FileDialog`。验收：`ShowDialogAsync` 模态正确、`CenterOwner`、
取消/回车/ESC 与现有行为一致。

**已完成**：`Dialogs/` 下四个对话框全部落地，统一「自绘 30px 标题栏 + 1px 边框 + `Borderless` +
`StartupLocation.CenterOwner`」，与主窗口同一套 `WindowDrag.Begin` 拖动：

| 文件 | 形态 | 截图核对 |
|---|---|---|
| `ConfirmDialog.cs` | 620×?，正文 + 可选勾选「同时删除已下载的文件」，返回 `record struct ConfirmResult(Confirmed, OptionChecked)` | 删除第三行 → 确认 → 行数 4→3、状态栏与分类计数同步 |
| `AboutDialog.cs` | 460×300，版本号读 `AssemblyInformationalVersionAttribute` 去掉 `+commit` | 「关闭」按钮此前被裁，高度 268→300 后完整 |
| `NewDownloadDialog.cs` | 580×402，链接 / 保存到（`FileDialog.SelectFolder`）/ 文件名 / 分段数（`NumericUpDown`）/ 立即开始 | 填链接 → 添加 → 列表新增一行、分类与状态栏 2→3 |
| `SettingsDialog.cs` | 620×570，`TabControl` 四页（常规/连接/目录/网络）+ 保存校验 | 四页布局、代理单选联动、空目录保存报「默认保存目录不能为空」并保持打开 |

校验一律复用共享层 `NewDownloadViewModel.TryCreate` / `SettingsViewModel.TryBuild`，MewUI 侧只做
「控件值 → ViewModel」回灌，不复制规则。`MainWindow` 提供 `Settings`（`AppSettings`）与
`SaveSettings`（`Func<AppSettings, string?>`）两个挂点，S5 接 `ISettingsStore` 时只填这两个。

**S3 实机踩坑**

11. **`ComboBox` 在自绘弹层里不响应鼠标**：设置页的代理模式原本用 `ComboBox`（`.Items([...])` +
    `.SelectedIndex(0)`），点击正文区与右侧箭头都不展开、`Down`/`Enter` 也无效（四轮截图复核，
    stderr 无异常）。改成三个 `RadioButton`（`.GroupName("proxy")` + `.IsChecked` +
    `.OnCheckedChanged`）后一次通过。**结论：设置类单选先用 RadioButton，不要指望 ComboBox。**
12. **`TabItem.Header` 必须给元素，不能只给 `HeaderText`**：`TabControl.AddTab` 在 `Header` 为
    `null` 时抛 `TabItem.Header must be set`（设置窗口一闪即溃）。写 `.Header(new TextBlock().Text(...))`。
13. **截图坐标不能凭肉眼在缩放后的预览图上量**：读图工具会把 1750px 的 PNG 缩成 1080px 显示，
    直接拿预览图坐标当物理像素点，误差 1.6 倍——表现为「点了没反应」，其实是点在按钮下方 30 DIP。
    正确做法：用 `EnumWindows` 找到对话框自己的 `GetWindowRect`，再按 `GetDpiForWindow/96` 把 DIP
    布局坐标（如「添加」在 580×402 内容里的 522,372）换算成绝对屏幕坐标。
    文本框那次「看似点中」是因为对话框打开时焦点本来就在第一个输入框，键盘输入掩盖了鼠标根本没命中。

### S4 视觉对齐与命令桥（2 天）✅ 已完成

- `MewCommandBridge`：把 `System.Windows.Input.ICommand` 适配为 MewUI `Command` +
  `Register`，`CanExecuteChanged` → `window.RequerySuggested()`；`RelayCommand` 本身不动。
- 主题：深色标题栏、badge、按钮 hover/pressed、列表选中色按 `UI-DESIGN.md` 映射。
- 输入框中文右键菜单（撤销/剪切/复制/粘贴/全选 + 快捷键提示）照 `EditTextBox` 的逻辑重写。
- 图标：`logo.png` 透明版直接 `Image`；标题栏三键用矢量形状。

**已完成**

| 项 | 落地方式 | 截图核对 |
|---|---|---|
| 命令桥 | `MewCommandBridge.Connect(window, ICommand, id, text)` → `Commands.Register(execute, canExecute)`，源 `CanExecuteChanged` 转 `RequerySuggested()` | 未选中行时「恢复/暂停/删除」置灰；选中第三行点「暂停」→ 行变已暂停、状态栏 `1 个下载中` |
| 行级刷新 | `RowBinding`（模板控件组）随容器复用改写；行 `PropertyChanged` → `ApplyRow` 就地重刷，配合 `Rows.CollectionChanged` 订阅/退订 | 暂停后行内徽标、按钮文字、速度/剩余即时更新（此前要点菜单才碰巧重绑） |
| 中文右键菜单 | `Controls/EditBox`（`Create` / `AttachMenu<T> where T : TextBase`）挂 `StandardCommands` + `SetCommandTarget(CommandTarget.From(box))` | 搜索框输入 `hello` → 右键五项（自带 Ctrl+Z/X/C/V/A 提示）→ 全选 + 剪切后文本清空 |
| 标题栏 | `#1F2B3B` + `#2C3A4E` 底线；18px logo（嵌入 `xdm-logo.png`）；三键改 `PathShape` 矢量字形，hover 浮出色块、关闭键 `#C42B1C` | 关闭键 hover 变红；点最大化 → 铺满屏幕且中间键换成「还原」双框 |
| 输入框统一 | 搜索框、新建下载三框、设置保存目录/代理地址、请求头多行框全部走 `EditBox` | 设置四页与保存校验行为不变 |

**S4 实机踩坑**

14. **`Button.Background` 压不住默认样式**：标题栏三键一开始用 `Button` + `Background`，渲染出来的仍是
    默认灰块，与标题栏色差明显。改用 `Border` 自绘（`Background` + `MouseEnter/Leave` 换色）后一次通过。
    **结论：需要精确控制底色/hover 的地方不要用 `Button`。**
15. **`Border` 的 `MouseUp` 不可靠**：`MouseDown` 里发 `WM_NCLBUTTONDOWN` 被标题栏接管后，按钮的
    `MouseUp` 收不到，点最大化毫无反应。改成 `MouseDown` 直接触发（Windows 标题栏按钮的实际区别用户无感）。
16. **行模板没有属性绑定**：`ItemTemplate` 的 `bind` 只在容器创建/复用时调用一次，`DownloadRowViewModel`
    的属性变化（暂停、进度、速度）不会重绘。必须自己维护「行 → 控件组」映射就地重刷；
    用 `Rows.CollectionChanged` 管理订阅，避免删除的行被长期持有。
17. **`PathShape` 必须显式 `StrokeThickness`**：只设 `Stroke` 时线宽为 0，字形完全不可见（三个空按钮就是
    这么来的）；`PathGeometry` 在 `Aprillz.MewUI.Rendering`，不是 `Aprillz.MewUI`。
18. **`StandardCommands` 自带快捷键提示**：`ContextMenu` 里挂 `StandardCommands.Undo/Cut/Copy/Paste/SelectAll`
    会连右侧 `Ctrl+Z` 一起画出来，不需要自己拼字符串；但命令目标必须 `SetCommandTarget`，否则菜单点不动。

### S4.5 引擎接线（1 天）✅ 已完成

此前界面只跑 `XDM_DEMO` 假数据。本轮把 `Xdm.Core` 的真实组件装进 `Xdm.App.Mew` 组合根，
MewUI 侧不重写任何业务（`TaskStore` / `DbSettingsStore` / `DownloadManager` / `AppPaths` 全部复用）：

| 文件 | 职责 |
|---|---|
| `MewRuntime.cs` | 组合根：`AppPaths.Resolve` → `TaskStore` → `DbSettingsStore`（先 `ImportLegacyJson`）→ `SpeedLimiter` + `RangeHttpClient` + `DownloadManager` → `MainWindowViewModel`；`SaveSettings` 先落库再套用（落库失败只回错误文本，不改内存态） |
| `MewDownloadActions.cs` | `IDownloadActions` → `DownloadManager` |
| `MewDownloadBridge.cs` | 引擎事件 → UI 线程：新增/删除立即投递，进度按 250ms 合并成 4Hz |
| `Shell.cs` | 行内「目录」按钮打开资源管理器 |
| `Program.cs` | 平台注册 → 运行时装配（失败走 `NativeMessageBox` + 退出码 2/3）→ `Application.Run(window, ConnectEngine)` |
| `MainWindow` | `ConfirmOnExit` + `Closing` 确认（有任务在跑时提示「进度会保存」），新增 `TitleBarChrome` 统一四个对话框的标题栏 |

`XDM_DEMO` 语义：**设置即演示**（含 0 验空态），未设置才建库 —— 验证脚本不会污染真实库。

**实测**（本地 `HttpListener` 提供 256KB + Range 的源）

| 场景 | 结果 |
|---|---|
| 首次启动（新库） | stderr `[startup] db=... demo=False`，状态栏显示真实库路径，空态提示正常 |
| 新建下载 → 下载完成 | 行内 `100%`、`完成于 22:27`、徽标 `已完成`；左侧 `全部 1 / 已完成 1`（顺带修掉 `UpdateRow` 不刷计数） |
| 杀掉进程后重启同库 | 任务从库恢复，元数据（大小/完成时间）一致 |
| 设置改「默认保存目录」→ 保存 → 重启 | 目录页回显新值；新建下载对话框「保存到」「分段数」随之取设置值 |
| 点标题栏 ✕ | 弹「退出」确认框，取消后留在主窗口 |
| 演示模式回归 | `XDM_DEMO=2` 下暂停/恢复、命令桥、行内刷新与 S4 一致 |

**S4.5 实机踩坑**

19. **`DispatcherTimer` 不能在 `Application.Run` 之前建**：桥接器一开始在 `Main` 里构造，定时器拿不到
    消息循环的 UI 线程。改用 `Application.Run(window, startup)` 的 `startup` 回调建，
    `Post` 走 `Application.Current.Dispatcher.IsOnUIThread` 判断后再 `BeginInvoke`。
20. **`MainWindowViewModel.UpdateRow` 不刷分类计数**：假数据永远不改状态，接真引擎后立刻暴露
    （下载完仍显示 `已完成 0`）。改成「状态变化才 `RefreshCounts()`」，避免每帧遍历全部行。
21. **对话框的 ✕ 是 `Button`，在深色标题栏上是个浅色方块**：四个对话框各写了一份同样的标题栏代码。
    抽成 `Controls/TitleBarChrome`（`Border` + `PathShape`），主窗口与对话框共用同一套字形与 hover 色。
22. **`NewDownloadDialog` 没接设置里的默认目录/分段数**：Avalonia 版由 `NewDownloadWindow.SaveFolder`
    预设，MewUI 侧漏了，对话框永远用 `%USERPROFILE%\Downloads`。补 `Show(owner, initialUrl, saveFolder, segments)`。

### S5 测试与发布（1–2 天）✅ 已完成

- ✅ 新建 `tests/Xdm.Ui.Mew.Tests`（**42 条**，不是计划的 15–20 条：行为类断言可以平移，
  控件树断言在不 Show 的前提下也做得成，见下方坑 23–24）。
- ✅ 当时加了 `build.ps1 -Ui mew`；**2026-11-22 删掉 Avalonia 后该开关已移除**，
  产物固定 `publish/win-x64{,-aot}/xdm.exe`。
- ✅ 体积/首帧/内存实测（3 次取中位，与 Avalonia 同档对比）进 `PROGRESS.md`。
- ✅ 文档：`ARCHITECTURE.md §2.1/2.2/2.4`、`UI-DESIGN.md §12`、`README` 命令与工程结构。

**MewUI 测试工程的组成**

| 文件 | 覆盖 |
|---|---|
| `MainWindowTests` | 标题、自绘 chrome、logo、状态栏库路径、工具栏命令进 `CommandScope`、空态提示、删除确认接线、`ConfirmOnExit` 默认关 |
| `MewCommandBridgeTests` | 注册 / 注销 / 转发执行 / 可用性跟随选中 / `CanExecuteChanged` 无消息循环不炸 |
| `DialogChromeTests` | 四框共用标题栏（标题 + 单个关闭字形）、确认框文案与复选框 |
| `NewDownloadDialogTests` | 设置默认目录/分段数落位、越界夹取、`Submit` 建请求、多链接、非法 URL、目录不存在 |
| `EditBoxTests` | 中文五项 + 映射到 `StandardCommands` + `CommandTarget` 可解析 |
| `MewThemeTests` | 配色/尺寸 token 钉死（防样式漂移） |
| `LayoutTests` | 300 行时元素树 < 100 节点（MewUI 的虚拟化等价断言）、行高、空列表布局 |

**S5 实机踩坑**

23. **MewUI 控件不是线程安全的**：xunit 默认按类并行，两个测试同时构造控件树会互相踩坏
    （`RefreshInheritedSubtree` 抛 `ArgumentOutOfRangeException`）。测试工程加
    `[assembly: CollectionBehavior(DisableTestParallelization = true)]`。
24. **没有 Headless 宿主**：控件在 `Show()` 之前是普通对象，可以构造与断言；
    但 `Measure/Arrange` 仍要有渲染后端 —— 测试里按需 `Win32Platform.Register()` +
    `Direct2DBackend.Register()`，失败就 `Assert.Skip`，不能让无 GPU 的机器整片红。
25. **文案有两种藏法**：按钮/复选框的文本在 `AccessText.RawText`（MewUI 内部类型，只能反射读），
    输入框值在 `TextBox.Text`；只收 `TextBlock.Text` 会莫名「找不到文案」。
26. **`ListBox` 不建行元素**：行是画出来的（`TryGetItemBounds` 的存在就是证据），
    所以「容器数量」断言没有意义，改成「300 行时元素树仍是百级以内」；
    行内实时刷新只能靠端到端截图验收。

合计 **10–14 人日**（AI 协助下按阶段推进，实际日历时间会更短）。

## 六、风险与兜底

| # | 风险 | 兜底 |
|---|---|---|
| 1 | ~~无 `DragMove`，自绘标题栏拖不动~~ | **S1 已解决**：`ReleaseCapture` + `WM_NCLBUTTONDOWN/HTCAPTION`，实测可用 |
| 2 | API 在 0.x 之间破坏性变更 | 锁 0.22.1；升级当独立任务做，升级后跑 S1 的四个验证点 |
| 3 | 无 `ICommand`，`CanExecute` 改为按 dispatcher turn 重求值 | 桥接层把 `CanExecuteChanged` 转成 `RequerySuggested()`；状态变更后立即调用 |
| 4 | `ContextMenu` 是独立分层窗口（贴边、焦点、IME 需实测） | **S1 已确认是独立窗口**：S2 先验贴边翻转与 IME；不行改 `Popup` + 手动定位 |
| 5 | ~~中文字体在 Direct2D 下渲染异常~~ | **S1 已解决**：13/11px 中文与标点清晰，不换后端 |
| 6 | 视觉与 Avalonia 版不完全一致 | 不追求像素级一致，只对齐 `UI-DESIGN.md` 的色值/尺寸/间距规范 |
| 7 | ~~窗口尺寸是物理像素，DIP 设计在 175% 屏上缩水~~ | **S2 已解决**：`WindowSize` 本来就是 DIP，S1 坑 9 的结论作废；不要再手工乘 `DpiScale` |

## 七、明确不做

- 不做 DataGrid 级能力（列排序、列宽拖拽、单元格编辑）——MewUI 没有，现有界面也不需要。
- 不引 `MewDock` / `MewCharts` / `WebView2` / `Skia` 扩展包。
- 不在 MewUI 侧重写引擎接线（`DownloadManager`、`TaskStore`、`AppPaths` 一律复用）。
- 不为了「统一」而回头删 Avalonia 前端；是否切换主推前端，等 S5 出数据后再定。
