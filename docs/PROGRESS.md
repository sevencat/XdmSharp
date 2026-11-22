# 进度台账

## 当前状态

| 里程碑 | 内容 | 状态 | 完成日期 |
|---|---|---|---|
| — | 开工文档（README / PLAN / ARCHITECTURE / UI-DESIGN / TESTING） | 完成 | 2026-11-22 |
| M0 | 地基：solution、分层工程、启动链、Headless 跑通、发布 | 完成 | 2026-11-22 |
| M1 | 主界面骨架（假数据） | 完成 | 2026-11-22 |
| M2 | 界面测试基建 | 完成（随 M1 落地；Avalonia 版测试已随前端删除，同能力见 MewUI S5） | 2026-11-22 |
| M3 | 引擎核心（单段 → 多段 → 续传） | 完成（等分切段；动态二分/接管/竞速转入 M6） | 2026-11-22 |
| M4 | 接线（Manager + 进度上屏） | 完成（进度详情窗口待做） | 2026-11-22 |
| M5 | 任务库与队列（PureDb） | 完成（并发压测待补） | 2026-11-22 |
| M6 | 打磨（限速/代理/通知/单实例） | 进行中（限速/代理/设置/标题栏/单实例/命令行 URL/全局连接上限/进度详情窗口/文件级并发已完成，Avalonia 前端已删除；PartAllocation 待做） | — |
| V2 | HuggingFace 模型下载 | 进行中（清单 + 勾选 + 父任务汇总 + 代理 + 发布前 sha256 校验 + 文件级并发 + 子行展开已落地；`hf_transfer` 式多连接待实测） | 2026-11-23 |

状态取值：`未开始` / `进行中` / `完成` / `阻塞`（阻塞须在日志里写明原因）。

## 待决事项

| # | 事项 | 结论 | 定案时间 |
|---|---|---|---|
| 1 | 主列表用什么容器 | **MewUI `ListBox`**（虚拟化即绘制，不为每行建元素）；不引表格控件 | 2026-11-22 |
| 2 | 是否加视觉回归（截图基线） | 不做自动化；用端到端截图脚本人工比对 | 2026-11-22 |
| 3 | `PartAllocation`（无稀疏文件预填充）是否进首期 | 待定，倾向 M6 尾 | — |
| 4 | 渲染栈 | **MewUI（`Win32Platform` + `Direct2DBackend`）唯一**；Avalonia 前端已于 2026-11-22 删除 | 2026-11-22 |
| 5 | V3 BT 引擎：引 `MonoTorrent`（MIT）还是自研最小实现 | 待 V3 立项时评估；优先引库，自研仅作兜底 | — |
| 6 | V2 HF 是否需要 `hf_transfer` 式多连接加速 | 待 V2 实测 CDN 单连接吞吐后定 | — |
| 7 | 父子任务的列表呈现（展开 / 独立窗口 / 折叠统计） | 默认只渲染父行（第二行显示「N/M 个文件」）；父行行首三角可原地展开子文件行（2026-11-23 落地），不做独立窗口 | 2026-11-23 |
| 8 | 任务库位置与文件名 | **`AppContext.BaseDirectory\xdm.db`**（exe 同目录，绿色版）；不可写回退 `%LOCALAPPDATA%\Xdm\`；命令行 `--db <path>` 覆盖（优先级最高） | 2026-11-22 |
| 9 | 发布形态：framework-dependent 目录（4MB）/ self-contained / NativeAOT（15MB） | 默认 framework-dependent（需 .NET 11）；另提供 AOT 档 `build.ps1 -Aot`（15MB 单 exe、目标机零依赖；构包需 MSVC 链接器 + Windows SDK） | 2026-11-23 |
| 10 | 同时下载/恢复的任务量大时，界面怎么刷 | **待更新项进队列 → `DispatcherTimer` 每 200ms 在 UI 线程批量 flush**；同时把行定位改字典索引、每帧汇总只算一次、结构变化合并（方案见 `ARCHITECTURE.md` §6.1）。当前是 250ms 单条 drain，够用到百级任务 | 2026-11-22（方案定，未实现） |

## 日志

### 2026-11-23 · HF 搜索支持多关键字

- 实测（socks5 → huggingface.co）：`/api/models?search=qwen 7b` 是**全文检索 + 空格 AND**，返回的每条 id 都同时含
  两个词；`qwen 7b` 有 30+ 条，而 `qwen zzzzzz` 回 0 条。**逗号不认**：`qwen,7b` 回 0 条。
- 因此不做客户端交集，改成：`HfHubClient.SplitKeywords` 按空格 / 中英文逗号 / 分号拆词（去空、忽略大小写去重、
  最多 10 个），再用空格拼回去**发一个请求**，AND 交给 Hub。少一半请求，也不会因「各取前 20 条再求交」丢模型。
- VM 提示分单/多关键字两套文案（`正在搜索「qwen」「7b」…` / `找到 20 个同时匹配 qwen、7b 的模型…`），
  空闲提示改成说明「空格或逗号分开多个即同时包含」。
- 测试：Core +7（拆分 4、空格拼串单请求 1、纯分隔符不打请求 1、上限 1），Ui.Mew +2（多关键字文案、无命中文案）。
  全量 **382 通过**（Core 271 + Ui.Mew 111）。

### 2026-11-23 · HF 搜索结果加「详情」

- **交互变了**：搜索结果点一行现在只**选中**（以前一点就填 id 并拉清单）。底部按钮按模式切换：
  结果模式给「详情」「下载此模型」，文件模式仍是「全选」「全不选」；「详情」两种模式都能用
  （结果模式看选中项，文件模式看输入框里的模型 id）。
- **详情内容**：文件数、总大小、任务类型、下载/点赞、README 摘要。
  `HfHubClient.GetReadmeAsync` 拉 `{endpoint}/{repo}/raw/{rev}/README.md`（404 = 没写 README，返回 null）；
  `HfReadme.Summarize` 去 YAML 头/HTML/图片/markdown 记号后截 400 字；`HfModelDetails.Create` 汇总。
  清单刚拉过且同仓库时直接复用，不重复请求。
- **窗口**：`HfModelDetailsWindow`（520×360，标题栏复用 `TitleBarChrome`），README 摘要可滚动。
- **测试**：Core +14（README 摘要 5、详情聚合 2、客户端 README 拉取 3、URL 1 …），Ui.Mew +6
  （选中/按钮状态、详情数据、手填 id、无目标拒绝、详情窗渲染 2）。全量 **373 通过**（Core 264 + Ui.Mew 109）。

### 2026-11-23 · HuggingFace 专用代理

- **设置项**：`AppSettings.HuggingFaceProxyId`（结构版本 5 → 6）。`null` = 跟随 `DefaultProxyId`
  （老设置文件读出来就是 `null`，行为不变）、`direct` = 直连、其余为具名代理 id；
  `Normalized()` 里丢弃指向已删代理的 id（退回跟随默认），手写成 `system` 也归到「跟随默认」。
- **解析**：`ProxyResolver.ResolveHuggingFace(settings)`（内部复用 `Resolve`，空 id 自然落到默认代理）。
- **UI**：设置 → 网络页 HuggingFace 段多一组单选「HuggingFace 代理」（跟随默认 / 直连 / 各条具名代理，
  与「默认代理」那组同源、增删代理行时一起重建）；新建下载对话框按 `hfProxyId` 预选代理单选，
  于是 HF 的搜索、拉文件清单、模型包下载（`SubmitHuggingFace` 把选中项写进 `DownloadGroupRequest.ProxyId`）
  都走这条路线，用户仍可在对话框里临时改。
- **测试**：Core +7（归一化/克隆/落盘往返 + 三条 `ResolveHuggingFace`），Ui.Mew +3
  （设置页下标映射、对话框预选并入包）。全量 **353 通过**（Core 250 + Ui.Mew 103）。

### 2026-11-23 · 目标框架升到 net11.0

- **改动**：`Directory.Build.props` 的 `TargetFramework` 由 `net10.0` 改为 `net11.0`；
  `global.json` 的 SDK 由 `10.0.101`（`latestPatch`）改为 `11.0.100`（`latestFeature`）。
- **验证**：`dotnet build` 0 警告 0 错误；`dotnet test` 全量 **343 通过**（Core 243 + Ui.Mew 100），
  测试产物落到 `bin/Debug/net11.0/`。
- **顺带**：关于窗口版本行改 `.NET 11`；`ARCHITECTURE.md` / `PLAN.md` / `TESTING.md` / `README.md`
  的 TFM 与运行时说明同步，README 测试条数由 329 更正为 343。
- **未做**：`.NET 11` 的 NativeAOT 发布档（`build.ps1 -Aot`）未跑；
  `publish/` 下的旧 `net10.0` 产物未重新生成。

### 2026-11-23 · HF 新建下载加模型搜索

- **问题**：HF 页只认完整的 `owner/name`，用户得先知道准确 id（还得去网页上抄），拼错一点就 404。
- **做法**：
  - `HfHubClient.SearchModelsAsync` 打 `GET {endpoint}/api/models?search=…&limit=20&sort=downloads&direction=-1`，
    解析 `id`/`downloads`/`likes`/`pipeline_tag`（新记录 `HfModelSummary`），只留两段 `owner/name`；
    关键字与站点交给 `HfHubClient.SearchUrl`（站点缺省回落官方，`limit` 夹到 [1,100]）。
  - `HfDownloadViewModel.Search()` 拿输入框内容当关键字（同步，界面放后台线程），命中列表进 `SearchResults`；
    `SelectModel()` 把 id 填回 `Repo` 并清空结果。
  - 对话框 HF 页加「搜索」按钮：结果列表与文件列表叠在同一格按模式切换（两套条目类型，模板不能复用），
    点一条结果 = 填 id + 立即拉清单；结果模式下点「添加」会被拦下（文件清单还是上一个仓库的）。
- **测试**：Core +6（URL 拼装/解析/过滤非 repo id/token/空关键字不发请求/非 JSON），
  Ui.Mew +6（ViewModel 搜索与选中、对话框搜索结果列表与拦截）。
- **未做**：结果分页（只取 20 条）、按 author/tag 过滤。

### 2026-11-23 · 列表原地展开子文件行

- **问题**：HF 模型包在列表里只有一个父行，看不到包内哪个文件慢、哪个失败了，只能开详情窗口；
  待决 #7 早已定下「行模板的子行区保留、展开视图后补」，一直没补。
- **做法**：父行行首加 14px 三角位（`PathShape` 9×9 描边，收起朝右 / 展开朝下，普通行隐藏），
  点击调 `MainWindowViewModel.ToggleExpand`：
  - 子行来源同步走 `IDownloadActions.Children(id)`（引擎 `DownloadManager.Children` 快照）；
    演示模式退到 `MainWindowViewModel.DemoChildren`（`SampleDownloads.Children`）。
  - 子行**插在父行之后、同处 `Rows`**，所以行高仍是统一 56，虚拟化/选中/右键菜单全部沿用；
    子行把三角位留空形成 14px 缩进，图标显示自己的扩展名。
  - 展开状态存 `_expanded`（父 id → 子行列表）：`ApplyFilter` 重建集合时跟着父行原位恢复，
    父行删除时整段消失，`RemoveById` 能认领子任务 id（展开列表里删单个文件）。
  - 子行不是独立任务：不进 `_all`、不进分类计数，`ListSummary` 改为只数父行（`显示 1 / 共 1 个任务`）。
- **顺手修的老问题**：`OnRowsChanged` 对 `NewItems` 只 `+=` 订阅，而 `ApplyFilter` 的 `Clear()` 走
  Reset（拿不到 `OldItems`），同一行反复 Add 会把订阅叠成 N 份 —— 改成先 `-=` 再 `+=`，幂等。
- **演示数据**：`SampleDownloads.Create(n)` 的第 4 行换成 `Qwen2.5-7B-Instruct` 模型包
  （`IsContainer`、4 个子文件、2 个已完成），`XDM_DEMO` 下可直接点三角验收。
- **测试**：`DownloadGroupRowTests` 加 8 例 —— 展开插在父行之后且 `ListSummary` 只数父行、再点收起、
  非父行点不动、换分类/搜索后展开状态保留（搜索词命中不了父行则整段消失）、展开期间子快照刷新到对应行、
  `RemoveById` 摘掉单个子行、删父行连带子行、演示数据里确有可展开的模型包。
- **测试结果**：全量 **329 条全绿**（Core 235 + Ui.Mew 94），构建 0 warning。
- **实机验证**：启动 `XDM_DEMO=6` 截到一次整窗渲染，确认加了 14px 三角列后原行布局没被挤坏（普通行三角隐藏、图标/名称/进度条位置正常）；
  展开态的截图没取到——随后系统进入锁屏，`CopyFromScreen` 只能拿到锁屏壁纸，多次唤醒无效后放弃，未做点击式视觉比对。
- **未做**：三角的 hover 反馈、子行折叠动画。文档已按实现更新，视觉细节沿用既有 token。

### 2026-11-23 · 多文件任务的文件级并发闸门

- **问题**：`EnqueueGroup` 一次性把 HF 包里**所有**文件塞进队列，`MaxConcurrent` 只能保证「同时跑 3 个任务」，
  于是一个 300 文件的模型包会把任务槽位占满几十分钟，用户手动添加的小任务排在后面饿死；
  同时任务列表里 297 行 `Queued` 也毫无信息量。
- **做法**：`DownloadManager` 加兄弟闸门 —— `_runningChildren`（父行 id → 在跑子任务数）在 `_gate` 内随
  `SlotHeld` 加减；`TryTakeNext` 遇到「本包名额已满」的子任务就**放回队尾**并继续扫下一个，
  单次只扫一遍当前队列长度（`budget`），不会被全是被挡兄弟的队列卡成死循环；`ReleaseSlot` 还名额后
  `_wake.Set()` 唤醒调度线程重扫。独立任务（`ParentId == 0`）不受影响，仍只受 `MaxConcurrent` 管。
- **配置**：`AppSettings.MaxFilesPerTask`（默认 3，夹紧 1–16，schema 4 → 5）、`DownloadManagerOptions.MaxFilesPerTask`、
  `DownloadManager.MaxFilesPerTask`（运行中可调，调大立即唤醒）；设置页「连接」页新增
  「每个多文件任务同时下载的文件数」，保存后即时生效。
- **测试**：`DownloadGroupTests` 加 3 例 —— `MaxFilesPerTask_One_SerializesSiblingFiles`（4 文件、闸门 1，
  采样子任务并发峰值必须是 1 且全部完成）、`MaxFilesPerTask_Two_RunsSiblingFilesInPairs`（5 文件、闸门 2，峰值恰好 2）、
  `MaxFilesPerTask_DoesNotThrottleIndependentTasks`（两个无父行的任务能同时跑，证明闸门没误伤）；
  `AppSettingsTests` 加 2 例（默认 3 + 夹紧、`Clone` 拷贝），`ProxySettingsViewModelTests` 加 1 例（回填 + 保存夹紧）。
- **测试结果**：全量 **321 条全绿**（Core 235 + Ui.Mew 86），`DownloadGroupTests` 连跑 6 轮无抖动，构建 0 warning。
- **文档**：`ARCHITECTURE.md` §6.2 新增「同一包内的文件闸门」表、§12.2 第 6 条改为指向实现。
- **仍未做**：列表里的子行展开视图、`hf_transfer` 式单文件多连接。

### 2026-11-23 · 任务详情窗口（分段时间轴）

- **新窗口** `src/Xdm.Ui.Mew/Dialogs/TaskDetailsDialog.cs`：自绘标题栏 + 头部（文件名 / URL / 状态徽标 / 大小 /
  速度 / 剩余 / 总进度条 + 百分比 / 「N/M 段 · 目录」）+ 分段列表。普通任务逐段显示 `#id / 区间 / 已同步÷段长 /
  状态`，多文件任务（HF 模型包）逐文件显示 `序号 / 文件名 / 已下载÷总长 / 状态`。
- **数据是拉的不是推的**：窗口每 250ms 调一次 `IDownloadActions.Segments(id)` / `Children(id)`（主列表同一节拍），
  窗口不开就没有任何开销，也不用往 `IDownloadObserver` 里加「只有窗口开着才有人听」的事件。
- **`DownloadManager.Segments` 改读实时段表**：原来返回 `entry.Segments` 缓存，只在建段/暂停时更新，
  拿去画时间轴会一直停在旧进度。现在从 `task.Snapshot()`（任务内锁）取，且不持 `_gate`——4Hz 轮询不该和调度线程抢锁；
  段表为空时回落到缓存（未跑过的任务）。新增 `DownloadManager.Children(id)` 返回父行下的子任务记录（id 升序）。
- **`IDownloadActions` 增两员**：`Segments(long)`、`Children(long)`；`MewDownloadActions` 直转管理器。
- **接入口**：右键菜单新增「任务详情…」（`RowDetailsCommand`），行上 `MouseDoubleClick` 也打开。
  行控件只在段/文件集合变化时重建，仅进度走动时复用已有控件。
- **测试**：`TaskDetailsDialogTests`(5：逐段渲染、集合不变复用 / 变化重建、空段提示、父行渲染子文件、失败文案)；
  Core 补 `DownloadGroupTests.Children_ExposesChildRecords_InIdOrder`、`Segments_IsLive_WhileTaskRuns`（钉住「缓存不刷新」这个坑）。
  全量 **315 条全绿**（Core 230 + Ui.Mew 85），构建 0 warning。
- **稳定性观测**：串行连跑 8 轮全解决方案 + 12 轮 Core 全绿；此前在同一进程里叠跑构建/多份测试进程的高负载下见过 2 次计时类用例失败
  （`PeriodicSync_WritesProgressBeforeShutdown`、`Delete_RemovesPartAndState`，均为「等到有进度再暂停」的写法），
  之后 26 轮串行未复现，判为资源竞争所致的测试抖动，本轮不改；若以后进 CI 再统一上「重试 + 放宽等待」策略。
- **文档**：`UI-DESIGN.md` 窗口表 / §7 交互表、`IDownloadObserver.OnSegmentsChanged` 注释（明确详情窗口不订阅）。

### 2026-11-23 · 全局连接上限（ConnectionSlots）+ 暂停竞态收口

- **配额**：新增 `Core/Scheduling/ConnectionSlots`（信号量式，`Acquire`/`Release`/`SetCapacity`/`IsSaturated`
  + `SlotLease` 作用域租约）。`DownloadEngineOptions.ConnectionSlots` 由 `MewRuntime` 建一个实例给所有任务共享；
  `SegmentRunner.Run` 的「发请求 → 读完响应体」整段持租约，**探测请求同样占名额**（否则并发峰值会超上限）。
- **设置**：`AppSettings.MaxConnections`（默认 32，夹紧 1–256，schema 3 → 4），`SettingsViewModel` +
  设置对话框「连接」页新增「全局连接上限（所有任务的所有分段合计）」；保存后 `SetCapacity` 即时生效，
  调大立刻唤醒等待中的段。
- **测试**：新增 `ConnectionSlotsTests`(10：饱和阻塞/Release 唤醒/等待中取消/调大唤醒/调小不打断在途/
  Dispose 踢出等待者/超额 Release/夹紧/null 空转/租约归还)、`ConnectionQuotaTests`(4：占满且不超、
  容量 1 串行、跨任务共享、暂停归还名额)。断言口径改成采样 `ConnectionSlots.InFlight` 峰值——
  服务端「写完关 socket」比客户端「读完让名额」慢半拍，服务端连接峰值不可靠。
- **修掉两个暂停竞态**（并行跑测试放大后暴露）：
  1. `Pause` 枚举 `Runtime.Threads` 时与 `Launch` 追加线程并发 → `Collection was modified`；
     且 `Resume` 在调度线程上尚未 `Launch` 就被 `Pause` 收尾，段线程会拿到已释放的 CTS。
     改为 `Runtime.SetupDone`（装配结束置位）+ 线程表读写持 `Runtime.Gate`，`Pause` 先 `Cancel` 再等装配收尾。
  2. 后台线程取 `Cancellation.Token` / `WaitHandle` 撞上 CTS 已释放 → 未捕获 `ObjectDisposedException`。
     改为构造时抓 `Runtime.Token`，`WaitHandle` 等待对 `ObjectDisposedException` 按「已收尾」处理
     （引擎 `Sleep` / `SegmentRunner.Wait`），`Pause`/`Dispose` 的 `Cancel` 走 `TryCancel` 容忍已释放。
- **测试结果**：Core 连跑 4 轮均 **228 条全绿**；全量 **308 条全绿**（Core 228 + Ui.Mew 80），构建 0 warning。
- **未做**：文件级并发限流（HF 一个任务内最多几个文件同时跑）、`hf_transfer` 式单文件多连接。

### 2026-11-23 · HF 父任务汇总（契约 1/2 落地）

- **Core**：新增 `DownloadGroupRequest`（父行名/目录/协议/元数据 + 子任务请求列表）；
  `DownloadManager.EnqueueGroup` 插父行 + 逐文件 `AddTask(file, parentId)`，父行不进调度队列。
- **汇总**：新增 `GroupTotals` / `AggregateLocked`，父行的大小/已下载/速度/文件数由子任务求和，
  状态按「全完成 → 有子在跑 → 有失败 → 有排队 → 暂停」推导；`Publish(child)` 末尾联动
  `RefreshContainer`（状态变了才落库，进度走 30s 的 `PersistTick`）。
- **级联**：`Pause`/`Resume`/`Delete` 对父行逐个作用到子任务；删父行先删子行，不留孤儿。
- **恢复**：按 `parent_id` 重建父子关系（**有子行才算父行**），父行不做「已完成文件是否存在」检查，
  状态与完成时间按子任务重算；首版按文件建的旧任务没有子行，照旧按普通任务显示，无需迁移。
- **UI**：`MainWindowViewModel.AddRange` 跳过子行（列表只出父行），`StartGroup` 走 `IDownloadActions.StartGroup`
  → `MewDownloadActions` → `EnqueueGroup`；行第二行由「N/M 段」改为「N/M 个文件」，图标标签「包」；
  完成通知只按父行报一次（`MewDownloadBridge` 过滤 `ParentId != null`）。
- **弹窗**：`NewDownloadDialog.Show` 改为返回 `NewDownloadResult`（链接页给 `Requests`，HF 页给 `Group`）。
- **测试**：新增 `DownloadGroupTests`(6：汇总/下载中联动/暂停恢复/删除级联/子失败汇总/重启恢复)、
  `DownloadGroupRowTests`(4：子行不占行/文件数文案/完成文案/StartGroup)。全量 **294 条全绿**
  （Core 214 + Ui.Mew 80），构建 0 warning。
- **未做**：列表里的子行展开视图（当前只渲染父行）、文件级并发限流、`hf_transfer` 式多连接。

### 2026-11-23 · HuggingFace 下载（第一版：模型 id → 清单 → 勾选 → 任务）

- **Core（纯逻辑，可注入 HttpClient）**，新命名空间 `Xdm.Core.HuggingFace`：
  - `HfRepo`：解析 `owner/name`（含粘贴整条 `https://huggingface.co/owner/name` 的容错），
    `ResolveUrl` 拼 `{endpoint}/{repo}/resolve/{rev}/{path}?download=true`。
  - `HfFile`：清单条目 `path`/`size`/`sha256`，`RelativeFolder`、`IsSafePath`
    （拒 `..`、绝对路径、`//`、反斜杠——清单里出现过 WSL 风格的路径）。
  - `HfHubClient`：`/api/models/{repo}/tree/{rev}?recursive=true&expand=true`，`Link` 头分页（50 页兜底），
    过滤目录/符号链接，按路径排序；`CreateHandler` 开自动重定向 + `DecompressionMethods.All`
    （gzip JSON 不解压就解析不了），与下载客户端的「关重定向、关自动解压」刻意相反。
  - `HfDownloadPlanner`：`RootFolder` = `{保存目录}/{owner}/{name}`；`Plan` 按 `size` 跳过本地已存在文件；
    `ToRequests` 先建子目录，再出 `TaskKind.HuggingFace` 请求（token → `Authorization`、sha256 → `ContentSha256`、
    协议信息 → `SourceMetaJson`）。
- **重名策略改成 `Overwrite`**：模型目录是仓库镜像，大小变了就是文件更新；默认 `Rename` 会写成
  `model (1).onnx`，推理框架按原名加载就读不到。大小一致的文件在 `Plan` 已跳过，覆盖是安全的。
- **设置项**：新增 `HuggingFaceEndpoint`（默认 `https://huggingface.co`，镜像可填 `hf-mirror.com`）与
  `HuggingFaceToken`，保存时走 `SanitizeEndpoint` / `SanitizeToken`（去空白、去查询串、非法地址回落默认）；
  token 为空时 UI 层回落 `HF_TOKEN` 环境变量。
- **UI**：新建下载窗口改 `TabControl`（「链接」/「HuggingFace」），保存到/分段数/立即开始/代理单选组
  移到页签下方两页共用，窗口固定 620×600。HF 页：模型 id + [拉取文件] + 版本 + 多选文件列表（默认全选）
  + [全选]/[全不选] + 状态行；拉清单在 `Task.Run` 里跑、结果经 `Dispatcher.BeginInvoke` 回填，
  失败原因（网络/超时/401/404/非 JSON）写状态行。清单请求与文件下载共用选中的代理。
- **测试**：新增 `HfRepoTests`(7) / `HfHubClientTests`(6，含分页与 gzip) /
  `HfDownloadPlannerTests`(10 例)，`HfDownloadViewModelTests`(7)，`NewDownloadDialogTests` 加 4 例
  窗口级 HF 用例，`AppSettingsTests` 加 5 例归一用例；`FakeHttpServer` 增加文本响应与带查询串的精确匹配。
  全量 **281 条全绿**（Core 206 + Ui.Mew 75），构建 0 warning。
- **补强（同日）**：
  - 发布前内容校验接线：`DownloadEngine.Complete` 在 part 写满并 flush 之后、改名发布之前比对
    `ContentSha256`（HF 的 LFS oid）；不符则删 part 并报新增的 `DownloadError.Checksum`（「内容校验失败」），
    重试从头下。`Checksum` 追加在枚举末尾，避免旧库 `error_code` 错位。
  - 代理一致性：拉清单不再固定用窗口打开时的默认代理——每点一次「拉取文件」按当前代理单选组重建清单客户端
    （`HfDownloadViewModel.TryUseProxy`，注入的测试客户端不动）。
  - 测试：`IntegrityTests` 加 2 例（正确摘要发布成功 / 错误摘要失败且删 part、不发布），
    `HfDownloadViewModelTests` 加 1 例（换代理只重建自建客户端）。全量 **284 条全绿**（Core 208 + Ui.Mew 76）。
- **未做**：HF 侧的父任务汇总（仍是一文件一任务，`parent_id` 为 0）、文件级并发限流
  （交回 `ConnectionSlots` 前先按任务队列跑）、`hf_transfer` 式多连接加速。
- **后续**：torrent（MonoTorrent）仍未开始。

### 2026-11-22 · 多代理：代理表 + 默认代理 + 任务级代理选择

- **设置模型改为「代理表」**：`AppSettings.Proxies`（`ProxyProfile { Id, Name, Url }`）+ `DefaultProxyId`
  （`null` = 系统代理，`ProxyIds.Direct` = 直连，其余为具名代理 id）。旧字段 `ProxyMode` / `ProxyUrl`
  在 `Normalized()` 里一次性迁移成一条具名代理并清空，非法地址的行直接丢弃，未知默认 id 回落系统代理。
- **任务级选择**：新建下载窗口多一行「代理」单选（默认 / 直连 / 各条具名代理），选中的 id 落
  `DownloadRequest.ProxyId` → `StoredTask.ProxyId`，建任务时由 `ProxyResolver.Resolve` 解析成
  `ProxyRoute` 固化在 `DownloadTask.Route` 上；之后改设置不影响已建任务（重启按 `proxy_id` 重新解析）。
- **按路线分客户端**：`ProxyHttpClientProvider.For(route)` 懒建并缓存，一条路线一份连接池，
  `DownloadEngine` 的探测与 `SegmentRunner` 都改走 `ClientFor(task)`（不再全局一个 `RangeHttpClient`）。
- **持久化踩坑**：PureDb 不支持 `ALTER TABLE`，原打算给 `tasks` 加 `proxy_id` 列的迁移方案走不通
  （`Unsupported statement near ALTER`），改为新增 `task_extras(task_id, name, value)` 键值表；
  旧库靠 `CREATE TABLE IF NOT EXISTS` 自然补齐。顺带修掉 id 复用隐患：`Delete` 与 `Insert` 都清
  `task_extras`，避免删掉最大 id 后新任务继承旧代理（`MaxId()+1` 会复用 id）。
- **UI**：设置「网络」页 = 代理列表（名称/地址/删除 + 新增行）+ 默认代理单选组；代理选择一律
  `RadioButton`（`ComboBox` 自绘弹层不响应点击，见 `PLAN-MEWUI.md` 坑 11）。设置页默认代理最多列
  8 条（超出可在新建任务窗口选）。
- **测试**：新增 `ProxyResolverTests`(8) / `ProxyHttpClientProviderTests`(3) /
  `ProxySettingsViewModelTests`(6) / `ProxyChoiceTests`(4)，`AppSettingsTests` 换掉旧的单代理用例，
  `TaskStoreTests` 加 `Insert_RoundTripsProxyId` / `Delete_RemovesProxyExtraSoReusedIdStaysClean` /
  `Open_CreatesTaskExtrasOnLegacyDatabase`；`NewDownloadDialogTests` 加 5 例窗口级代理用例
  （单选组条数、默认预选为 `null`、点具名/直连后请求带对应 id、无代理时退化成两项）。
  全量 **236 条全绿**（Core 172 + Ui.Mew 64），构建 0 warning。
- **后续**：HF 模型下载（输入 model id → 选文件 → 下载）、torrent（MonoTorrent）尚未开始。

### 2026-11-22 · 删除 Avalonia 前端，MewUI 成为唯一前端

- **删除**：`src/Xdm.App`（7 个文件 + `Assets/xdm.ico`）、`src/Xdm.Ui`（视图/样式/转换器）、
  `tests/Xdm.Ui.Tests`（65 条 Headless 测试）。`XdmSharp.sln` 同步移除三个工程
  （用 `dotnet sln remove`，不动手改 GUID 块）。
- **保留 .Mew 命名**：目录/命名空间仍是 `Xdm.App.Mew` / `Xdm.Ui.Mew` / `Xdm.Ui.Mew.Tests`
  （用户决定：名字指明启动栈，不用去掉），只把**产物**改名：`AssemblyName` `xdm-mew` → `xdm`，
  发布产物统一 `xdm.exe`。
- **资产迁移**（`git mv`，历史不断）：
  `src/Xdm.Ui/Assets/xdm-logo.png` → `src/Xdm.Ui.Mew/Assets/xdm-logo.png`（改为本工程嵌入资源，
  `LogicalName` 不变）；`src/Xdm.App/Assets/xdm.ico` → `src/Xdm.App.Mew/Assets/xdm.ico`（`ApplicationIcon`）。
  `tools/make-icons.ps1` 的默认输出路径同步。
- **build.ps1**：去掉 `-Ui avalonia|mew` 开关（只剩一个前端），固定
  `publish/win-x64`（4.0 MB 目录）与 `publish/win-x64-aot`（15.4 MB 单 exe）。
- **文档**：`README`（技术栈/工程结构/命令/体积/测试条数）、`ARCHITECTURE`（§1 依赖图、§2 工程表、
  §2.1 渲染栈重写为 MewUI、"§2.2 为什么删掉 Avalonia"决策记录与四档实测、§7.1/§7.3/§9/§12 措辞）、
  `UI-DESIGN`（顶部实现说明 + §2 窗口清单 + §3/§4/§9/§10/§11 去 Avalonia 化 + §12 改为 MewUI 唯一实现）、
  `TESTING`（§4 整节重写为 MewUI 视图层测试：无 Headless、必须关并行、`TestPlatform` 按需注册、
  文案三处来源）、`PLAN`（总体原则/风险表改口径，M0–M2 标为 Avalonia 版历史记录）、
  `PLAN-MEWUI`（标"已完成并归档"，S5 的 `-Ui` 开关记录同步失效说明）。
- **回归**：`dotnet build XdmSharp.sln` 0 error / 0 warning；`dotnet test` **197 条全绿**
  （Core 155 + Ui.Mew 42）；`src/Xdm.App.Mew/bin/Debug/net10.0/xdm.exe` 生成正常。

### 2026-11-22 · M6 单实例 + 命令行传 URL（两个前端同时接）

- **Core 新增两件不依赖 UI 的能力**：
  - `Xdm.Core/Configuration/LaunchOptions.cs`：`Parse(args)` = `AppPaths.Resolve` + 捞第一个 HTTP/HTTPS 链接
    （跳过 `--db` 的值，`--db=x` 不吃下一个参数）；`IsHttpUrl` 只认 http/https。
  - `Xdm.Core/Shell/SingleInstanceHost.cs`：命名 Mutex 定主从（`Local\xdm-sharp-<key>`，键 = 库全路径小写
    SHA256 前 8 字节）+ 命名管道转发载荷（链接或空串=只激活）。`TryAcquire` 不取 Mutex 所有权，
    只用 `createdNew` 判定，避开「Mutex 归创建线程所有、换线程 Dispose 抛异常」；`Dispose` 先取消、
    再自连一下叫醒阻塞在 `WaitForConnection` 的监听线程，最后 Join 2s 兜底。
- **两个前端都接上**：`Program` 里**先抢单实例、再开任务库**（第二个进程不该碰主实例占着的库），
  抢不到就把 URL 递过去并退出（`0` 已交接 / `4` 交接失败）；主窗口侧 `BeginNewDownload*` 预填链接，
  与工具栏「新建」共用一条路径（Avalonia `BeginNewDownloadAsync`、MewUI `BeginNewDownload`）。
  Avalonia 用 `DispatcherPriority.Background` 延后弹框，MewUI 用 `Window.FirstFrameRendered`。
- **踩坑（已写进 `ARCHITECTURE.md §7.3`）**：冷启动带 URL 时，在 `Application.Run` 的 startup 回调里
  直接 `ShowDialog` 会把消息循环堵住——实测只剩「新建下载」一个窗口，主窗口永远不显示。
  MewUI 改成首帧后再弹；首帧之前到达的转发请求排队（`_ready` 闸门）。
- **验证**（Debug 产物，两个前端各跑一遍）：
  | 场景 | Avalonia | MewUI |
  |---|---|---|
  | 冷启动带链接 | 主窗口 + 预填 `https://example.com/cold.bin` 的对话框 | 同左 |
  | 运行中再启动带链接 | 第二进程 `delivered=True`、退出码 0；主窗口弹预填对话框 | 同左 |
  | 运行中再启动不带链接 | 第二进程退出码 0，只激活窗口 | 同左 |
  | 演示模式双开 | 两个进程都能起（不抢 Mutex） | 同左 |
- **测试**：`LaunchOptionsTests`（9）+ `SingleInstanceHostTests`（7，含管道往返、重复抢锁、不同库键互不影响）
  → 全量 **262 条全绿**（Core 155 + Ui 65 + Ui.Mew 42），构建 0 warning。

### 2026-11-22 · MewUI S5 完成：测试工程 + AOT 出包 + 双前端文档

- **新测试工程 `tests/Xdm.Ui.Mew.Tests`（42 条，全绿）**。MewUI 0.22 没有 Headless 宿主，
  但控件在 `Show()` 之前就是普通对象：结构类断言照样能走逻辑树，只有 `Measure/Arrange`
  需要按需注册 Win32 + Direct2D（失败则 `Assert.Skip`）。覆盖：主窗口结构与命令作用域、
  命令桥注册/注销/转发、四个对话框标题栏与文案、新建下载的预设与校验、输入框中文右键菜单、
  主题 token、300 行时的元素树规模（MewUI 用绘制代替行容器，等价于虚拟化断言）。
  坑写进 `PLAN-MEWUI.md` 23–26（控件非线程安全 → 测试串行；无 Headless；`AccessText` 只能反射读；
  `ListBox` 不建行元素）。为测试加了最小测试缝：`InternalsVisibleTo`、`MainWindow.ViewModelForTest`、
  `NewDownloadDialog.Submit()/ViewModel`、`MewCommandBridge.CanExecute()/Execute()`。
- **`build.ps1` 加 `-Ui`**（`avalonia` 默认 / `mew`）：只切发布工程与产物目录，
  构建与测试始终覆盖两个前端。顺手删掉 S1 遗留且不再被引用的 `Xdm.Ui.Mew/SpikeWindow.cs`。
- **出包与实测**（3 次取中位，空库、空闲 2s 后取样）：

  | 档位 | 产物体积 | 首帧（主窗口可见） | 工作集 |
  |---|---|---|---|
  | MewUI NativeAOT | **15.4 MB**（单 exe） | 304 ms | **91 MB** |
  | Avalonia NativeAOT | 30.0 MB（单 exe） | **123 ms** | 111 MB |
  | MewUI framework-dependent | 4.0 MB（目录） | 939 ms | 123 MB |
  | Avalonia framework-dependent | 11.1 MB（目录） | 1156 ms | 163 MB |

  结论：MewUI 档**体积减半、内存低约 18%**，AOT 首帧比 Avalonia 慢（304 vs 123 ms，
  差距在 Direct2D 设备/字体初始化），但比两个 framework-dependent 档都快。
  AOT 产物实机跑通：真实库建库 + 空态、演示模式 3 行（logo/图标/中文/速度剩余全部正常），
  一次反射相关的裁剪问题都没出现（MewUI 0.22.1 自身是 AOT 干净的）。
- **回归**：`dotnet build XdmSharp.sln` 0 error / 0 warning；`dotnet test` **241 条全绿**
  （Core 134 + Avalonia Ui 65 + MewUI 42）。`./build.ps1 -Ui mew -Aot` /
  `-Ui avalonia -Aot` 均一次成功。
- **文档**：`ARCHITECTURE.md` 新增 §2.4「双前端」与工程表补 `Xdm.Ui.Mew.Tests`；
  `UI-DESIGN.md` 新增 §12「MewUI 一致性约定」（含深色标题栏不能用 `Button`、`PathShape` 必须设
  `StrokeThickness` 等实现差异）；`README` 补 `-Ui` 开关、两条 `dotnet run`、测试条数与工程结构。

### 2026-11-22 · MewUI S4.5 完成：接真实引擎（组合根 / 事件桥 / 退出确认）

- `src/Xdm.App.Mew/MewRuntime.cs`：MewUI 壳的组合根。`AppPaths.Resolve` → `TaskStore` →
  `DbSettingsStore`（先 `ImportLegacyJson`）→ `SpeedLimiter` + `RangeHttpClient` +
  `DownloadManager` → `MainWindowViewModel` + `MewDownloadActions`；`SaveSettings` 先落库再套用
  （限速/并发/默认头即时生效，代理与重试参数下次启动生效）。
- `src/Xdm.App.Mew/MewDownloadBridge.cs`：引擎事件 → UI 线程，新增/删除立即投递，进度 250ms 合并 4Hz；
  在 `Application.Run(window, startup)` 的 startup 回调里构造（`DispatcherTimer` 必须属于消息循环线程）。
- `src/Xdm.App.Mew/Shell.cs`：行内「目录」打开资源管理器；`Program.cs` 改为真实装配 + 原生消息框报错
  （退出码 2 参数错、3 库不可用），保留 `XDM_DEMO` 演示模式（设置即演示，含 0 验空态）。
- `MainWindow`：`ConfirmOnExit` + `Closing` 确认，有任务在跑时提示「进度会保存」；新增
  `Controls/TitleBarChrome` 统一四个对话框标题栏（矢量 ✕，替换会在深色底上露出浅色方块的 `Button`）；
  新建下载对话框补 `saveFolder` / `segments` 预设，跟随设置。
- 修共享层 bug：`MainWindowViewModel.UpdateRow` 不刷分类计数（假数据永不变状态，接真引擎后
  「下载完仍显示 已完成 0」），改为状态变化时 `RefreshCounts()`。
- 实测（本地 `HttpListener` 提供 256KB + Range 源）：新库启动 → 新建下载 → 行变 `已完成`、
  `全部 1 / 已完成 1`；杀进程重启后任务从库恢复；设置改默认保存目录并保存，重启后目录页回显、
  新建下载继承；点 ✕ 弹退出确认、取消后留在主窗口；`XDM_DEMO=2` 演示模式回归通过。
- 回归：`dotnet build XdmSharp.sln` 0 error / 0 warning，`dotnet test` 199 条全绿
  （`NameConflictTests.Fail_...` 在并行满载时偶发失败一次，单跑与复跑均通过，属既有时间敏感用例）。
- 新坑写进 `PLAN-MEWUI.md` 19–22：`DispatcherTimer` 必须在消息循环后建、`UpdateRow` 不刷计数、
  对话框 ✕ 用 `Border` 自绘、新建下载漏传设置默认值。

### 2026-11-22 · MewUI S4 完成：命令桥 + 行级刷新 + 中文右键菜单 + 标题栏图标

- 新增 `src/Xdm.Ui.Mew/MewCommandBridge.cs`：共享层 `ICommand` → MewUI `Command`，
  在 `window.Commands.Register(execute, canExecute)` 里转调，`CanExecuteChanged` 转 `RequerySuggested()`。
  工具栏「恢复/暂停/删除」改挂命令，未选中行时置灰、执行走 ViewModel。
- 行模板补行级刷新：`RowBinding` 记录每行控件组，随容器复用改写映射，行 `PropertyChanged` 就地重刷，
  `Rows.CollectionChanged` 管订阅/退订。暂停后行内徽标、按钮文字、速度/剩余立即更新（此前要右键
  菜单关闭时碰巧重绑才刷新）。
- 新增 `src/Xdm.Ui.Mew/Controls/EditBox.cs`：中文右键菜单（撤销/剪切/复制/粘贴/全选，走
  `StandardCommands`，自带快捷键提示）；搜索框、新建下载三框、设置的保存目录/代理地址、请求头
  多行框全部换用。
- 标题栏对齐 `UI-DESIGN.md`：底色 `#1F2B3B` + `#2C3A4E` 底线、18px 透明版 logo（嵌入
  `Xdm.Ui.Assets/xdm-logo.png`）、三键改矢量字形（`PathShape`，hover 浮出色块、关闭键 `#C42B1C`）、
  最大化/还原换字形。
- 实机截图：未选中行按钮置灰 → 选中第三行点「暂停」→ 行变已暂停且状态栏 `1 个下载中`；
  搜索框右键五项齐全，全选+剪切后文本清空；关闭键 hover 变红；最大化铺满 2560×1600 且中间键变还原。
- 回归：`dotnet build XdmSharp.sln` 0 error / 0 warning，`dotnet test` 199 条全绿。
- 新坑写进 `PLAN-MEWUI.md` 14–18：`Button.Background` 压不住默认模板、`Border.MouseUp` 不可靠、
  行模板无属性绑定、`PathShape` 必须显式 `StrokeThickness`、`StandardCommands` 自带快捷键提示。

### 2026-11-22 · MewUI S3 完成：新建下载 / 设置 / 关于 / 确认框

- `src/Xdm.Ui.Mew/Dialogs/` 四个对话框全部落地，共用「自绘 30px 标题栏 + 1px 边框 + `Borderless` +
  `StartupLocation.CenterOwner` + `WindowDrag.Begin`」：
  - `ConfirmDialog`：`record struct ConfirmResult(bool Confirmed, bool OptionChecked)`，未完成任务只提示
    临时文件、已完成任务带「同时删除已下载的文件」勾选；`MainWindow` 的 `_vm.ConfirmDelete` 接到它。
  - `AboutDialog`：460×300，版本号读 `AssemblyInformationalVersionAttribute` 去 `+commit`。
  - `NewDownloadDialog`：链接 / 保存到（`FileDialog.SelectFolder`）/ 文件名 / 分段数 / 立即开始；
    校验全走 `NewDownloadViewModel.TryCreate`，提交后 `_vm.StartDownloads(requests)`。
  - `SettingsDialog`：`TabControl` 四页（常规/连接/目录/网络）+ 保存校验，控件值回灌
    `SettingsViewModel` 后调 `TryBuild`，失败在底部红字提示且不关窗。
- 接线：`MainWindow.Settings`（`AppSettings`）+ `MainWindow.SaveSettings`（`Func<AppSettings,string?>`），
  保存成功后立即覆盖 `DefaultSegments`/`DefaultSaveFolder`；S5 接 `ISettingsStore` 时只填这两个挂点。
- 搜索框补 `✕` 清空按钮（有输入才显示，点后列表恢复）。
- 修掉删除任务后左侧分类计数不刷新：订阅 `CategoryViewModel.PropertyChanged(Count)`，变化时重建
  `ItemsView` 并恢复 `SelectedItem`（实机 4→3）。
- 实机截图逐项核对：新建下载填链接→列表 2→3 行、状态栏 `显示 3 / 共 3 个任务`；设置四页布局、
  代理单选选「自定义」后出现代理地址行、清空保存目录后点保存报「默认保存目录不能为空」且窗口不关；
  搜索 `pdf` 过滤到 1 行、点 `✕` 回到 2 行。
- 新踩的坑已写进 `PLAN-MEWUI.md`：`ComboBox` 自绘弹层不响应点击（改用 `RadioButton`）、
  `TabItem.Header` 必须给元素、截图坐标要按对话框真实 `GetWindowRect` + DPI 换算。
- 回归：`dotnet build XdmSharp.sln` 0 error / 0 warning，`dotnet test` 199 条全绿。

### 2026-11-22 · MewUI S2 收尾：右键菜单 + 命令桥 + 拖入 URL

- 列表右键菜单接通：`ListBox.ContextMenu(...)` + 暂停继续/打开目录/复制链接/删除四个 `MenuItem`，命令读
  `SelectedRow`，右键按下先选中该行。菜单项只能挂 `Command`，处理器注册到窗口 `Commands`（`CommandScope`），
  并用 `menu.SetCommandTarget(CommandTarget.From(Commands))` 指过去——不设则点击毫无反应（已实测定位）。
  实机截图确认：右击第三行 →「暂停 / 继续」→ 该行变 `已暂停`、按钮变 `恢复`、状态栏 `3 个下载中`。
- 拖入 URL：`AllowDrop` + `DragOver`/`Drop`，按空白切分 http(s) 链接、从路径取文件名建任务并提示；
  `AllowDrop` 是逐元素命中，`ListBox` 也要单独设，否则指针在列表上收不到事件。
  **未做端到端验证**：用 PowerShell + WinForms `DoDragDrop` 模拟 OLE 拖放时目标端收不到任何事件，需真机手工拖。
- 搜索框补 `Placeholder("搜索任务")`（之前是空框，看不出用途）。
- 回归：全量构建 0 警告，199 测试全绿。

### 2026-11-22 · MewUI S2 主窗口落地（标题栏/工具栏/分类栏/行模板/状态栏）

- `src/Xdm.Ui.Mew/MainWindow.cs` 从 `SpikeWindow` 升级为真正主窗口：30px 自绘深色标题栏（拖动 +
  最小化/最大化/关闭）、40px 工具栏（新建/恢复/暂停/删除 + 居中搜索框 + 设置/关于/退出）、
  180px 分类栏（全部/正在下载/已完成/失败 + 计数 + 选中高亮）、任务列表行模板（扩展名徽标 /
  文件名 / `SegmentBar` 分段进度 / 进度与目录 / 大小 / 速度与剩余 / 状态徽章 / 暂停·目录·删除）、
  空态提示、24px 状态栏；`Del` 删除、`Ctrl+N` 新建；`XDM_DEMO=<n>` 造演示数据（`0` 验空态）。
- 新增 `Controls/RowHost.cs`：MewUI 条目容器铺满整行但按模板根 `DesiredSize` 摆放内容，
  星号列只算内容宽度，导致右侧定宽列被摆到行外；`Grid` 子类把测量宽度报到可用宽度解决。
- 修正 S1 误判：`WindowSize` 是 DIP 语义，之前乘 `DpiScale` 把窗口放到 1750 DIP（3062 px），
  内容右半跑到屏幕外；同时弃用手工 `MoveTo`/`SetWindowPos`，改 `StartupLocation.CenterScreen`。
- 踩坑 8 条记入 `docs/PLAN-MEWUI.md` §五 S2（`DockPanel` 附加属性无效、分类栏容器宽度反向撑开、
  `HorizontalScroll=Auto` 常驻滚动条、自绘控件要 `MeasureOverride` + `Bounds.X/Y` 原点、
  截图脚本必须 DPI 感知等）。
- 回归：全量构建 0 警告，199 测试全绿（Xdm.Ui 65 + Xdm.Core 134）。

### 2026-11-22 · 抽出 Xdm.Ui.Shared，MewUI 前端 S1 验证通过

- 新工程 `src/Xdm.Ui.Shared`：`ViewModels/`、`Services/IDownloadActions.cs`、`Formatting/` 从
  `Xdm.Ui` 整体平移（命名空间仍是 `Xdm.Ui.*`），`Xdm.Ui` 改为引用它。199 测试全绿、0 警告，
  Avalonia 前端行为零变化。
- 新工程 `src/Xdm.Ui.Mew`（MewUI 视图，code-first）+ `src/Xdm.App.Mew`（Win32 + Direct2D 壳），
  包锁 0.22.1 在 `Directory.Packages.props`：视图层引 `Aprillz.MewUI.Core`，壳引
  `Aprillz.MewUI.Backend.Direct2D` + `Aprillz.MewUI.Platform.Win32`（不引 Windows 元包，
  实测发布产物省掉 MewVG.Core + MewVG.GL 共 196 KB）。
- S1 四项硬点实测通过：①`Borderless` 自绘 30px 标题栏用 `ReleaseCapture` +
  `WM_NCLBUTTONDOWN/HTCAPTION` 拖动，位移与模拟鼠标完全一致；②Direct2D 下 13/11px 中文清晰；
  ③1000 行 `ListBox` 只创建可视区容器、滚动正确；④右键菜单弹出定位正常（独立分层窗口）。
- 实机踩坑 9 条（`ItemHeight` 是行高真值、`Content`/`Borderless` 是属性、模板里要用 `TextBlock`、
  窗口尺寸是物理像素等）记入 `docs/PLAN-MEWUI.md` §五 S1，S2 照此写。
- 文档：`PLAN-MEWUI.md` 新增 S1 结论与风险表状态；`ARCHITECTURE.md` §1/§2/§9 改为双前端结构；
  `README` 目录结构与命令同步。

### 2026-11-22 · logo 透明化并贴到标题栏

- `tools/make-icons.ps1` 重写：从根 `logo.png` 生成**透明底**资源，不再做白底圆角牌。
  关键处理是背景不能按固定阈值切——原图底色是 240~255 的渐变，改为从四边洪泛近白连通区；
  箭头本体（min ≤ 200）保留原配色，中间浅色辉光按白色混入比例给半透明并反合成去白
  （否则深色标题栏上会浮出一圈白雾），alpha 再做 3×3 均值消除边界碎齿，最后按内容包围盒裁正方形。
- 产物：`src/Xdm.Ui/Assets/xdm-logo.png`（256×256 透明底，标题栏 18px / 关于页 52px / 窗口 Icon 共用）、
  `src/Xdm.App/Assets/xdm.ico`（同一透明底十帧）。
- `TitleBar.axaml` 的 logo 加 `BitmapInterpolationMode="HighQuality"`；实机 PrintWindow 抓图确认
  深色标题栏上是透明底箭头，没有白块。
- 文档：`UI-DESIGN.md` §11.3 改述透明化规则并注明「不要再用白底圆角牌」。

> 每次提交/里程碑收尾追加一条，倒序（最新在上）。

### 2026-11-22 · 续传改为库权威 + 删除确认 + 中文右键菜单 + 系统下载目录

- **断点续传去掉 json 依赖**：`DownloadManager` 用 `writeStateFiles: false` 构造引擎，段表只落
  `xdm.db` 的 `segments` 表；`Resume()` 优先用库回填到任务对象上的段（`TryRestoreTaskSegments`），
  只在任务没有段表时才读旧版 `state/<id>.json`。删掉 `WriteStateIfMissing` 补写逻辑，
  重启恢复不再碰文件。段表校验（首尾相接 / `Σ length == TotalSize` / `0 ≤ synced ≤ length`）
  两种来源共用，不合法整任务重来。
- **删除任务加确认**：`ConfirmDialog` 支持一个可选勾选项，删已完成任务时弹「同时删除已下载的文件」
  （默认不勾）；未完成任务只删 `.xdm-part`。`MainWindowViewModel.DeleteAsync` 走窗口注入的
  `ConfirmDelete`，同一任务重复点（确认框还开着）直接忽略；`IDownloadActions.Delete` 增加
  `deleteFile` 参数，`DownloadManager.Delete` 负责删已发布文件（改名策略下用 `Entry.FinalPath`）。
- **右键菜单中文化**：新增 `Controls/EditTextBox`（`TextBox` 子类，`ContextFlyout` 换成
  撤销/剪切/复制/粘贴/全选，带 Ctrl+Z/X/C/V/A 提示，`Opening` 时按选区刷新可用项），
  主窗口搜索框、设置页、新建下载窗口的 URL 输入框全部换用。
- **默认下载目录走 Windows API**：`KnownFolders.Downloads()`（`SHGetKnownFolderPath`，
  FOLDERID_Downloads），用户把「下载」搬到别的盘 / OneDrive 接管也能跟随；取不到时回退
  `%USERPROFILE%\Downloads`。
- 验证：build 0 警告 0 错误；199 测试全绿（Core 134 + Ui 65）。新增：删文件/留文件、已知文件夹
  （含注册表 `Shell Folders` 对拍）、删除取消/勾选、右键菜单五项与快捷键、确认框勾选显隐、
  删除确认接线、默认目录绝对路径。
- 踩坑：`TextBox` 子类拿不到主题——Avalonia 类型选择器按精确类型匹配，`Template` 为 null
  等于隐形控件；`protected override Type StyleKeyOverride => typeof(TextBox)` 复用父类型样式
  （由 `KeepsThemeTemplate` 测试守住）。

### 2026-11-22 · 品牌图换代：logo.png 重做图标 + README banner

- 根目录 `logo.png`（480×480，白底 + 蓝紫渐变箭头 + 光带）成为唯一图标源，新增 `tools/make-icons.ps1`：
  4 倍超采样画 22% 圆角路径再缩回，生成 `xdm-logo.png`（256×256，圆角外全透明）
  与 `xdm.ico`（16/20/24/32/40/48/64/96/128/256 十帧，手工拼 ICONDIR + PNG 帧）。
- 旧深色圆形徽标废弃：标题栏底色 `#1F2B3B` 不再需要与 logo 融为一体，18px 白牌是有意的品牌块；
  关于窗口 52px、任务栏、资源管理器同源。
- 踩坑：PowerShell 函数 `return $bytes`（`byte[]`）会被管道拆成 `object[]`，`BinaryWriter.Write`
  写出坏 ico，CSC 报 `CS7065: 生成 Win32 资源时出错 -- Unable to read beyond the end of the stream`；
  改成 `return , [byte[]]$stream.ToArray()` 保住数组类型。
- README 顶部换成 `banner.jpg`（居中 `<img width="100%">`）+ 五条能力速览；工程结构补
  `logo.png` / `banner.jpg` / `tools/make-icons.ps1`，常用命令补图标重生成一行。
- 验证：build 0 警告 0 错误、186 测试全绿；截图确认标题栏 18px 与关于窗口 52px 显示正常。

### 2026-11-22 · 配置入库存 `settings` 表 + NativeAOT 发布档（win-x64）

- 配置不再写 `settings.json`：`TaskStore` 新增 `settings` 表（一份 `AppSettings` 一行），
  `DbSettingsStore` 实现 `ISettingsStore`，写路径「`UPDATE` 影响 0 行 → `INSERT`」在同一事务内。
  任务与配置同库，备份/搬家只剩一个 `.db`；设置页「设置文件」改为「设置存放（与任务同库）」。
- 旧 `settings.json` 启动时一次性迁移：库里已有配置则跳过，导入成功才删文件，损坏则原样保留并按默认值启动。
  删除 `JsonSettingsStore`，其测试改用 `DbSettingsStore` + `:memory:`（185 → 186 条）。
- NativeAOT（win-x64）：`Xdm.App.csproj` 加 `PublishAot` 条件组（RID 固定、SelfContained、StripSymbols），
  `build.ps1` 加 `-Aot` / `-KeepAotSymbols`。
  - 踩点 1：`System.Text.Json` 反射序列化在 AOT 分析下是 IL2026/IL3050，仓库开了
    `TreatWarningsAsErrors` 直接编译失败 → 改 source-generated `XdmJsonContext`
    （`[JsonSerializable]` + `UseStringEnumConverter`），`AtomicJson.Write/Read` 与`TaskStore` 的
    headers 序列化都改成显式传 `JsonTypeInfo<T>`。**新增落盘类型必须补 `[JsonSerializable]`**，
    否则 AOT 包运行时抛"Metadata for type ... was not provided"。
  - 踩点 2：PureDb 要求每表有且仅有一个 `INTEGER PRIMARY KEY`，`TEXT PRIMARY KEY` 建表直接失败 →
    settings 表用固定 `id=1`。
  - 踩点 3：MSBuild 的 `AfterTargets="Publish"` 早于 ILC 符号拷贝，删不掉 `xdm-app.pdb`（~118MB）→
    改由 `build.ps1 -Aot` 发布后清理。
- 验证：`dotnet build` 0 警告 0 错误；186 全绿；`build.ps1 -Configuration Release -Aot` 产出
  `publish\win-x64-aot\xdm-app.exe`（30.1MB，目录里只有它）；实机跑 AOT 包：列表/分类绑定正常，
  设置 → 保存后 `xdm.db` 含配置 JSON、**没有** `settings.json`，截图确认设置页文案。

### 2026-11-22 · 工具栏/对话框按钮统一加矢量图标

- 工具栏 7 个按钮全部「12px 图标 + 5px 间距 + 文本」：新建 `plus`、恢复 `play`、暂停 `pause`、
  删除 `delete`、设置 `cog`、关于 `information`、退出 `logout`；确认框「取消 `close` / 确定 `check`」。
  原先只有 `＋ 新建` 一个全角加号、`⚙ 设置` 一个 emoji，其余纯文字，风格不齐。
- 图标用 Avalonia 内置 `PathIcon`（`Data` 写 Material Design Icons 的 24×24 path 字符串），
  不用 emoji：彩色字形在 125%/175% DPI 下发糊，还依赖 Segoe UI Emoji 渲染差异。
  `PathIcon` 的 `Fill` 取自继承的 `Foreground`，所以**按钮禁用时图标自动变灰**——
  截图实测未选中行时「暂停/删除」图标与文字同为浅灰，选中后变正常深色，accent 的「新建」是白色。
- 踩点：按钮 `Content` 变成 `StackPanel` 后 UIA 名不再是文本，给每个按钮补
  `AutomationProperties.Name`；`ConfirmDialog` 的按钮文案可在构造里换（退出确认时是「退出」），
  所以改成设置内部 `OkText`/`CancelText` 两个 `TextBlock` 并同步 `AutomationProperties.SetName`，
  不再给 `Button.Content` 赋字符串。测试断言相应改为读 `TextBlock.Text` + `GetName` + `IsType<StackPanel>`。
- 行内悬停按钮（`DownloadRow` 的暂停/目录/删除）保持纯文字 11px：行高 56 加图标会挤，且是次要操作。
  规范写进 `UI-DESIGN.md` §3.1（图标对照表 + `PathIcon` 禁用态约束 + 可访问性要求）。
- 补齐其余对话框的同类按钮，避免「同一个词有的带图标有的不带」：
  关于窗口「关闭」、设置窗口「关闭」、新建下载窗口「取消」都加 `close`（✕）图标并设 `AutomationProperties.Name`。
  仍未加图标的只剩设置窗口「保存/浏览…」与新建窗口「添加/浏览…」——
  「保存/添加」是动词性主操作，等有明确的 `check`/`plus` 语义再统一，`浏览…` 本身是超短次要按钮。
- 验证：`dotnet build` 0 警告 0 错误；Core 129 + Ui 56 = **185 全绿**；实机截图确认工具栏与确认框图标正常，
  退出确认链路（点退出 → 取消 → 再退出 → 确认进程退出）复跑通过。

### 2026-11-22 · 标题栏压到 30px、圆形 logo、关于/退出（含退出确认）

- 标题栏 36 → **30px**（对齐 Neat Downloader 一类第三方自绘标题栏；系统默认 32 偏厚）。
  按钮 46×36 → 44×30，logo 22 → 18，左距 10 → 8，间距 8 → 7。实测截图深色区 53 物理 px
  @175% DPI，与工具栏 40px 比例正常。
- `TitleBar` 新增 `ShowMinimize`：模态对话框（新建下载 / 设置 / 关于 / 确认框）设 `False`，
  标题栏只剩「✕」，不再出现点了会把模态框最小化的怪按钮。
- **logo 改圆形**：`logo.png` → `xdm-logo.png` 用「铺满 + 椭圆裁剪」重做，圆外全透明、
  边缘 1px 抗锯齿；图案最外点距圆心 ~240 < 半径 255，光带端头不会被切；
  原图底部残留的两个白角（采样 (2,509)=#F3F8FB）本来就在四角，落在圆外自然裁掉。
  ico 同步重打十帧圆形。踩坑：先用 `DrawImage` 内缩 7% 再圆裁，得到的是「圆角方形/八角形」——
  绘制矩形小于圆时，圆边那一圈没有内容，看起来像方角；必须让内容铺满整个圆再裁。
  又试过「圆内填底色 + 图案内缩 15%」，结果底部白角被缩进圆里变成两个白三角（用 Graphics 的
  ColorMap 也匹配不到渐变的背景），最后回到铺满方案。
- 工具栏右侧新增 **[关于] [退出]**：
  - `Views/AboutWindow.axaml`：圆形 logo 52px、版本（取 `AssemblyInformationalVersion`，去 `+hash`）、
    GPL-3.0 与参考实现声明、技术栈，460 宽 × 内容自适应高。
  - `Views/ConfirmDialog.axaml`：通用确认框（`ConfirmDialog.ShowAsync(owner, title, message, ok, cancel)`），
    440 宽 × 内容自适应高，Alt+F4/✕ 关闭按「取消」处理（`ShowDialog<bool>` 返回 false）。
- 退出确认：`MainWindow.ConfirmOnExit`（组合根设 `true`，无头测试保持 `false` 以免模态框挂住用例）。
  重写 `OnClosing` 拦截，点「退出」与关窗口（标题栏 ✕ / Alt+F4）统一走 `ConfirmAndExitAsync`，
  文案带 `MainWindowViewModel.ActiveCount`（不受分类过滤影响）；确认后置 `_exitConfirmed` 再 `Close()`
  防递归。UIA 实测：点「退出」→ 弹框（772×342）→「取消」→ 窗口仍在、进程存活 →
  再「退出」→「退出」→ 进程退出（`alive=False`）。
- 测试：新增 `AboutWindow_Loads`、`ConfirmDialog_Loads_And_Exposes_Text`；
  Core 129 + Ui 56 = **185 全绿**，`dotnet build` 0 警告 0 错误。

### 2026-11-22 · 窗口外观：自绘深色标题栏 + 窗口略缩 + 图标

- 系统 chrome 全关：三个窗口统一 `WindowDecorations="BorderOnly"`。试过
  `ExtendClientAreaToDecorationsHint` + `TitleBarHeightHint`，Avalonia 12.1.6 下系统标题栏仍在
  （截图里 logo 与系统标题文字重叠、左侧被边框挤压）；`ExtendClientAreaChromeHints` 在 12.x 已移除，
  `HasTitleBar` 只存在于 `Avalonia.Controls.Chrome.WindowDrawnDecorations`（不能当附加属性用）。定案 `BorderOnly`。
- 新增 `Ui/Controls/TitleBar.axaml(.cs)`：36px 深色条（`#1F2B3B` 底色取 logo 背景、`#2C3A4E` 分隔线），
  左 22×22 logo + 12px SemiBold 标题，右最小化/最大化/关闭各 46×36（关闭键 hover `#C42B1C`）。
  空白处 `BeginMoveDrag` 拖动、双击切换最大化、最大化时按钮换「还原」双框图；
  `Title` / `ShowMaximize` 两个 `StyledProperty`，`SyncState()` 在 `TitleText is null` 时直接返回
  （XAML 加载期属性先于子控件就位，用 `IsInitialized` 判会抛空）。
- 尺寸：主窗口 1080×680 → **1000×620**（MinWidth 720）；新建下载 580×470 → 580×500；
  设置 620×540 → 620×570（两者补标题栏后留出高度）。规格进 `UI-DESIGN.md` §11。
- 图标：根目录 `logo.png` 处理成 `Ui/Assets/xdm-logo.png`（`AvaloniaResource`，窗口 `Icon` + 标题栏）
  与 `App/Assets/xdm.ico`（`ApplicationIcon`，16/20/24/32/40/48/64/96/128/256 十帧）。
  处理方式：白角转透明，边缘按亮度算 alpha（`255*(255-mn)/115`）保柔边，RGB 填标题栏底色避免白晕。
- 一期范围收敛到 HTTP/HTTPS：新建下载底部提示改为「一期只支持 HTTP / HTTPS 直链（每行一个）；
  HuggingFace 与 BT 后续版本再加。」页签骨架仍保留（V2/V3 只加页，不动窗口）。
- 实测（非 Headless，物理像素）：双击最大化 `-12,-12 2352x1624` → 再双击还原 → 拖动位移 +210,+136；
  `BorderOnly` 下右/下边缘仍可 resize；关闭键使进程退出。
- 测试：新增 `Window_Uses_SelfDrawn_TitleBar`（主板）与 `WindowChromeTests` 两条（新建下载 / 设置，
  顺带覆盖两个对话框的 XAML 加载——原先没有测试构造它们），断言 `WindowDecorations=BorderOnly`、
  视觉树里正好一个 `TitleBar`、`Title`/`ShowMaximize` 正确；Core 129 + Ui 54 = **183 全绿**，
  `dotnet build` 0 警告 0 错误。
- 踩坑：上一轮 `dotnet test` 残留的 `testhost` 进程锁住 `Xdm.Core.Tests.dll` 导致 MSB3027，
  `taskkill` 报「no running instance」，最后用 `Get-CimInstance Win32_Process | Invoke-CimMethod Terminate` 才清掉。

### 2026-11-22 · 存储引擎 SQLite → PureDb

- 依赖：`Directory.Packages.props` 去掉 `Microsoft.Data.Sqlite 10.0.15`，改 `PureDb 1.0.0-rc.1`
  （`net10.0`，纯托管无原生依赖）。`Xdm.Core` / `Xdm.App` 的 `SqliteException` → `PureDb.SqlException`。
- `Core/Tasks/TaskStore`：**全局单连接 + 全局锁**——构造时打开唯一一条 `PureDbConnection`
  （`Data Source=<path>;Access=Shared`）并持有到 `Dispose`，所有公开方法在 `_gate` 内串行执行，
  不再每方法新建连接（PureDb 无连接池，多连接各缓存页会读到旧数据）。去掉 WAL / `PRAGMA` /
  `user_version`。方言限制按实测处理：段表加自增 `id` 列 + `(task_id, seg_id)` 唯一索引
  （PureDb 不支持复合主键）；`Insert` 在事务内取 `MAX(id)+1` 显式写 id（无 `last_insert_rowid()`）；
  冲突仅剩"另一个进程实例抢同一库"，仍由 `Run(...)` 退避重试 `DBConcurrencyException` 与
  唯一索引冲突兜底；事务内命令显式挂 `DbCommand.Transaction`；`:memory:` 映射到
  `%TEMP%\xdm-memory\` 临时文件，`Dispose` 时连同 `.lock` / `.pidx` 一起删除。
- 保护：检测到旧 SQLite 文件头（`SQLite format 3\0`）时抛 `InvalidOperationException`，
  提示备份后删除或改名，不尝试就地迁移。
- 数据文件：`xdm.db` 旁变为 `.lock`（访问锁）+ `.pidx`（页索引），原 `-wal` / `-shm` 不再产生；
  README / ARCHITECTURE §7.1 / PLAN 风险表已同步。
- 测试：`TaskStoreTests` 的 WAL / `sqlite_master` 断言改为查表验证；Core 129 + Ui 51 = **180 全绿**；
  `dotnet build` 0 警告 0 错误。冒烟：`--db %TEMP%\xdm-puredb-smoke\xdm.db` 首次生成
  `xdm.db/.lock/.pidx`（文件头 `PUREDB01`），结束进程后二次启动正常。

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
