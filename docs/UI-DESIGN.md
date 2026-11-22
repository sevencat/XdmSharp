# 界面设计规范

界面先行的落地依据。M1 全部用假数据实现本文件；M4 只替换数据来源，不改布局。

## 1. 设计基调

- 组件库：Fluent 主题，由 `Sevencat.Avalonia.SimdAIO` 自带的 `Avalonia.Themes.Fluent.dll` 提供
  （独立程序集身份，App 不要再引官方 `Avalonia.Themes.Fluent` 包）。不做重型自定义换肤。
- 风格：信息密度优先（类 IDM / XDM 桌面端），行高 56，字号 12–13，圆角 4。
- 明暗主题跟随系统，两套都用 Fluent 的 `ThemeVariant`，额外定义 4 个语义色资源：
  `ProgressBrush`（主色）、`SuccessBrush`、`WarnBrush`、`ErrorBrush`。

## 2. 窗口清单

| 窗口 | 文件 | 首期 | 说明 |
|---|---|---|---|
| 主窗口 | `Views/MainWindow.axaml` | ✔ | 工具栏 + 分类 + 列表 + 状态栏 |
| 新建下载 | `Views/NewDownloadWindow.axaml` | ✔ | 多行 URL、保存到、分段数、文件名 |
| 进度详情 | `Views/DownloadDetailWindow.axaml` | ✔ | 分段可视化、速度曲线、日志 |
| 设置 | `Views/SettingsWindow.axaml` | ✔ | 常规 / 连接 / 目录 / 网络 四页 |
| 关于 | `Views/AboutWindow.axaml` | ✔ | 版本、许可（GPL-3.0）、参考实现声明 |
| 完成通知 | `Views/CompletionToast.axaml` | M6 | 右下角气泡，可"打开文件/打开目录" |
| 删除确认 / 重名冲突 | `Views/ConfirmDialog.axaml` | ✔ | 通用确认框，勿各写一套；支持一个可选勾选项（删除任务的「同时删除已下载的文件」） |

所有输入框（搜索、URL、设置项）用 `Controls/EditTextBox`，而不是原生 `TextBox`：主题自带的右键菜单是
英文 Cut/Copy/Paste，`EditTextBox` 换成「撤销 / 剪切 / 复制 / 粘贴 / 全选」并在弹出时按选区置灰。
它是 `TextBox` 子类且 `StyleKeyOverride => typeof(TextBox)`，样式与行为完全沿用父类型。

以上文件均在 `src/Xdm.Ui/Views/`，命名空间 `Xdm.Ui.Views`（XAML 写 `x:Class="Xdm.Ui.Views.MainWindow"`）。

所有窗口统一 `WindowDecorations="BorderOnly"` + 自绘 `Controls/TitleBar`（见 §11），
不再使用系统标题栏；窗口 `Title` 仍保留（任务栏、Alt+Tab、Headless 断言依赖它）。

## 3. 主窗口

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ▣ XDM Sharp                                        ─   □   ✕                │ 标题栏 30px（自绘，深色）
├──────────────────────────────────────────────────────────────────────────────┤
│ [＋ 新建] [▶ 恢复] [⏸ 暂停] [🗑 删除] │ 🔍 搜索   [⚙ 设置] [ⓘ 关于] [⇥ 退出] │ 工具栏 40px
├───────────────┬──────────────────────────────────────────────────────────────┤
│ 全部 (128)    │ 文件名                    │ 大小    │ 进度        │ 速度  │ 剩余 │
│ 正在下载 (3)  │ ──────────────────────────────────────────────────────────── │
│ 已完成 (120)  │ ubuntu-24.04.iso          │ 5.7 GB  │ ███████░░░  │ 24MB/s│ 1:12 │
│ 失败 (5)      │ 76%  3/12 段  目标: D:\Downloads                             │
│               ├──────────────────────────────────────────────────────────────┤
│               │ report.pdf                │ 12 MB   │ ██████████ │ —     │ —    │
│               │ 已完成  2026-11-22 10:31  双击打开                            │
├───────────────┴──────────────────────────────────────────────────────────────┤
│ 3 个下载中 · 总速度 24.1 MB/s · 限速 关闭 · 剩余空间 128 GB                    │ 状态栏 24px
└──────────────────────────────────────────────────────────────────────────────┘
```

- 左栏固定 180px，可折叠；列表区用**虚拟化**容器。
- 列表技术选型（**已定案**）：`ListBox` + `ItemTemplate` + `VirtualizingStackPanel`。
  **不引 `Avalonia.Controls.DataGrid`**：两行式行模板本来就不吃列布局，且 `ListBox` 视觉树更轻、
  虚拟化行为可预期、行内自绘（`SegmentBar`）更容易控制。列排序/列宽拖拽不是首期需求，
  将来真要加就在 `ListBox` 之上做排序（ViewModel 排序 + 重建集合），不要把 DataGrid 拉回来。
- 虚拟化参数：`VirtualizingStackPanel.VirtualizationMode` 保持默认回收模式；
  `ScrollViewer.IsDeferredScrollingEnabled` 关闭；行模板不要包 `ScrollViewer`/`Border` 嵌套层级过深。
- 工具栏右侧：[⚙ 设置] [关于] [退出]，`Button.tool` 样式。**退出必须确认**：
  `MainWindow.ConfirmOnExit = true`（组合根设置）时，点「退出」与关窗口（标题栏关闭键 / Alt+F4）
  都走 `ConfirmDialog`，文案带进行中任务数（`MainWindowViewModel.ActiveCount`），
  确认后才真正 `Close()`；取消则窗口保持。无头测试不设该开关，避免模态框挂住用例。

### 3.1 按钮图标

所有带文字的按钮统一「**12px 矢量图标 + 5px 间距 + 文本**」，不用 emoji 字形
（`⚙` / `🗑` 这类彩色字形在 125%/175% DPI 下发糊，且依赖 Segoe UI Emoji 的渲染差异）。

| 按钮 | 图标（Material Design Icons，24×24 viewBox） |
|---|---|
| 新建 | `plus` |
| 恢复 | `play` |
| 暂停 | `pause` |
| 删除 | `delete`（垃圾桶） |
| 设置 | `cog` |
| 关于 | `information`（圆 i） |
| 退出 | `logout`（门 + 右箭头） |
| 取消 | `close`（✕） |
| 关闭 | `close`（✕，与「取消」同款；关于窗口、设置窗口） |
| 确定 | `check`（✓） |
| 标题栏最小化 / 最大化 / 关闭 | 见 §11.1，1px 线宽几何，与系统窗钮同观感 |

实现约束：

- 图标用 `PathIcon`（`Data` 直接写 path 字符串，`Stretch="Uniform"` 默认）。
  `PathIcon.Fill` 取自继承的 `Foreground`，所以按钮禁用时图标自动变灰——**不要**改成
  `Path` + 固定 `Fill`，那样禁用态图标仍是亮的。
- 按钮 `Content` 变成 `StackPanel` 后，UIA 名称不再是文本，**必须**显式设
  `AutomationProperties.Name="暂停"` 之类；`ConfirmDialog` 在构造里同步设置（按钮文案可换）。
- 行内悬停按钮（`DownloadRow` 的暂停/目录/删除）保持纯文字 11px：行高只有 56，
  加图标会挤，且它们是次要操作。

## 4. 列表行规范（`Controls/DownloadRow.axaml`）

两行式，高 56，内边距 12/8：

| 区域 | 内容 | 说明 |
|---|---|---|
| 左图标 24 | 文件类型图标（按扩展名）+ 状态角标 | 下载中角标为旋转箭头，暂停为 ⏸，失败为 ⚠ |
| 第一行 | 文件名（省略号截断，Tooltip 全名） | 字号 13，`TextTrimming=CharacterEllipsis` |
| 第一行右 | 大小 / 已完成时间 | 右对齐，次要色 |
| 第二行 | 进度条 + 百分比 | 进度条高 4，圆角 2；完成态隐藏 |
| 第二行中 | `x/y 段 · 目标文件夹` | 次要色 11px |
| 第二行右 | 速度 + 剩余时间 | `24.3 MB/s` / `约 1 分 12 秒` |
| 悬停 | 右侧浮现 [暂停/恢复] [目录] [删除] 三个图标按钮 | 悬停才显示，避免视觉噪声 |

状态徽标色：

| 状态 | 颜色 | 文案 |
|---|---|---|
| 下载中 | `ProgressBrush` | `下载中`（百分比与段数在第二行：`76%  3/12 段`，徽标不重复） |
| 暂停 | 次要灰 | `已暂停` |
| 连接中 | 次要灰 + 呼吸 | `正在连接…` |
| 完成 | `SuccessBrush` | `已完成` |
| 失败 | `ErrorBrush` | `失败：连接被重置` |
| 磁盘错误 | `WarnBrush` | `磁盘空间不足` |

进度刷新约束：`Progress` 值变化 ≥ 1% 才更新绑定；文字（速度/剩余）250ms 一次。
自定义 `SegmentBar`（分段条）用 `DrawingContext` 直绘，不复用 `ProgressBar` 控件（减少视觉树与布局开销）。

### 4.1 多协议 / 多文件的行呈现（V2 HF、V3 BT 预留）

首期只有单文件任务，但行模板与 ViewModel 现在就要兼容两级结构，否则 V2 会推翻列表：

| 项 | 首期表现 | V2/V3 表现（同一模板） |
|---|---|---|
| 父行（模型包 / torrent） | 不出现（任务自己就是顶层） | 文件名 = `repo` 或 torrent 名；进度 = Σ 子文件；第二行中显示 `37/128 文件` |
| 子行 | 不出现 | 缩进 24px，字号 12，只有文件名 + 进度 + 速度；默认折叠 |
| 图标 | 按扩展名 | 父行用来源图标（HF / 磁力），子行同扩展名 |
| 详情窗口 | 分段条 | 父任务显示"文件网格"（每格一个文件），点格子进子任务的分段条 |
| 徽标文案 | `下载中` | 父行同样 `下载中`，文件数放第二行（`37/128 文件`） |

约束：`DownloadRowViewModel` 的字段（`Progress` / `SizeText` / `SpeedText` / `StatusText` /
`IsParent` / `Children`）首期就定义全，子集合为空即可 —— 这样 V2 只改数据装配，不改 XAML 骨架。

## 5. 进度详情窗口

```
┌──────────────────────────────────────────────────────────┐
│ ubuntu-24.04.iso                       5.7 GB · 76%      │
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ │ ← 分段条：每段一格
│ 段  0 ████████ 24.0MB/24.0MB 完成                         │
│ 段  1 ██████   9.8MB/24.0MB 下载中  8.1 MB/s              │
│ 段  2 ░░░░░░░░  0.0MB/24.0MB 等待中                       │
│ 速度 24.3 MB/s   剩余 1 分 12 秒   连接 3                  │
│ ─────────────────────────────────────────────────────── │
│ 24.3 ┤      ╭─╮                                          │
│      │   ╭──╯ ╰─╮                                        │
│  0   └───┴──────┴──────────────                          │
│ [暂停] [删除] [复制链接] [打开所在目录]                    │
└──────────────────────────────────────────────────────────┘
```

- 分段条：段数 ≤ 64 时逐段绘制，超过则按像素聚合（避免 256 段时视觉噪声）。
- 速度曲线：保留最近 120 秒采样（1Hz），折线自绘。
- 段列表在段数 > 200 时切换为聚合视图（只显示统计）。

## 6. 新建下载窗口

字段：URL（`TextBox`，`AcceptsReturn`，支持多行批量）、保存到（`TextBox` + 浏览按钮，记住上次）、
文件名（可空，空则自动推导）、分段数（`NumericUpDown`，1–256，默认取设置值）、
高级折叠区（Referer / Cookie / 自定义头）、「立即开始」勾选。

校验：URL 必须 `http/https` 绝对地址；保存目录必须存在且可写（失败即时提示红框 + 文案）。
多行 URL 时按钮文案变「批量添加」并列出条数。

结构预留：本窗口用 `TabControl`，首期只有「链接」一页；V2 增加「HuggingFace」页
（仓库 / revision / 通配符过滤 / Token），V3 增加「种子文件 / 磁力」页。页签内容不同，
**底部按钮区与校验提示区共用**，不要为每个协议各写一个窗口。

## 7. 交互与快捷键

| 操作 | 触发 |
|---|---|
| 新建 | `Ctrl+N`、工具栏、拖入 URL |
| 暂停/恢复 | `Space`（选中行）、工具栏、行内按钮 |
| 删除 | `Del`（弹确认，可选"同时删除文件"） |
| 打开文件 | `Enter`、双击（仅已完成） |
| 打开所在目录 | `Ctrl+Enter`、右键菜单 |
| 复制下载链接 | `Ctrl+C`（选中行，仅复制 URL） |
| 全选 | `Ctrl+A` |
| 搜索框聚焦 | `Ctrl+F` |
| 设置 | `Ctrl+,` |

右键菜单（多选生效）：暂停 / 恢复 / 删除 / 打开文件 / 打开目录 / 复制链接 / 属性。
拖放：窗口接收文件拖入（.txt 视为 URL 列表）与文本拖入（URL）。

## 8. 空态与错误态

- 空列表：居中图标 + 「把下载链接拖到这里，或点击新建」+ [新建下载] 按钮。
- 失败行：文件名下方直接显示错误文案（中文），Tooltip 为详细异常；提供 [重试]。
- 目录不可写：新建时阻断；运行中出现则任务转 `磁盘错误` 状态并保留 `.xdm-part`。

## 9. 可测试性约定（Headless 断言依赖）

- 每个关键控件必须设 `x:Name`，命名规范 `Part_Element`：
  `Main_TaskList`、`Main_StatusText`、`Row_FileName`、`Row_ProgressText`、`New_UrlBox`、`New_OkButton`。
- 行模板内控件的绑定必须可被 ViewModel 单测覆盖（`INotifyPropertyChanged` 属性名稳定）。
- 所有图标按钮设 `AutomationProperties.Name`（中文动作名），Headless 测试按名字查找，
  顺便满足无障碍。
- 禁止把状态文案硬编码在 XAML：统一走 `DownloadStatusTextConverter`，便于测试断言中文文案。

## 10. 尺寸与资源 token

| Token | 值 |
|---|---|
| 行高 | 56（紧凑模式 40） |
| 标题栏高 | 36（自绘，见 §11） |
| 工具栏高 | 40 |
| 状态栏高 | 24 |
| 左栏宽 | 180（可折叠至 0） |
| 圆角 | 4（进度条 2） |
| 字体 | 系统字体栈（SimdAIO 无 Inter，禁止 `WithInterFont()`；字号/字重按 Fluent 默认） |
| 字号 | 13 正文 / 11 次要 / 15 标题 |

## 11. 窗口外观（自绘标题栏 / 尺寸 / 图标）

系统 chrome 一律关掉，标题栏自己画：`WindowDecorations="BorderOnly"`
（不要用 `ExtendClientAreaToDecorationsHint`——Avalonia 12.1.6 里它去不掉系统标题栏，
会与本控件叠成两层；`ExtendClientAreaChromeHints` 在 12.x 已移除）。

### 11.1 `Controls/TitleBar`

| 项 | 规格 |
|---|---|
| 高 | 30，底部 1px 分隔线（与 Neat Downloader 等第三方自绘标题栏同高，系统默认 32 偏厚） |
| 底色 | `#1F2B3B`（`XdmTitleBarBrush`） |
| 分隔线 | `#2C3A4E`（`XdmTitleBarBorderBrush`） |
| 前景 | `#E7EEF8`（`XdmTitleBarForegroundBrush`） |
| 左区 | 18×18 透明底 logo（`/Assets/xdm-logo.png`，白底与辉光已剥离，直接贴深色底板）+ 12px SemiBold 标题 |
| 右区 | 最小化 / 最大化 / 关闭，各 44×30，`Button.cap` 样式（无圆角无边框） |
| 按钮 hover | 约 `#26FFFFFF`；关闭键 hover `#C42B1C`、按下 `#E81123` |
| 交互 | 空白处按下即 `BeginMoveDrag`；双击切换最大化；最大化时最大化键变「还原」双框图标 |
| 属性 | `Title`（与 `Window.Title` 分开维护）、`ShowMaximize` / `ShowMinimize`（对话框两者都设 `False`） |

约束：

- 宿主窗口必须是 `WindowDecorations="BorderOnly"`，否则两层标题栏。
- 标题栏放在 `DockPanel.Dock="Top"` 的第一项；窗口根布局保持 `DockPanel`，不要改成 `Grid` 硬编码行。
- 标题与图标按钮的 `PointerPressed` 必须 `e.Handled = true`，否则点按钮会把窗口拖起来。

### 11.2 窗口尺寸

| 窗口 | 默认 | 最小 |
|---|---|---|
| 主窗口 | 1000×620 | 720×420 |
| 新建下载 | 580×500 | 480×400 |
| 设置 | 620×570 | 540×480 |
| 关于 | 460×（内容自适应高） | 不可缩放 |
| 确认框（`ConfirmDialog`） | 440×（内容自适应高） | 不可缩放 |

对话框一律 `WindowStartupLocation="CenterOwner"` + `ShowInTaskbar="False"`，主窗口 `CenterScreen`。
`SizeToContent="Height"` 的对话框不要设 `Height`，否则内容会按固定高度拉伸。

### 11.3 图标资源

- 唯一来源是仓库根 `logo.png`（480×480，白底 + 蓝紫渐变箭头 + 光带），处理脚本 `tools/make-icons.ps1`：
  `pwsh tools/make-icons.ps1` 一次生成下面两个文件，改 logo 后重跑即可，**不要手工改产物**。
- 透明化规则：从四边洪泛「近白背景」（原图底色是 240~255 的渐变，不能用固定阈值一刀切）；
  箭头本体（min 通道 ≤ 200）保留原配色；中间那圈浅色辉光按白色混入比例给半透明并反合成去白，
  否则贴到深色标题栏上会浮出一圈白雾。alpha 再做一遍 3×3 均值消掉洪泛边界的碎齿，
  最后按内容包围盒裁正方形缩到目标尺寸。
- 界面内 logo：`src/Xdm.Ui/Assets/xdm-logo.png`（`AvaloniaResource`，256×256，**透明底**）：
  标题栏（18px，`RenderOptions.BitmapInterpolationMode="HighQuality"`）、关于窗口（52px）、窗口 `Icon` 共用。
- exe / 任务栏图标：`src/Xdm.App/Assets/xdm.ico`（`ApplicationIcon`），同一透明底打包
  16/20/24/32/40/48/64/96/128/256 十个 PNG 压缩帧（手工拼 ICONDIR，256 帧在目录项里记 0）。
- 不要再用「白底圆角牌」：标题栏是深色，白块会显得像贴纸。
