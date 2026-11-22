# Xdm 总体计划

参考实现：`refcode/xdm-master`（XDM，Kotlin）。本项目目标是 Windows 下的普通 HTTP 下载器，
C# + MewUI（Win32 + Direct2D）+ .NET 10，命名空间根 `Xdm`。产物 `xdm.exe`。

> 2026-11-22：原 Avalonia 前端已删除，MewUI 成为唯一前端。下方 M0–M5 的历史记录保留原文
> （带「Avalonia 版」字样），它们记录的是当时的验收事实；现状以 `ARCHITECTURE.md` / `README.md` 为准。

---

## 一、总体原则

1. **界面先行**。先用假数据把主窗口、列表行、进度可视化、新建对话框、设置页跑通，再实现引擎。
   理由：UI 框架的控件能力与列表虚拟化方案是唯一的不确定性来源，先证实它，
   避免引擎写完后被 UI 方案推翻（这次换到 MewUI 也印证了：`Xdm.Core` 一行没改）。
2. **分层单向依赖**：`Xdm.Core` ← `Xdm.Ui.Shared` ← `Xdm.Ui.Mew` ← `Xdm.App.Mew`。
   `Core` 不引用 UI 框架，视图层不引用平台/后端（注册只在壳里），`App.Mew` 只做组装
   （启动链、DI、窗口生命周期）。测试工程因此可以只依赖视图层，跑得快、也不受渲染后端牵连。
3. **渲染栈可替换**。现行是 MewUI（`Win32Platform` + `Direct2DBackend`），切换点只在
   `src/Xdm.App.Mew/Program.cs` 与壳的包引用；约束见 `ARCHITECTURE.md` §2.1。
4. **借鉴设计，不逐行抄**。算法语义必须一致（`synced` 进度、`If-Range` 用 `Last-Modified`、
   Range 能力判定、拒绝分类），但 .NET 有更好的原语（`RandomAccess`、`SocketsHttpHandler`、
   `System.Text.Json`），能用框架替代的一律替代。
5. **并发模型沿用参考实现的"阻塞 IO + 专职线程"**，不改成 `async/await`。
   Kotlin 那边用 `ReentrantReadWriteLock` 保护分段表，锁内做状态迁移；C# 里 `lock` 不能跨
   `await`，一旦 async 化就要重写整套并发正确性论证，风险远大于收益。
6. **一切可测**。引擎用自建 TCP 级假 HTTP 服务器测（可造畸形响应、断流、无视 Range）；
   视图用 `Xdm.Ui.Mew.Tests` 测（不建窗口遍历控件树、命令桥直接调 `CanExecute/Execute`）。见 `TESTING.md`。
7. **每个里程碑收尾必做**：`dotnet build`（0 警告）+ `dotnet test`（全绿）+ 手工冒烟，
   结果记入 `PROGRESS.md`。

---

## 二、首期范围

### 做

- HTTP/HTTPS 单文件下载，多段并发，段数上限可配（上限 256，同参考实现 `CoreConfig.MAX_SEGMENTS`）
- 动态二分切分、段接管、尾段竞速（可分期交付，见 M6）
- 断点续传（`.xdm-part` + 状态文件），崩溃后从"已落盘"进度恢复
- 任务队列：并发数限制、暂停/恢复/删除、失败重试
- 限速（全局，KB/s）、代理（HTTP/SOCKS）、自定义 UA / Referer / Cookie / 任意请求头
- 文件名推导（`Content-Disposition` → URL → 兜底）、重名策略（覆盖 / 自动改名 / 失败）
- 主窗口列表、详情（分段时间轴）、新建下载、设置页
- 完成通知

### 不做（首期，但架构预留）

- HF（HuggingFace）模型下载 —— **V2 主线**，多文件 + LFS 校验 + Hub API，见 `ARCHITECTURE.md` §12.2
- BT / 磁力下载 —— **V3**，独立引擎（peer swarm），共享存储/限速/任务库，见 §12.3
- 视频嗅探、HLS/DASH 下载与转封装、字幕
- 浏览器扩展与 Native Messaging、剪贴板 URL 监控
- 定时任务、关机后动作（校验和校验会随 V2 的 LFS sha256 一起落地）
- 多语言（先中文，字符串集中管理，后续可加）

> 首期**不做不等于不管**：任务模型、数据来源接口、任务库 schema、UI 行模板都必须
> 预留"多协议 / 多文件 / 带校验"的形状，约束见 `ARCHITECTURE.md` §12.1。

---

## 三、里程碑（界面优先）

> 依赖关系：M0 → M1 → M2；M3 独立于界面，可与 M1/M2 并行；M4 需要 M1 + M3。

> **M0–M2 的条目是 Avalonia 版的验收记录**（该前端已于 2026-11-22 删除）。同名能力在 MewUI 版上
> 已由 `docs/PLAN-MEWUI.md` 的 S1–S5 重新交付，数字见 `PROGRESS.md`。保留原文只为追溯决策顺序。

### M0 地基（0.5–1 天）✔ 已完成（2026-11-22）

- `XdmSharp.sln` + 5 个工程（`Xdm.Core` / `Xdm.Ui` / `Xdm.App` / `Xdm.Core.Tests` / `Xdm.Ui.Tests`）
- `Directory.Build.props`（`net10.0`、`Nullable=enable`、`ImplicitUsings=enable`、
  `TreatWarningsAsErrors=true`、`Deterministic=true`）+ `Directory.Packages.props`（中央版本）
- `Xdm.App` 启动链（SimdAIO）：`UseStandardRuntimePlatformSubsystem()` + `UseWin32()` +
  `Win32RenderingMode.Software` + `UseSimdSkia(SimdSkiaGpuOptions.UseGpuPreferred)` + `LogToTrace()`，
  **全部集中在 `Program.cs` 的 `BuildAvaloniaApp()` 里**（`ARCHITECTURE.md` §2.1）
- `Xdm.Core/Configuration/AppPaths.cs`：库文件路径解析 —— `--db <path>` > `XDM_DB` > `XDM_DATA_DIR`
  > exe 目录 > `%LOCALAPPDATA%\Xdm`；`state/` 跟随库所在目录（`ARCHITECTURE.md` §7.1）。
  `Xdm.App` 解析命令行后注入，Core 不读 `Environment.GetCommandLineArgs()`
- `.gitignore`、`build.ps1`
- 验收：`dotnet run` 出窗口；`dotnet build` 0 警告；`dotnet publish -r win-x64` 产物可运行；
  `dotnet run -- --db .\dev.db` 时库落在 `.\dev.db`、`state\` 建在同级（`--db` 指目录时返回码 2）
- **实测（2026-11-22）**：0 警告 0 错误；15 条测试通过（Core 12 + Ui 3）；
  publish 单文件 `xdm-app.exe` 11.7 MB 可运行；启动到窗口 750 ms、私有内存 99.9 MB；
  `--db` 生效，`--db C:\Windows` 返回码 2

### M1 主界面骨架（2–3 天，假数据）✔ 已完成（2026-11-22）

- 主窗口：工具栏（新建/暂停/恢复/删除/设置）+ 左侧分类 + 主列表 + 状态栏
- 列表行：文件名、大小、进度条、速度、剩余时间、状态徽标、行内操作按钮
- 列表虚拟化验证：5000 条假任务滚动帧率、1Hz 全量进度刷新时的 CPU
- 新建下载窗口（URL 多行、保存目录、段数、文件名）、进度详情窗口（分段时间轴）、设置页骨架
- 行模板与 ViewModel 就按 `UI-DESIGN.md` §4.1 的"父子两级"形状定义（首期子集合为空），
  免得 V2 的 HF 多文件列表推翻 XAML
- 列表容器**已定案**：`ListBox` + `ItemTemplate` + `VirtualizingStackPanel`，
  **不引 `Avalonia.Controls.DataGrid`**（见 `UI-DESIGN.md` §3）
- 验收：假数据下界面完整可点、滚动流畅，1000 行假数据下滚动不掉帧
- **实测（2026-11-22）**：1000 行假数据下实际实例化 item 数 1–100（虚拟化生效）；
  Debug 私有内存 116.8 MB、启动到窗口 ~1.3 s（Release 数据见 M0 条目）；`dotnet build` 0 警告，
  测试 55 条全绿。**进度详情窗口（分段时间轴）未做**，推迟到 M4 接好真实分段数据再实现；
  "5000 行滚动帧率"改用 1000 行 + 实例化/内存增量衡量（软件光栅下逐帧数值不可靠）

### M2 界面测试基建（1 天）✔ 已完成（随 M1 一并落地，2026-11-22）

- `Xdm.Ui.Tests` 接 `Avalonia.Headless.XUnit`，`AvaloniaTestApplication` 配好
- 写通 3 类样板：控件树断言、点击/输入模拟、ViewModel 状态驱动 UI
- 验收：`dotnet test tests/Xdm.Ui.Tests` 无窗口跑绿
- **实测（2026-11-22）**：43 条界面测试（控件树、快捷键、分类过滤、虚拟化），`dotnet test` 无窗口约 1 s

### M3 引擎核心（5–7 天）✔ 已完成（2026-11-22）

分三步，每步都有测试：

1. 单段下载：请求 → 响应头解析 → 落盘 → 完成发布（含 `Content-Disposition` 文件名、
   无 `Content-Length` 的 EOF 模式）
2. 多段：Range 能力判定（200 / 长度不符 / 无 `Content-Length`）→ 固定等分 →
   动态二分切分 → 段接管 → 尾段竞速
3. 续传：`.xdm-part` + 状态文件（只记已 fsync 的字节）→ 崩溃恢复 → `If-Range` 校验 →
   服务器拒绝分类（限流 vs 链接过期）

- 验收：`TESTING.md` §3 矩阵中 M3 对应项全绿
- **实测（2026-11-22）**：第 1、3 步全部完成；第 2 步只做了**固定等分**，
  动态二分 / 段接管 / 尾段竞速与连接数协商按计划**转入 M6**（它们需要多任务队列与
  实时调度器才谈得上收益，单任务阶段无从压测）。Core 60 条测试全绿（总计 103），
  4 段下载逐字节与源文件一致，暂停后恢复的请求确实从非零 offset 续读。
  未做：限速、磁盘预留、下载中 `If-Range` 失配的在线重切（当前按失败处理，暂停恢复会整任务重来）。

### M4 接线（2–3 天）✔ 已完成（2026-11-22）

- `DownloadManager`（队列、并发闸门、状态机）+ 事件总线
- ViewModel 订阅引擎进度，4Hz 节流 + `Dispatcher.UIThread.Post` 上屏
- 验收：真实 URL 下载全程 UI 正确；100 任务并发时 UI 不卡
- **实测（2026-11-22）**：`Core/Scheduling/DownloadQueue`（FIFO）+ `Core/Downloads/DownloadManager`
  （闸门 3、任务快照、暂停/恢复/删除/全部暂停）已落地并单测；`App/DownloadUiBridge` 把
  `TaskAdded/TaskRemoved` 立即 `Post`，进度进 `ConcurrentDictionary` 由 250ms `DispatcherTimer` 合并。
  界面命令走 `Ui.Services.IDownloadActions`，`MainWindowViewModel` 不再自己改状态。
  **进度详情窗口未做**（仍缺分段时间轴 UI）；"100 任务并发不卡"未压测——真实任务需要先有任务库，
  连同 M5 一起验。演示数据改为 `XDM_DEMO=1000` 环境变量启动，正常启动走真实引擎（空列表）。

### M5 任务库与队列（2–3 天）✔ 已完成（2026-11-22）

- 库文件 `xdm.db` 固定放 **exe 同目录**（`AppContext.BaseDirectory`），
  不可写时回退 `%LOCALAPPDATA%\Xdm\`（`ARCHITECTURE.md` §7.1）
- 任务表（见 `ARCHITECTURE.md` §7，存储引擎 2026-11-22 由 SQLite 改为 PureDb）：**建表即带** `kind` / `parent_id` /
  `content_sha256` / `source_meta_json`，首期写默认值，不留 ALTER 迁移
- 启动恢复：未完成任务转暂停态、已完成后校验文件存在性
- 暂停/恢复/删除（含 `.xdm-part` 清理）、重名策略、发布到目标目录的原子改名
- 验收：杀进程后重启，任务列表与进度完整；发布过程断电不留半个文件
- **实测（2026-11-22）**：`Core/Tasks/TaskStore`（tasks/segments 首建全字段）与
  `DownloadManagerOptions.Store` 接线完成。写库时机为状态迁移 / 切分 /
  每 30s 同步 / 完成失败，**不在每秒进度里写**；退出时做最后一次同步。
  恢复规则：`Queued/Connecting/Downloading` → `Paused`，`Completed` 校验目标文件存在否则 `Failed`；
  段与进度从库回填，续传直接用回填的段（引擎不写也不读 `state/<id>.json`，见 ARCHITECTURE §7.2）。
  重名策略 `Rename/Overwrite/Fail` 在完成阶段生效，发布仍是同目录 `File.Move`（原子改名）。
  测试：Core 106 + Ui 51 = **157 全绿**，`dotnet build` 0 警告；应用二次启动（同一 `--db`）正常。
  未做：真实「100 任务并发 UI 不卡」压测（需批量建任务脚本）；任务分类/搜索/历史仍为空。

### M6 打磨（3–4 天）◐ 进行中

- ✅ 限速（令牌桶，全局限速跨所有任务）、代理、自定义头
- ✅ 错误分类与中文文案、失败重试退避
- ✅ 单实例 + 命令行传入 URL（2026-11-22）：命名 Mutex 决定主实例，
  命名管道把第二个实例的链接递给主实例（空载荷=只激活窗口）；`LaunchOptions` 负责从命令行捞 URL。
  键按库路径哈希、带 `Local\` 前缀，所以不同 `--db` 的实例可并存
- ✅ 删除 Avalonia 前端、MewUI 定为唯一前端（2026-11-22），产物统一 `xdm.exe`
- ☐ 托盘图标 + 完成通知（下载完气泡提示、双击打开、右键菜单）——M6 收尾
- ☐ `PartAllocation`（无稀疏文件系统预填充）——低优先级，仅稳定性需要时做

### V2 HF 模型下载（约 6–8 人日）

前提：M3/M5 完成（复用分段引擎、`PartFile`、限速、任务库）。要点：

- **两级任务**：一次 HF 下载 = 一个「模型任务」，内含 N 个「文件子任务」；UI 上是一条父行
  （总进度 = Σ子文件 size）可展开。首期的单文件任务就是"只有 1 个子任务"的特例。
- Hub 交互：`GET {endpoint}/api/models/{repo}`（或 `/tree/{rev}?recursive=true`）拿文件清单
  （path / size / lfs.sha256）；下载走 `{endpoint}/{repo}/resolve/{rev}/{path}`，**手动跟 302** 到 CDN，
  签名 URL 有时效 → 过期按"链接过期"分类（`SourceOutcome.Expired`）重新解析，不算失败。
- 认证：`Authorization: Bearer <token>`，来源：设置页 → 环境变量 `HF_TOKEN`；公开仓库可匿名。
  镜像：`HF_ENDPOINT`（如 `hf-mirror.com`），设置里可改。
- **两级并发**：文件级并发（默认 4）+ 文件内分段并发，总量由既有 `ConnectionSlots` 统一配额。
- **增量**：目标文件已存在且 size 一致（有 lfs 时再做 sha256）→ 跳过；记录 revision 支持"更新模型"。
- 校验：LFS 文件下载完校验 sha256（`Segment.Verifier` 的第一个真实使用场景）。
- UI：新建窗口增加「HuggingFace 仓库」页签（repo / revision / 通配符过滤 / 目标目录）。

### V3 BT 下载（约 15–20 人日，独立引擎）

- **不复用 `DownloadEngine`**：peer 调度与 HTTP 段调度是两套问题；共享的只是
  `Storage` / `Scheduling` / `Tasks` / `ProgressTracker` / UI。
- 协议面（自研成本极高，**优先引现成库**）：BEP 3 / 5(DHT) / 6(fast) / 9(metadata) / 10(ext) /
  11(PEX) / 15(UDP tracker) / 19(webseed)。候选 `MonoTorrent`（MIT，与本项目 GPL-3.0 兼容），
  最终选型见 `PROGRESS.md` 待决事项 #5。
- piece 与段的关系：piece 是**校验单位**，段是**下载单位**；`Segment.Verifier` 承载 piece hash，
  只有校验通过的字节才能计入 `Synced`（这直接复用首期的"Synced 才算进度"语义）。
- 元数据：`.torrent` 文件 + magnet（BEP 9 交换）；多文件 torrent = 天然的两级任务。
- 磁盘：先稀疏写 + 完成态校验，不做预分配；空间不足单独报错。
- 做种策略：V3 只做"下载完成后按配置最小做种或不做种"，上传调度、限速、私有 tracker 上报放 V4。
- webseed（BEP 19）走 HTTP Range → 可直接复用首期的 `SegmentRunner`。

### V4（远期，不在本期规划）

浏览器扩展 + Native Messaging、剪贴板监控、HLS/DASH、定时任务、BT 上传/做种与私有 tracker 完整支持。

---

## 四、验收标准（首期）

- [ ] 10GB 文件下载中断网 → 恢复后从断点续传，最终文件 SHA256 与服务器一致
- [ ] 服务器无视 Range（恒回 200）→ 自动退化为单流，完成后文件正确
- [ ] 服务器伪造 `Content-Length` → 拒绝写入并报错，不生成损坏文件
- [ ] 429/503 + `Retry-After` → 按指示等待重试，不消耗重试预算
- [ ] 下载中杀进程 → 重启后进度回退不超过 30 秒数据量
- [ ] 限速 1MB/s 时实测吞吐 ≤ 1.1MB/s，且各段均分
- [ ] 5000 条任务列表滚动无明显掉帧
- [ ] `dotnet test` 全绿，`dotnet build` 0 警告
- [ ] `dotnet publish -r win-x64` 产物可独立运行

---

## 五、风险登记

| 风险 | 影响 | 应对 |
|---|---|---|
| 大量进度条刷新导致掉帧（Direct2D，软件 WARP 回退时更明显） | 体验差 | 进度按 ≥1% 变化才更新；行内进度条用轻量自绘控件；用 1000 行假数据压测；任务量大时改**队列 + 200ms 批量 flush**（`ARCHITECTURE.md` §6.1：字典索引、每帧汇总只算一次、结构变化也合并） |
| exe 目录不可写导致任务库建不出来（装在 `Program Files`） | 启动即失败 | 回退 `%LOCALAPPDATA%\Xdm\xdm.db` 并提示，不弹窗阻断（`ARCHITECTURE.md` §7.1） |
| 用户只备份 `xdm.db`、漏掉 `.lock` / `.pidx` | 丢最新进度 | 文档写明"三件一起拷"（`ARCHITECTURE.md` §7.1） |
| Aprillz.MewUI 是 0.x，次版本可能破坏性变更 | 升级返工 | 版本锁死；升级当独立任务，跑全量测试 + 端到端截图对比（`ARCHITECTURE.md` §2.1） |
| 并发分段状态机移植出错（暂停/取消/竞速竞态） | 静默损坏文件 | 保留阻塞线程模型；按 `TESTING.md` 矩阵逐条对齐；段级断言区间不重叠 |
| 将来换 UI 框架时发现业务耦合视图 | 切换成本 | 守 `ARCHITECTURE.md` §1 的约束；`Xdm.Ui.Shared` 不引任何 UI 框架（这次换到 MewUI 时零改动） |
| 参考实现 GPL-3.0 | 许可传染 | 本项目已采用 GPL-3.0；文件头标注衍生来源 |
| 首期把任务模型写死成"单 URL 单文件"，V2/V3 才改 | 大范围返工（schema/UI/引擎签名） | 首期即落 `TaskKind` + `parent_id` + `files` 表 + 两级任务模型（`ARCHITECTURE.md` §12.1） |
| 引擎耦合 HTTP 状态码（429/403 直接进调度逻辑） | BT/镜像站无法复用 | `SourceOutcome` 语义化枚举，HTTP 语义只留在 `RangeHttpClient` 内部 |
| HF 签名 URL 过期被当成失败 | 大文件续传中断、重试耗尽 | 单独分类为 `Expired`，重新解析清单后继续，不消耗重试预算 |
| BT 协议面过大（DHT/PEX/UDP tracker） | 自研失控 | 优先引成熟库；自研只作为库不可用时的兜底，且先做"仅 tracker + 无 DHT"的最小可用版 |

---

## 六、工作量估算

| 阶段 | 人日 |
|---|---|
| M0 地基 | 1 |
| M1 主界面骨架 | 3 |
| M2 界面测试基建 | 1 |
| M3 引擎核心 | 7 |
| M4 接线 | 3 |
| M5 任务库与队列 | 3 |
| M6 打磨 | 4 |
| **首期合计** | **约 22 人日** |
| V2 HF 模型下载 | 6–8 |
| V3 BT 下载 | 15–20 |

首期里为 V2/V3 预留的部分（两级任务模型、`SourceOutcome`、schema 字段、UI 行模板的多文件聚合）
最多多花 1–2 人日，属于"先花小钱买返工保险"。

引擎与界面可由两人并行：M3 与 M1/M2 无依赖。
