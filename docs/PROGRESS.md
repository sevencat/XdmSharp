# 进度台账

## 当前状态

| 里程碑 | 内容 | 状态 | 完成日期 |
|---|---|---|---|
| — | 开工文档（README / PLAN / ARCHITECTURE / UI-DESIGN / TESTING） | 完成 | 2026-11-22 |
| M0 | 地基：solution、分层工程、启动链、Headless 跑通、发布 | 完成 | 2026-11-22 |
| M1 | 主界面骨架（假数据） | 完成 | 2026-11-22 |
| M2 | 界面测试基建（Avalonia.Headless.XUnit） | 完成（随 M1 落地） | 2026-11-22 |
| M3 | 引擎核心（单段 → 多段 → 续传） | 完成（等分切段；动态二分/接管/竞速转入 M6） | 2026-11-22 |
| M4 | 接线（Manager + 进度上屏） | 完成（进度详情窗口待做） | 2026-11-22 |
| M5 | 任务库与队列（SQLite） | 完成（并发压测待补） | 2026-11-22 |
| M6 | 打磨（限速/代理/通知/可选切 SimdAIO） | 未开始 | — |

状态取值：`未开始` / `进行中` / `完成` / `阻塞`（阻塞须在日志里写明原因）。

## 待决事项

| # | 事项 | 结论 | 定案时间 |
|---|---|---|---|
| 1 | 主列表用 `DataGrid` 还是 `ListBox` + `ItemTemplate` | **`ListBox` + `ItemTemplate` + `VirtualizingStackPanel`**；不引 `Avalonia.Controls.DataGrid` 包 | 2026-11-22 |
| 2 | 是否加视觉回归工程（截图基线，需 Avalonia.Skia） | 待定，优先不做 | — |
| 3 | `PartAllocation`（无稀疏文件预填充）是否进首期 | 待定，倾向 M6 | — |
| 4 | 渲染后端 | **首期即用 `Sevencat.Avalonia.SimdAIO 12.1.6.2`**；回退官方 Desktop 栈只改 `Program.cs` + `Xdm.App.csproj` | 2026-11-22 |
| 5 | V3 BT 引擎：引 `MonoTorrent`（MIT）还是自研最小实现 | 待 V3 立项时评估；优先引库，自研仅作兜底 | — |
| 6 | V2 HF 是否需要 `hf_transfer` 式多连接加速 | 待 V2 实测 CDN 单连接吞吐后定 | — |
| 7 | 父子任务的列表呈现（展开 / 独立窗口 / 折叠统计） | 首期只做单行，但行模板预留子行区 | — |
| 8 | 任务库位置与文件名 | **`AppContext.BaseDirectory\xdm.db`**（exe 同目录，绿色版）；不可写回退 `%LOCALAPPDATA%\Xdm\`；命令行 `--db <path>` 覆盖（优先级最高） | 2026-11-22 |
| 9 | 发布形态：framework-dependent 单文件（12MB）还是 self-contained（~70MB） | 暂用 AIO 默认（需目标机装 .NET 10 运行时）；M6 按"绿色版"要求再定 | — |

## 日志

> 每次提交/里程碑收尾追加一条，倒序（最新在上）。

### 2026-11-22 · M5 完成（SQLite 任务库 + 启动恢复）

- `Core/Tasks/TaskStore`：每条 SQL 开一条池化连接；首次打开建 `tasks` / `segments`，
  `PRAGMA journal_mode=WAL`、`synchronous=NORMAL`、`user_version=1`、`busy_timeout=5000`。
  表一次建全：`kind` / `parent_id` / `content_sha256` / `source_meta_json` / `name_policy`，
  不做 ALTER 迁移。`:memory:` 用一条锚连接保持库存活，其余场景由 `AppPaths` 决定路径。
- `Core/Tasks/StoredTask`：任务行 DTO（含 headers / cookie / validator / 重名策略 / 段快照）。
  `Insert` 返回 SQLite 分配的 id，重启后新任务不会复用旧 id。
- `DownloadManager` 接库：`DownloadManagerOptions.Store` + `PersistInterval`（默认 30s）。
  写库时机严格按 §7.1：状态迁移、切分、30s 同步、完成/失败；`PersistTick` 先让引擎
  `FlushToDisk` + 写快照，再整表替换段；退出时补一次同步。落库失败只记 `PersistenceError`，
  不让 `SqliteException` 打死段线程。
- 启动恢复：`ResetIncomplete()` 把 `Queued/Connecting/Downloading` 降为 `Paused`；
  `Completed` 校验 `target_folder/file_name`，文件不在则降 `Failed` 并写错误文案；
  段按偏移首尾相接校验后回填，库里有段而 `state/<id>.json` 缺失时补写快照，恢复后能真续传。
- 重名策略：新增 `Storage/NameConflictPolicy`（`Rename` / `Overwrite` / `Fail`）与
  `FileNaming.ResolveTarget`。发布仍是**同目录 `File.Move`**（原子改名）：下载途中旧文件原样保留，
  成功后才替换；`Fail` 保留 `.xdm-part` 方便改名重试，并新增 `DownloadError.TargetExists`。
- `App/Program.cs` 启动时开库，失败返回码 `3`（库路径不可用），不再静默空表启动；
  `XDM_DEMO` 演示模式不建库。`App.axaml.cs` 把 `Startup.Store` 交给管理器，退出时先
  `manager.Dispose()`（最后一次落盘）再 `store.Dispose()`（`ClearAllPools` 释放文件句柄）。
- 测试：Core 106（新增 13 条 `TaskStore` + 8 条持久化/重名策略 + 2 条 `FileNaming`）
  + Ui 51 = **157 全绿**，Core 连跑 3 次无抖动；`dotnet build` 0 警告 0 错误。
  应用冒烟：`--db %TEMP%\xdm-m5-smoke\xdm.db` 首次启动生成 `xdm.db/-wal/-shm`，
  杀进程后同库二次启动正常。
- 明确未做：`100 任务并发 UI 不卡` 压测（需批量建任务脚本）；分类/搜索/历史；
  进度详情窗口（分段时间轴）仍等 M6。

### 2026-11-22 · M4 完成（接线：Manager + 进度上屏）

- `Core/Scheduling/DownloadQueue`：FIFO 队列，只管顺序不管并发（闸门在管理器），线程安全。
- `Core/Downloads/DownloadManager`：任务表 + 全局闸门（默认 3）+ 调度线程。
  调度器只负责「放行并起一个短命线程」，`Start/Resume` 的阻塞探测不占调用方；
  长驻的仍是引擎的段线程。槽位在终态（完成/失败/磁盘错）或暂停时归还，归还即唤醒调度器。
  对外 API：`Enqueue(DownloadRequest)` / `Snapshot` / `Find` / `Segments` / `Pause` /
  `Resume` / `PauseAll` / `ResumeAll` / `Delete`，事件 `TaskAdded` / `TaskUpdated` / `TaskRemoved`。
- `DownloadRequest`：新建下载只产请求（URL、目录、文件名、段数、是否立即开始），
  id/临时文件名/探测结果全由引擎决定，UI 不再自己编号。
- 界面接线：`Ui/Services/IDownloadActions`（Start/Pause/Resume/Delete）+ `MainWindowViewModel.Actions`。
  有引擎时命令只转发、**不抢答状态**（乐观改状态会把「排队」骗成「下载中」）；无引擎（演示/单测）
  仍按旧逻辑本地改，测试不受影响。`NewDownloadWindow` 改为吐请求，主窗 `StartDownloads` 交给管理器。
- `App/DownloadUiBridge`：`TaskAdded/TaskRemoved` 立即 `Dispatcher.UIThread.Post`（结构变化），
  进度先塞 `ConcurrentDictionary`，由 250ms `DispatcherTimer` 合并刷新——即 4Hz 上屏。
  `App/UiDownloadActions` 把接口调用转成管理器调用。`App.axaml.cs` 装配引擎、
  管理器、Bridge，退出时 `Dispose`；假数据退到 `XDM_DEMO=1000` 环境变量。
- **本轮修掉的两个并发缺陷**（都是 M4 的暂停路径逼出来的）：
  1. 探测阶段暂停无效：引擎原先在探测**之后**才注册 runtime，`Pause` 找不到运行时只能返回 false。
     改为**先注册再探测**，`Pause` 取消 token 即可打断探测；探测因取消返回失败时不再误判
     `Failed`，并给 `Runtime` 加 `ProbeDone` 标记，避免在探测线程还在用 token 时
     `CancellationTokenSource.Dispose()`（会抛 `ObjectDisposedException`）。
  2. 排队任务暂停失效：`PauseAll` 赶上「调度器已出队、引擎还没注册 runtime」的窗口时，
     `Pause` 落空、任务照跑。改为 `Entry.PauseRequested` 标记 + `RunEntry` 启动前后各取一次，
     命中就把任务置暂停并归还槽位。
- 测试：Core 72（新增 12 条管理器用例：成功发布、文件名推导、并发闸门、排队暂停不启动、
  运行中暂停保留 `.xdm-part` 并续完、删除清临时文件、失败释放槽位、`StartImmediately=false`、
  进度事件含速度/终态、非 http 拒绝、连接阶段暂停不误判失败、全部暂停/恢复）
  + Ui 51（新增 8 条：命令转发不抢答、删除转发、`RemoveById` 幂等、请求转发不造行、
  无引擎时造占位行、管理器事件驱动列表） = **123 全绿**，`dotnet build` 0 警告。
- 应用冒烟：真实启动（不设 `XDM_DEMO`）窗口标题 `XDM Sharp`、私有内存 123.7 MB，无异常退出。
- 明确未做：进度详情窗口（分段时间轴）；"100 任务并发 UI 不卡"压测（需 M5 任务库才有真实规模）；
  任务持久化仍是空表启动。

### 2026-11-22 · M3 完成（HTTP 引擎核心）

- 传输层 `Http/RangeHttpClient`：`SocketsHttpHandler` + 关自动重定向 + 关透明解压；
  自跟最多 10 跳并记 `FinalUrl`；200/206/416/401/403/410/429/503/5xx 全部翻成
  `SourceOutcome`（`Ok / Retry / Busy / Unsupported / Expired / Rejected`），
  调度层拿不到也判不到 HTTP 状态码。
- 存储层 `Storage/PartFile`：**单句柄 + `RandomAccess` 偏移写**，多段并发不需要锁；
  已知长度时越界直接抛（防止段区间算错把文件写坏），未知长度（EOF 模式）按写入增长。
  `Storage/AtomicJson`：`.tmp` → fsync → `File.Move(overwrite)`；`Storage/FileNaming`：
  `Content-Disposition`（含 RFC 5987 `filename*`）→ URL → 兜底名，含非法字符/保留名/重名处理。
  `Storage/SegmentVerifier`：SHA256 挂点（V2 的 LFS 校验、V3 的 piece 校验接这里），首期已有测试。
- 引擎 `Downloads/DownloadEngine`：探测（`bytes=0-0`）→ 按 `MinSegmentSize`/`MaxSegments` 等分建段
  → 开句柄 → 每段一个**专职 `Thread`**（不是线程池）→ 末段结束触发发布。阻塞 IO，
  没有 `async/await` 渗透到业务层（`ARCHITECTURE.md` 总体原则 5）。
- `SegmentRunner`：Range 请求 → 校验 `Content-Range` 起点/长度 → 流式写 → 每 `FlushInterval`
  fsync 并把 `Synced` 推到 `Downloaded`；短读/断流/超时按指数退避重试，重试从
  `Offset + Downloaded` 续写。`Task.Wait` 会把流异常包成 `AggregateException`，必须拆出来
  才走得到 `IOException` 重试分支（踩过一次）。
- 续传：暂停 = 取消 → join 段线程 → 刷盘 → 写 `state/<id>.json` → 关句柄；
  恢复先用 `If-Range: <Last-Modified>` 探测，`Last-Modified` 或总大小变化、或不支持 Range
  就整任务重来（删 part + 快照），否则按快照恢复段并只跑未完成的段。
- 拒绝写坏：区间起点/长度与请求不符一律 `LengthMismatch` 且**不落盘**；全部段完成后再核对
  `SyncedTotal == TotalSize`，不等则判内部错误而不是发布。
- 测试（`tests/Xdm.Core.Tests`，自建 `TcpListener` 假 HTTP 服务器）：单段/空文件/无 Range 退化/
  未知长度 EOF、4 段等分与区间起点、暂停后恢复真的从非零 offset 续、服务器文件变化后整任务重来、
  断流重试、404/长度造假/503 等待上限、`PartFile` 越界与句柄释放、`AtomicJson` 往返与损坏快照、
  `FileNaming` 的 RFC 5987 与保留名、`ProgressTracker` 速率/ETA、SHA256 已知向量。
  **Core 60 + Ui 43 = 103 全绿**，`dotnet build` 0 警告 0 错误，Core 测试 1 s 跑完。
- 明确未做（按计划留 M6）：动态二分、段接管、尾段竞速、连接数协商、限速、磁盘预留；
  下载中的 `If-Range` 不匹配目前按失败处理（暂停后恢复会走整任务重来）。

### 2026-11-22 · M1 完成（主界面骨架）+ M2 界面测试基建

- 主窗口按 `UI-DESIGN.md` §3 落地：40px 工具栏（新建 / 恢复 / 暂停 / 删除 + 搜索框 + 设置）
  + 180px 左侧分类（全部 / 正在下载 / 已完成 / 失败，带实时计数）+ 主 `ListBox`
  + 24px 状态栏（`StatusSummary` / `ListSummary` / 库路径）。空间不足时仍靠 `DockPanel` 收口，
  没用 `Grid` 多列硬编码。
- 行模板：`MinHeight=56` 的两行 `Grid`（`ColumnDefinitions="28,*,Auto"`）——扩展名图标、文件名、
  自绘 `SegmentBar`、段数与目录、速度/剩余、状态徽标、行内操作（暂停 / 目录 / 删除，悬停才显示）。
  完成行第二行改显 `目录 · 完成于 …`，状态徽标统一为短词（`下载中` / `已暂停` / `已完成` 等），
  百分比只出现在进度条下方，避免同一行三处重复。
- 自绘控件 `Controls/SegmentBar.cs`：`Value` / `Segments` / `Fill` / `Track` 四个 `StyledProperty`，
  `AffectsRender<SegmentBar>` 触发重绘，≤64 段才画分段线。不走 `ProgressBar`，软件光栅下更省。
- 手写最小 MVVM：`ViewModelBase`（`SetProperty`）+ `RelayCommand`，**不引** CommunityToolkit；
  文案唯一入口 `Formatting/TextFormat.cs`（字节 / 速度 / 剩余 / 百分比 / 完成时间），
  百分比截断不四舍五入。
- 窗口：`NewDownloadWindow`（多行 URL、保存目录 + `StorageProvider.OpenFolderPickerAsync`、
  文件名、段数 1–256、立即开始，`ShowDialog<bool>`）与 `SettingsWindow`（常规/连接/目录/网络四页骨架）。
  快捷键：空格暂停/恢复、`Delete` 删除。
- **虚拟化验证**：`SampleDownloads.Create(1000)`（固定种子 20261122）下，Headless 断言实际实例化的
  item 数落在 1–100 之间（`TaskList_VirtualizesThousandRows`），确认 `VirtualizingStackPanel` 生效；
  Debug 下 1000 行私有内存 116.8 MB（M0 空壳 100 MB，增量约 17 MB）、启动到窗口 ~1.3 s。
- 测试：`Xdm.Core.Tests` 12 + `Xdm.Ui.Tests` 43 = **55 全绿**（`dotnet build` 0 警告 0 错误）。
  界面测试三类样板齐了：控件树断言（`MainWindowTests`）、键鼠模拟（空格 / Delete）、
  ViewModel 驱动 UI（换分类 → `Rows` 与计数同步）。
- 坑：
  - Avalonia 12 的 `TextBox.Watermark` 已废弃，编译即 AVLN5001 错误 → 全部改 `PlaceholderText`；
  - 编译绑定要求每个 `DataTemplate` 也带 `x:DataType`（含分类项模板），否则退化到反射绑定并告警；
  - `PrintWindow` 按物理像素抓图，位图必须用 `GetDpiForWindow` 算出的缩放（本机 168 DPI / 1.75）
    分配，否则截图右侧和下边被裁；
  - `TextFormat.Bytes` 对 <10 的值保留一位小数（`1_000_000` → `977 KB/s` 而非 `1.0 MB/s`），
    写测试断言时按实际输出取值。

### 2026-11-22 · M0 完成（首个可运行版本）

- 仓库从"只有文档"变成可编译可运行：`XdmSharp.sln` + 5 工程 + `global.json`（SDK 10.0.101）
  + `Directory.Build.props`（`net10.0`/Nullable/TreatWarningsAsErrors）+ `Directory.Packages.props` + `build.ps1`。
- **渲染栈改为首期即用 SimdAIO**（用户确认）：`Xdm.App` 只引 `Avalonia` + `Sevencat.Avalonia.SimdAIO 12.1.6.2`，
  启动链 `UseStandardRuntimePlatformSubsystem` + `UseWin32` + `Win32RenderingMode.Software`
  + `UseSimdSkia(SimdSkiaGpuOptions.UseGpuPreferred)`。输出目录核对：有 `Avalonia.SimdAio.dll`，
  无 `Avalonia.Win32.dll` / `Avalonia.Skia.dll` / `SkiaSharp.dll` / 任何原生 dll。
- **测得的事实**（Release，未开 R2R）：启动到窗口 750–1175 ms（中位 ~800）；空闲私有内存 100 MB、
  工作集 139 MB；publish 单文件 `xdm-app.exe` **11.7 MB**。因此 `ARCHITECTURE.md` §2.3 把"≤80MB"
  改成"≤120MB 私有"，启动目标定 ≤900ms（开 R2R 后复测）。
- `AppPaths` 落地并测 12 条：`--db`/`--db=` > `XDM_DB` > `XDM_DATA_DIR` > exe 目录 > `%LOCALAPPDATA%`；
  相对路径按 cwd；指向目录/缺值抛 `ArgumentException`，`Program.Main` 捕获后返回码 2。
- 测试：`Xdm.Core.Tests` 12 条 + `Xdm.Ui.Tests` 3 条 Headless（标题/状态栏路径/可 Show），全绿。
- 坑（写下来免得重踩）：
  - xunit.v3 不自动注入 `using Xunit`，两个测试工程各加 `GlobalUsings.cs`；
  - `Directory.Build.props` 里 `EnableDefaultNoneItems=false` 会干扰 Avalonia 资源收集，已移除；
  - 关闭 `TreatWarningsAsErrors` 的代价太大，宁可修警告（AIO targets 的冲突警告会直接变错误，这是好事）；
  - `dotnet publish -r win-x64` 出来是 **framework-dependent** 单文件（AIO targets 默认），
    目标机需装 .NET 10 运行时 → 待决 #9。

### 2026-11-22 · `--db` 命令行覆盖

- 规格定为 `AppPaths.DbPath`，优先级：`--db <path>`（含 `--db=<path>`）> `XDM_DB` >
  `XDM_DATA_DIR`（目录语义）> `AppContext.BaseDirectory` > `%LOCALAPPDATA%\Xdm`（仅当上一级不可写）。
- `state\` 恒为库文件所在目录下的 `state\`，库与进度快照永不拆开（中途换 `--db` 也不会串）。
- 相对路径按当前工作目录转绝对；父目录自动创建；`--db` 指目录或缺值 → 报错 + 返回码 2，不静默回退。
- 分工：`Xdm.App` 读命令行并注入 `AppPaths`，`Xdm.Core` 不碰 `Environment.GetCommandLineArgs()`（可测）。
- 单实例：不同 `--db` 允许并存；同库双开 M6 再加锁，首期只记日志警告。
- 落点：`ARCHITECTURE.md` §7.1（解析表与边界）、`PLAN.md` M0（`AppPaths.cs` + 验收命令）、
  `README.md`（用法示例）。**代码未写**（仓库尚无 `src/`），随 M0 落地。

### 2026-11-22 · 对照已安装的参考程序（9.0.11）

- 对照对象：`D:\network\XDM\xdm-app.exe`（jpackage，JDK `25.0.4.1`，`app/xdm-app.cfg` 可读 JVM 参数）。
  它当前未运行，需要时可随时启动看 UI；数据目录 `%LOCALAPPDATA%\xdm-app`（扁平二进制
  `xdm-app.config` + `window-geometry.dat` + `logs/` + `tmp/`）。
- **临时文件命名对齐**：我们原定的 `<id>.part` 改为 **`.<id>.xdm-part`**（点前缀 + `.xdm-part`，
  与参考实现 `.<hexid>.xdm-part` 一致），避免与用户文件撞名、避免被误认为完整文件。
  涉及 `README.md`、`ARCHITECTURE.md` §5/§6、`UI-DESIGN.md` §8、`PLAN.md`。
- 新增 `ARCHITECTURE.md` §2.3「启动与常驻开销基线」：从参考实现的 ZGC/AppCDS/小堆参数推出
  "下载器常驻后台，启动与内存要省"的意图，定下我们的目标（冷启动 ≤800ms、空闲 ≤80MB、
  启动期不同步做网络/磁盘重活、发布用 R2R 而非单文件压缩）。
- 数据目录解析顺序补全：`--data-dir` > `XDM_DATA_DIR` > exe 目录 > `%LOCALAPPDATA%\Xdm`，
  `xdm.db` 与 `state/` 同属一个数据目录。

### 2026-11-22 · 定案：ListBox 列表 + xdm.db 绿色版布局

- 主列表**定案** `ListBox` + `ItemTemplate` + `VirtualizingStackPanel`，**不再引 `Avalonia.Controls.DataGrid`**
  （工程表、`Directory.Packages.props` 都不用加）。理由：行模板两行式不吃列布局、视觉树更轻、
  行内自绘 `SegmentBar` 更好控；排序需求将来在 ViewModel 层做。M1 验收改为"1000 行假数据滚动不掉帧"。
- 任务库位置**定案** `AppContext.BaseDirectory\xdm.db`（exe 同目录，绿色版：拷走目录即拷走数据）。
  WAL 模式 → 备份需带 `-wal` / `-shm`，或先 `PRAGMA wal_checkpoint(TRUNCATE)`；
  exe 目录不可写时回退 `%LOCALAPPDATA%\Xdm\`，日志提示不弹窗；测试一律用 `:memory:` 或临时目录。
- 同步更新：`ARCHITECTURE.md` §2（去掉 DataGrid 包）、§7.1（库路径 / 连接串 / 回退策略 / 首次建表）、
  `README.md`（技术栈去掉 DataGrid，新增「数据文件」表格）、`UI-DESIGN.md` §3（选型与虚拟化参数）、
  `PLAN.md`（M1 验收、M5 库位置、风险表把 DataGrid 版本风险换成"目录不可写"与"WAL 备份"两条）。

### 2026-11-22 · 预留 HF / BT 扩展

- 后续路线确定：**V2 HF 模型下载** → **V3 BT / 磁力下载**，首期均不实现。
- `ARCHITECTURE.md` 新增 §12：6 条"现在不做但别做死"的契约 + HF 流程（Hub API / resolve 302 /
  跨域剥离 Bearer / 签名 URL 过期重解析 / 文件级+段级两级并发 / LFS sha256 / `HF_ENDPOINT` 镜像）
  + BT 方案（独立引擎、piece 校验挂 `SegmentVerifier`、稀疏写、webseed 复用 HTTP 引擎、引库优先）。
- 类型层预留：`TaskKind`（Http / HuggingFace / Torrent）、`DownloadTask.ParentId`、
  `SourceOutcome`（HTTP 状态码不再外泄到调度层）、`SegmentVerifier`。
- Schema 预留：`tasks.kind` / `parent_id` / `content_sha256` / `source_meta_json`。
- `PLAN.md`：V2/V3 里程碑与工作量（6–8 / 15–20 人日）、4 条新风险（其中"首期写死单文件任务"
  是最大返工风险）。首期为此多花 1–2 人日。

### 2026-11-22 · 文档修订（命名与后端）

- 命名空间根改为 **`Xdm`**，工程/程序集同步改为 `Xdm.Core` / `Xdm.Ui` / `Xdm.App` /
  `Xdm.Core.Tests` / `Xdm.Ui.Tests`；解决方案与仓库目录仍为 `XdmSharp`。
- 目标框架统一 `net10.0`（不写 `-windows`，`RuntimeIdentifier` 只在发布时给）。
- 首期渲染后端定为**标准 Avalonia 桌面栈**（`Avalonia.Desktop` + `UsePlatformDetect()` +
  `WithInterFont()`），**不引用** `Sevencat.Avalonia.SimdAIO`；
  同时在 `ARCHITECTURE.md` §2.2 写清 6 条"现在就要守"的约束，保证将来切换只动
  `src/Xdm.App/Program.cs` + App 包引用。
- 界面测试工程继续只引用 `Xdm.Ui`（不引 App）：现在能共存，但切 AIO 后 App 与 Headless 会冲突，
  这样切换时测试零改动。
- 核实依赖：Avalonia `12.1.6`、Avalonia.Desktop / Themes.Fluent / Fonts.Inter / Skia /
  Win32 / Headless / Headless.XUnit 均有 `12.1.6` 缓存；DataGrid `12.1.2`；xunit.v3 `3.2.2`；
  Microsoft.Data.Sqlite `10.0.15`；SimdAIO `12.1.6.2`（预留）。SDK `10.0.101`。
- 遗留：仍无代码，暂无测试可跑。

### 2026-11-22 · 开工文档

- 完成 `README.md` 与 `docs/` 下四份文档（PLAN / ARCHITECTURE / UI-DESIGN / TESTING）。
- 里程碑顺序调整为**界面优先**：M0 地基 → M1 界面骨架 → M2 界面测试 → M3 引擎 → M4 接线 → M5 任务库 → M6 打磨。
- 定下工程分层 `Core`（无 UI）← `Ui`（无启动栈）← `App`（组装）。
- 验证：`dotnet --list-sdks` 含 `10.0.101`，满足 net10.0 要求。
