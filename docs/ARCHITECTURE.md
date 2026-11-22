# 架构设计

命名空间根：`Xdm`。工程/程序集：`Xdm.Core`、`Xdm.Ui.Shared`、`Xdm.Ui.Mew`、`Xdm.App.Mew`、
`Xdm.Core.Tests`、`Xdm.Ui.Mew.Tests`。
目标框架：`net10.0`（不写 `-windows`）。

> 工程名里的 `.Mew` 指启动栈（MewUI），**产物**统一叫 `xdm.exe`（`AssemblyName` 与工程名解耦）。
> 2026-11-22 删除了原 Avalonia 前端（`Xdm.App` / `Xdm.Ui` / `Xdm.Ui.Tests`），
> 历史记录见 `PROGRESS.md`，迁移过程见 `PLAN-MEWUI.md`。

## 1. 分层与依赖

```
Xdm.App.Mew (WinExe)                    ← 壳：平台/后端注册、组合根、窗口生命周期、单实例
   ├── Xdm.Ui.Mew (classlib)            ← MewUI 视图（code-first）；不引平台与后端
   │      └── Xdm.Ui.Shared (classlib)  ← ViewModel / Service / 格式化；不引任何 UI 框架
   │              └── Xdm.Core
   └── Xdm.Core (classlib)              ← 引擎 / HTTP / 存储 / 任务库；不引任何 UI 框架
          └── (无)
```

单向依赖，禁止反向引用。判定标准：

- `Xdm.Core` 出现 `using Aprillz.MewUI;` → 错误
- `Xdm.Ui.Shared` 出现 `using Aprillz.MewUI;` → 错误（换 UI 框架时它必须零改动）
- `Xdm.Ui.Mew` 出现 `Win32Platform.Register` / `Direct2DBackend.Register` → 错误（平台注册只属于壳）
- `Xdm.App.Mew` 里出现业务代码（解析 HTTP、拼 SQL） → 错误
- 新增控件/对话框只能落在 `Xdm.Ui.Mew`，壳里不写视图代码

## 2. 工程与包引用

| 工程 | OutputType | TFM | 包引用 |
|---|---|---|---|
| `Xdm.Core` | Library | net10.0 | PureDb |
| `Xdm.Ui.Shared` | Library | net10.0 | →Core |
| `Xdm.Ui.Mew` | Library | net10.0 | Aprillz.MewUI.Core, →Ui.Shared, →Core |
| `Xdm.App.Mew` | WinExe（`xdm.exe`） | net10.0 | Aprillz.MewUI.Backend.Direct2D, Aprillz.MewUI.Platform.Win32, →Ui.Mew, →Ui.Shared, →Core |
| `Xdm.Core.Tests` | Exe（xunit v3） | net10.0 | xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, →Core |
| `Xdm.Ui.Mew.Tests` | Exe（xunit v3） | net10.0 | xunit.v3, Aprillz.MewUI.Platform.Win32, Aprillz.MewUI.Backend.Direct2D, →Ui.Mew |

`Directory.Packages.props` 统一版本；`Directory.Build.props` 统一
`net10.0` / `ImplicitUsings=enable` / `Nullable=enable` / `TreatWarningsAsErrors=true` / `Deterministic=true`。

`RuntimeIdentifier` 不写进 csproj（首期也不需要），只在 `dotnet publish -r win-x64` 时给。

### 2.1 渲染栈：MewUI（Win32 + Direct2D）

视图层用 `Aprillz.MewUI` 0.22（纯托管、code-first，没有 XAML 与资源字典）。三个包各司其职：

| 包 | 归属工程 | 作用 |
|---|---|---|
| `Aprillz.MewUI.Core` | `Xdm.Ui.Mew` | 控件 / 布局 / 绘制 / 输入；不含平台与后端 |
| `Aprillz.MewUI.Platform.Win32` | `Xdm.App.Mew` | 窗口、消息循环、DPI、剪贴板 |
| `Aprillz.MewUI.Backend.Direct2D` | `Xdm.App.Mew` | 渲染后端（Direct2D → WARP 软件回退） |

启动链（`src/Xdm.App.Mew/Program.cs`，唯一启动点）：

```csharp
Win32Platform.Register();
Direct2DBackend.Register();
// …构造 MainWindow…
Application.Run(window, onStartup);   // onStartup 里才接引擎桥（DispatcherTimer 需要 UI 线程）
```

**红线**：

| 禁止 | 原因 |
|---|---|
| 引 `Aprillz.MewUI.Windows` meta 包 | 会把 Gdi / MewVG 后端一起拖进产物（+~550 KB） |
| 在 `Xdm.Ui.Mew` 里调 `Win32Platform.Register()` / `Direct2DBackend.Register()` | 平台注册只属于壳；测试工程自己按需注册并允许 `Assert.Skip` |
| 视图层依赖 `Aprillz.MewUI.Platform.Win32` | 视图必须能在无后端的环境里构造（测试只用结构断言） |
| 依赖 0.x 的隐式默认值（主题回退、默认样式） | 次版本可能改动；升级当独立任务，跑全量测试 + 截图对比 |

输出目录自检（`build.ps1` 后肉眼核对）：有 `Aprillz.MewUI.dll`、`Aprillz.MewUI.Backend.Direct2D.dll`、
`Aprillz.MewUI.Platform.Win32.dll`；**没有** `Aprillz.MewUI.Backend.Gdi.dll` / `Aprillz.MewUI.Backend.MewVG.dll`。

### 2.2 为什么删掉 Avalonia（决策记录）

2026-11-22 删除 Avalonia 前端（`Xdm.App` / `Xdm.Ui` / `Xdm.Ui.Tests`），保留 MewUI 单前端。依据是 S5
的四档实测（3 次取中位，见 `PROGRESS.md`）：

| 档 | 产物 | 首帧 | 空闲内存 |
|---|---|---|---|
| MewUI AOT | **15.4 MB** 单 exe | 304 ms | **91 MB** |
| Avalonia AOT | 30.0 MB 单 exe | 123 ms | 111 MB |
| MewUI 托管发布 | **4.0 MB** | **939 ms** | **123 MB** |
| Avalonia 托管发布 | 11.1 MB | 1156 ms | 163 MB |

取舍说明（诚实版）：

- **赢**：产物减半（AOT 30→15.4，托管 11.1→4.0）、内存更低、依赖全托管无 `runtimes/**/native`；
  code-first 没有 XAML/后端切换的隐式约定，`Xdm.Ui.Mew` 的可测面更大（42 条结构/命令/对话框测试）。
- **输**：AOT 档首帧更慢（123→304 ms）——MewUI 的 Direct2D 初始化 + 冷路径 JIT 都在首帧前完成；
  托管发布档反而更快（1156→939 ms）。
- **维护**：只剩一套视图代码，不再有「同一个界面写两遍」的长期成本；`Xdm.Ui.Shared` 未改动一行，
  证明分层是有效的。

若将来需要换 UI 框架，切换面依旧只有「视图工程 + 壳工程」，`Xdm.Core` / `Xdm.Ui.Shared` 不动。

### 2.3 启动与常驻开销基线（对照参考实现）

已安装的参考程序：`D:\network\XDM\xdm-app.exe`（jpackage 产物，版本 `9.0.11`，JDK `25.0.4.1`）。
看它的启动参数（`D:\network\XDM\app\xdm-app.cfg`）能读出设计意图 —— 下载器是**常驻后台**的，
冷启动与内存都被刻意压过：

| 参考实现的做法 | 意图 | 我们的对应手段 |
|---|---|---|
| `-XX:+UseZGC` + 极小堆（`-Xms6m`、`-XX:-AlwaysPreTouch`） | 低延迟 GC、常驻内存小 | .NET 默认 Server/Workstation GC 即可；不显式调 `GCSettings` |
| `-XX:SharedArchiveFile=xdm-zgc.jsa`（AppCDS） | 跳过类加载，冷启动快 | `PublishReadyToRun=true`（发布档）；另提供 NativeAOT 发布档（`build.ps1 -Aot`） |
| `-XX:TieredStopAtLevel=1`、`CICompilerCount=1`、`ConcGCThreads=1` | 启动期不浪费 CPU 在编译/并行 GC 上 | 保持默认分层编译；启动路径别做重活 |
| `-Djava.library.path=$APPDIR` 预载原生库 | 避免首次调用时加载抖动 | 首帧前不加载大资源；图标走嵌入资源 |

**实测基线**（2026-11-22 S5 实测，3 次取中位，无任务、空窗口）：

| 档 | 产物 | 首帧 | 空闲私有内存 | 目标（M6 收尾） |
|---|---|---|---|---|
| AOT（`-Aot`） | 15.4 MB 单 exe | 304 ms | 91 MB | 首帧 ≤ 400 ms、内存 ≤ 120 MB |
| 托管发布（`-Publish`） | 4.0 MB 目录 | 939 ms | 123 MB | — |

- 启动期**不同步**做网络或磁盘重活：任务库加载、更新检查、日志轮转放到首帧之后
  （`Application.Run` 的 startup 回调里投递，不阻塞第一帧；单实例转发的链接也等首帧，见 §7.3）。
- 发布形态：`dotnet publish -c Release -r win-x64` 叠加 `PublishReadyToRun=true`；
  托管发布是**目录形态**（`xdm.exe` + 依赖 dll），必须现场验证「没有 `runtimes/**/native/*.dll`」。
- NativeAOT 档：`build.ps1 -Configuration Release -Aot`（= `dotnet publish -p:PublishAot=true`）。
  产物是**单个原生 exe（~15 MB），目标机不需要 .NET 运行时**，发布目录里不留 `.pdb`
  （ILC 原生符号 ~118 MB，`-KeepAotSymbols` 可保留）。
  约束：全链路不得用反射序列化/动态代码——JSON 一律走 `XdmJsonContext`（source-generated）；
  MewUI 视图是 code-first，没有 XAML 编译绑定要担心，但**不要用 `Type.GetType(string)` 之类按名字找控件**。
  新增落盘类型时要在 `XdmJsonContext` 补 `[JsonSerializable]`，否则 AOT 包运行时会抛
  "Metadata for type ... was not provided"。代价是发布慢（几分钟）且**必须装 MSVC 链接器 + Windows SDK**
  （VS「使用 C++ 的桌面开发」）。
- 内存结论：MewUI 空壳 AOT 档 91 MB 私有，上限取 120 MB 作为回归基线。

### 2.4 视图层约定（MewUI）

`Xdm.Ui.Mew` 是唯一视图层，约定集中在这里，细节见 `UI-DESIGN.md §12`：

| 维度 | 约定 |
|---|---|
| 视图表达 | 纯 C# 构建（无 XAML、无资源字典） |
| 渲染 | Aprillz.MewUI Direct2D 后端（唯一后端；WARP 软件回退由后端内部处理） |
| 主题 | `MewTheme` 静态类（色值/尺寸唯一来源，有 token 测试兜底） |
| 命令 | `Command` + `Window.Commands` 作用域（`MewCommandBridge` 适配 `ICommand`） |
| 打包 | `publish/win-x64`（4.0 MB 目录）、`win-x64-aot`（15.4 MB 单 exe），产物同为 `xdm.exe` |
| 测试 | `Xdm.Ui.Mew.Tests`（42 条：结构/命令桥/对话框/主题/布局） |

运行：

```powershell
./build.ps1 -Aot                                  # publish/win-x64-aot/xdm.exe
XDM_DEMO=3 publish/win-x64-aot/xdm.exe            # 演示模式，不建库
```

**MewUI 侧的三条硬约束**（都踩过，见 `PLAN-MEWUI.md`）：

1. **控件不是线程安全的**：父子关系与继承属性刷新都假定单线程。UI 线程之外只允许改 ViewModel，
   引擎事件必须经 `MewDownloadBridge` 切回 UI 线程；测试工程因此关掉 xunit 并行。
2. **没有 Headless 宿主**：`Measure/Arrange` 仍需要一个渲染后端（测试里按需注册 Win32 + Direct2D，
   失败就跳过布局类用例）；不 `Show()` 的控件树可以直接构造与断言。
3. **`ListBox` 不为每行建元素**：行是绘制出来的（所以才有 `TryGetItemBounds`），
   这既是它的虚拟化方式，也意味着行内刷新只能靠端到端截图验收，不能靠可视化树断言。

## 3. Core 结构

```
src/Xdm.Core/                       命名空间根 Xdm.Core
  Configuration/
    AppSettings.cs                  用户设置（限速/代理表/段数/目录/重名策略）
    DbSettingsStore.cs              设置读写（存任务库 settings 表，含旧 settings.json 迁移）
    ProxyResolver.cs                任务级代理 id → ProxyRoute（建任务时解析并固化）
    ISettings.cs
  Http/
    IRangeHttpClient.cs             抽象，便于测试注入
    RangeHttpClient.cs              SocketsHttpHandler + HttpClient 实现
    IRangeHttpClientProvider.cs     按 ProxyRoute 缓存客户端（一条代理一份连接池，任务间共享）
    HttpRangeRequest.cs             请求参数（url/offset/length/headers/cookie/ifRange）
    HttpResponseInfo.cs             状态码/ContentLength/ContentRange/LastModified/FinalUrl/Disposition
    ConnectOutcome.cs               **协议无关**语义：Ok / Retry / Busy / Unsupported / Expired / Rejected（见 §12.1）
  Storage/
    PartFile.cs                     单句柄 + RandomAccess 偏移写、FlushToDisk、SetLength
    AtomicJson.cs                   File.Move(overwrite) 原子写 JSON
    FileNaming.cs                   Content-Disposition/URL 推导、非法字符清洗、唯一名
    FolderPublisher.cs              .xdm-part → 最终名的原子发布（三种重名策略）
  Downloads/
    Segment.cs                      段模型
    DownloadTask.cs                 任务模型 + 状态机 + 段表（锁保护）；含 TaskKind / ParentId / Files
    SegmentVerifier.cs              段完成校验（V2 LFS sha256 / V3 piece hash）；首期为 null 不做校验
    SegmentRunner.cs                单段下载循环（← HttpChunkRetriever）
    DownloadEngine.cs               调度器（← HttpDownloader）
    TailRacer.cs                    尾段竞速（M6）
    ProgressTracker.cs              速率/ETA 滑动窗口
  Scheduling/
    DownloadQueue.cs                队列与全局并发闸门
    ConnectionSlots.cs              全局连接数配额
    SpeedLimiter.cs                 令牌桶限速（全局）
    RetryPolicy.cs                  指数退避 + 重试预算
  Tasks/
    TaskStore.cs                    PureDb 持久化
    DownloadRecord.cs               列表行数据（供 Ui 只读消费）
  Errors/
    DownloadError.cs                错误枚举 + 中文文案
```

## 4. 关键类型（签名级）

```csharp
namespace Xdm.Core.Downloads;

public enum SegmentStatus { Ready, Downloading, Finished, Failed, Cancelled }
public enum DownloadStatus { Ready, Downloading, Paused, Assembling, Finished, Failed }

public sealed class Segment
{
    public long Id { get; init; }
    public long Offset { get; init; }
    public long Length { get; set; }            // 可被切分放大
    public long Downloaded;                      // 原始字节计数，Interlocked
    /// <summary>已确认落盘（fsync）的字节数。进度与状态文件只认它，对应 Kotlin 的 Chunk.synced。</summary>
    public long Synced;
    public SegmentStatus Status;
    public long LastTakeOverTicks;
}

public sealed class DownloadTask
{
    public long Id { get; init; }
    /// <summary>协议种类。首期只实现 Http；HuggingFace / Torrent 为 V2 / V3，见 §12。</summary>
    public TaskKind Kind { get; init; }
    /// <summary>父任务 id（HF 模型包、BT 多文件 torrent）。首期恒为 0（自己就是顶层）。</summary>
    public long ParentId { get; init; }
    public string Url { get; set; }
    public string? FinalUrl { get; set; }        // 本次会话记录的重定向目标，不持久化
    public string FileName { get; set; }
    public string TargetFolder { get; set; }
    public string TempFolder { get; set; }
    public long? TotalSize { get; set; }
    public long Downloaded;                      // 只累加 Synced
    public DownloadStatus Status;
    public string? Validator { get; set; }       // If-Range 用 Last-Modified，不用 ETag
    public bool NoRangeSupport;
    public bool FillsGaps;                       // FAT32/exFAT：无稀疏文件
    public IReadOnlyDictionary<long, Segment> Segments;
    /// <summary>段完成校验（V2 LFS sha256 / V3 piece hash）。首期恒为 null。</summary>
    public SegmentVerifier? Verifier { get; set; }
    public PartFile? PartFile;
    public TaskCompletionSource<CommitResult> Completion;
}

/// <summary>协议无关的连接结果。HTTP 状态码只允许出现在 RangeHttpClient 内部。</summary>
public enum SourceOutcome
{
    Ok,
    Retry,          // 瞬时错误（超时/5xx/连接重置），消耗重试预算
    Busy,           // 对端限流（HTTP 429/503），按 RetryAfter 等待，不消耗预算
    Unsupported,    // 不支持区间（HTTP 恒回 200 / BT 无可用 peer）
    Expired,        // 链接失效（HTTP 403/410、HF 签名 URL 过期）→ 重新解析来源
    Rejected,       // 确定性失败（404/认证失败/校验不符）
}

public interface IRangeHttpClient
{
    Task<SourceOutcome> ConnectAsync(HttpRangeRequest request, CancellationToken ct);
    // 响应体以流返回，由 SegmentRunner 阻塞读取（不在此层做缓冲）
    // 约定：本接口只表达"按区间取字节"，不得让调用方判断 HTTP 状态码。
}
```

`TaskKind`：`Http`（首期）、`HuggingFace`（V2）、`Torrent`（V3）。
新增种类只允许"新增枚举值 + 新增引擎实现"，不允许改 `DownloadEngine` 的段调度逻辑。

`DownloadEngine` 照搬 `HttpDownloader.kt` 的调度骨架，方法一一对应：

| C# | Kotlin（`downloaders/web/http/`） | 说明 |
|---|---|---|
| `Start()` | `HttpDownloader.start()`:216 | 建段、开写句柄、起线程 |
| `Resume()` | `resume()`:248 | 读状态、归一化段、必要时整任务重来 |
| `OnSegmentFinished(id)` | `onChunkFinished()`:322 | **切分段 / 接管 / 起竞速** 的主决策点 |
| `OnSegmentFailed(id, err)` | `onChunkFailed()`:392 | 重试预算与整体失败 |
| `OnLinkRefused(id)` | `onLinkRefused()`:411 | 限流等 vs 链接过期 |
| `OnSegmentConnected(id, info)` | `onChunkConnected()`:452 | 首次响应确认大小/Range 能力 |
| `FindMaxSegment()` | `findMaxChunk()`:529 | 找可切分的最长段 |
| `SplitSegments()` | `splitChuck()`:553 | 二分 + 立即落状态 |
| `TryTakeOver(id, max)` | `takeOverChunk()`:608 | 并入相邻未开始段，复用连接 |
| `StartTailRacers()` | `startTailRacers()`:660 | 尾部竞速；**限速开启时直接返回** |
| `NeedsAllocation/Locate()` | `needsAllocation():745` / `allocate():764` | 无稀疏文件预填充（M6） |
| `FlushBacklog/SyncData` | `updateBytesDownloaded():829` / `syncData():978` | 30s 强制刷盘 + 落状态 |
| `FinishDownload()` | `finishDownload():928` | 关闭句柄 → 发布 → 回调 |
| `SegmentRunner.Run()` | `HttpChunkRetriever.retrieveChunk():39` | 单段循环 |
| `SegmentRunner.Connect()` | `connect():198` | Range 能力判定 |
| `SegmentRunner.CopyWithLength()` | `copyDataWithLength():320` | 带长度拷贝 + 校验 |
| `SegmentRunner.CopyUntilEof()` | `copyDataWithoutLength():436` | 无长度模式 |

## 5. 必须保持一致的语义（复刻时最易错）

1. **`Synced` 才是进度**。`Downloaded` 是内存计数，`Synced` 是 fsync 后的值；状态文件与
   UI 进度都用 `Synced` 之和。恢复时 `Synced := 读到的值`。
2. **`If-Range` 用 `Last-Modified`**（首次响应的值），不用 `ETag`——同文件不同镜像 ETag 不同，
   用 ETag 会导致换镜像即无法续传。
3. **Range 能力判定要严格**：
   - 请求 `bytes=a-b` 返回 `200` → 不支持 Range，整任务转单流 → `NoRangeSupport = true`
   - `Content-Length != b-a+1` → 拒写该段（不写坏数据）
   - 无 `Content-Length` → 只能单流读到 EOF
4. **拒绝分类**：403/410 且"还有别的段在收数据"或 15s 内有字节流入 → 判定服务器连接数限制，
   等待重试且不消耗重试预算；否则判定链接过期 → 失败。
5. **切分条件**：剩余可切字节 ≥ 256KB、目标段 5 秒内未被接管、活动段数 < `MaxSegments`。
   切分后**立即**落盘状态，避免切完崩溃丢段。
6. **竞速与限速互斥**：限速启用时不启动尾段竞速。
7. **发布是原子改名**：全程写 `.<id>.xdm-part`（放目标目录，点前缀 + `.xdm-part` 后缀，与参考实现
   `.<hexid>.xdm-part` 对齐，避免与用户文件撞名、也避免被误认为完整文件），完成后同盘 `File.Move` 到最终名；
   跨盘则复制 + 删除。重名按配置：覆盖 > 自动改名 > 失败。
8. **父任务进度 = Σ 子任务 `Synced`**，父自身不计字节；父状态机只在"全部子完成 / 任一子失败且不可重试"
   时迁移。首期子项恒为 1，语义照此实现（V2/V3 直接复用）。

## 6. 线程模型

```
UI 线程 (Dispatcher)              引擎
    │  队列 + 定时 flush(250ms)     │
    │◄──────── 进度事件 ────────────┤
    │                               ├─ 调度线程（1 个/任务）：切分、接管决策
    │                               ├─ 段线程（N 个，N ≤ MaxSegments）：阻塞读 + 写
    │                               └─ 同步线程：每 30s fsync 全部句柄 + 落状态
    └── 用户操作（暂停/删除） ──► 置位 CancellationToken + 通知调度线程
```

- 段线程用 `new Thread(IsBackground=true)`（长驻、阻塞 IO，占线程池会饿死其它任务）。
- 段表：`DownloadTask` 内部 `lock` 保护 `Dictionary<long, Segment>`；锁内只做状态迁移，
  IO 一律在锁外。
- 单写句柄：`PartFile` 全局一个 `SafeFileHandle`，各段通过
  `RandomAccess.Write(handle, span, offset)` 并发写；暂停/完成/失败时统一关闭。
- UI 永不读引擎可变字段：通过 `IDownloadObserver` 拿到不可变快照（`SegmentSnapshot` record）。

### 6.1 批量刷新（任务量大时启用，二期优化）

现状（`DownloadUiBridge`）：进度事件进 `ConcurrentDictionary<long, DownloadRecord>` 按 id 去重，
`DispatcherTimer` **250ms** 在 UI 线程 `Drain()` 逐条 `UpdateRow`；结构变化（新增/删除）直接
`Dispatcher.UIThread.Post` 立即投递。任务数在百级以内够用。

**触发条件**：并发任务数上百、或一次恢复上千条任务时，`Drain()` 的单帧成本按 O(n²) 增长
（`UpdateRow` 里 `List.Find` + `StatusSummary` 全表 Count/Sum 都是 O(n)，每行各算一次）。
届时按下述方式改成批量刷新，接口不动、只改 `DownloadUiBridge` + `MainWindowViewModel` 内部：

| 项 | 做法 |
|---|---|
| 队列 | 沿用按 id 去重的字典；结构变化（Add/Remove）也进同一队列，不再逐条 `Post` |
| 节拍 | `DispatcherTimer` **200ms**，`DispatcherPriority.Background`，一帧只 flush 一次 |
| 行索引 | ViewModel 内 `Dictionary<long, DownloadRowViewModel>` 取代 `List.Find`（O(1) 定位） |
| 行更新 | 只对真正变化的字段 `RaisePropertyChanged`（`SetProperty` 已做值比较）；`Sync` 状态未变就跳过 |
| 汇总 | `RefreshSummaries()` / 分类计数每帧**结尾调一次**；`StatusSummary`、`ListSummary` 缓存成字段，脏了才重算 |
| 结构批量 | tick 内一次性 `AddRange` / 批量移除，`ApplyFilter` 每帧最多一次 |
| 小规模豁免 | 队列长度 < 阈值（如 32）时直接 `Post` 单条刷新，保低延迟 |
| 度量 | tick 内 `Stopwatch` 计时，单帧 > 8ms 记日志并自适应降频（200 → 400ms） |

单帧成本目标：n=1000 全在下载时，一帧 (200ms) flush 的 UI 线程耗时应 < 8ms，
使列表滚动不掉帧（验收项见 `PLAN.md` 五、风险登记）。

### 6.2 全局连接配额

任务级闸门（`DownloadManager` 的 `MaxConcurrentTasks`）只限制「几个任务同时在跑」，
挡不住「4 个任务 × 8 段 = 32 条连接」；HF 那种一个任务几百个文件的场景更需要一条硬上限。
因此再加一层 `Scheduling/ConnectionSlots`（信号量式，可运行中调容量）：

| 项 | 约定 |
|---|---|
| 作用域 | 整个进程一个实例，由 `MewRuntime` 建好塞进 `DownloadEngineOptions.ConnectionSlots`，所有任务、所有段共用 |
| 覆盖面 | 段线程的「发请求 → 读完响应体 → 关连接」整段（`SegmentRunner.Run` 的 `using var lease`），**以及探测请求**（`DownloadEngine.Probe`）：探测也占一条真实连接 |
| 释放 | `SlotLease` 在 `Dispose` 里归还；重试、取消、异常都靠 `using` 兜住，不会漏名额 |
| 调容量 | `SetCapacity` 调大立即 `PulseAll` 唤醒等待者；调小不打断在途连接，只让后续 `Acquire` 排队 |
| 设置在 | `AppSettings.MaxConnections`（默认 32，夹紧 1–256，schema v4）；设置页「连接」页改完即时 `SetCapacity`，无需重启 |
| 可观测 | `InFlight` / `Waiting` / `IsSaturated` 供测试与将来的状态栏使用 |

**同一包内的文件闸门**：`MaxConcurrentTasks` 管的是「几个任务」，`MaxConnections` 管的是「几条连接」，
中间还缺一层「一个多文件任务里几个文件同时在跑」——HF 一个模型动辄几百个文件，若全部进队列，
它们会长期占满任务槽位，把用户手动加的小任务饿在后面。因此 `DownloadManager` 再加一道
兄弟闸门（`AppSettings.MaxFilesPerTask`，默认 3，夹紧 1–16）：

| 项 | 约定 |
|---|---|
| 作用域 | 只作用于 `ParentId > 0` 的子任务；独立任务照旧只受 `MaxConcurrent` 管 |
| 计数 | `_runningChildren`（父行 id → 在跑子任务数），进出都在 `_gate` 内，随 `SlotHeld` 一起加减 |
| 挡住时 | `TryTakeNext` 把候选**放回队尾**并继续扫下一个，单次只扫一遍当前队列长度（`budget`），不会空转 |
| 放行 | 子任务结束/暂停/删除 → `ReleaseSlot` 还名额 → `_wake.Set()` 唤醒调度线程重扫 |
| 效果 | 包内文件按队列顺序「跑满 N 个再补 N 个」，父行进度仍然单调推进，小任务也不会被大包长期挤占 |

**暂停与装配的互斥**：`Start`/`Resume` 的装配阶段（探测 → 开句柄 → 起线程）会往 `Runtime.Threads`
追加线程，而调度器线程上的 `Resume` 与被调度前的 `Pause` 可能撞在一起。`Runtime.SetupDone`
（`ManualResetEventSlim`）在装配结束时置位，`Pause` 先 `Cancel` 再等它（上限 5s），之后才枚举线程表、
Join、释放 CTS；线程表本身的读写都持 `Runtime.Gate`。另外后台线程一律用构造时抓下的
`Runtime.Token` 而非 `Cancellation.Token` 属性（CTS 释放后取 `Token` 会抛 `ObjectDisposedException`），
等待可取消延时时对 `WaitHandle` 的 `ObjectDisposedException` 按「已收尾」处理，避免段线程/进度线程
抛未捕获异常。

## 7. 持久化

### 7.1 PureDb（任务库）

**位置：`AppContext.BaseDirectory\xdm.db`** —— 与 exe 同目录（绿色版语义：目录即数据，拷走就能走）。

```csharp
// Xdm.Core/Tasks/TaskStore.cs
// 全局只保一条连接（构造时打开、Dispose 时关闭），所有公开方法在 _gate 锁内串行使用：
//   Data Source=<db 绝对路径>;Access=Shared
```

**不做多连接**：PureDb 没有连接池，多条连接各自缓存页，容易读到未合并的旧数据；因此
`TaskStore` 自己持有唯一 `PureDbConnection`，读写全部走 `lock (_gate)`，方法之间自然排队。
外部进程仍可用 `Access=Shared` 打开同一库，跨进程争用由乐观事务 + `Run` 重试兜底。

PureDb 是纯托管嵌入式库（`net10.0`，无原生依赖），SQL 方言是 SQLite 的子集，因此有几条硬约束：

- 表必须有且只有一个 `INTEGER PRIMARY KEY`；不支持复合主键 → 段表多一个自增 `id` 列，
  `(task_id, seg_id)` 的唯一性由 `CREATE UNIQUE INDEX` 保证。
- 没有 `PRAGMA`，也没有 `last_insert_rowid()`：`Insert` 在事务内取 `MAX(id)+1` 显式写入 id。
- 事务是乐观 Serializable，冲突抛 `DBConcurrencyException`；写路径统一走
  `Run(...)`（最多 6 次退避重试），唯一索引冲突（`SqlException` 含 `UNIQUE`）同样重试。
- 事务内的每条命令必须显式设置 `DbCommand.Transaction`，否则直接抛 `InvalidOperationException`。
- 外键与 `ON DELETE CASCADE` 由引擎执行（测试已覆盖）；删除任务仍显式删段，不依赖级联开关。

约束：

- `xdm.db` 旁边有 `xdm.db.lock`（跨进程访问锁），写入量上去后还会生成 `xdm.db.pidx`（页索引），
  **备份/迁移要一起拷**，只拷 `.db` 会丢数据。
- 目录**不可写**时（装在 `Program Files`、或 exe 在只读介质）→ 回退
  `%LOCALAPPDATA%\Xdm\xdm.db`，并在状态栏/日志提示"任务库已回退到用户目录"。
  判定方式：启动时试着建库，捕获 `SqlException`/`UnauthorizedAccessException` 后降级，不弹窗阻断。
- 旧版是 SQLite 库（文件头 `SQLite format 3\0`）。`TaskStore` 打开前会检测并抛出
  `InvalidOperationException`，提示备份后删除/改名，不尝试就地迁移。
- 库文件路径解析（`AppPaths.DbPath`，优先级从高到低）：

  | # | 来源 | 结果 |
  |---|---|---|
  | 1 | 命令行 `--db <path>` / `--db=<path>` | 该路径 |
  | 2 | 环境变量 `XDM_DB` | 该路径 |
  | 3 | 环境变量 `XDM_DATA_DIR`（目录语义） | `<dir>\xdm.db` |
  | 4 | `AppContext.BaseDirectory` | `<exe 目录>\xdm.db` |
  | 5 | 第 4 步目录不可写 | `%LOCALAPPDATA%\Xdm\xdm.db` |

- **`state/` 恒为"库文件所在目录"下的 `state\`**（`AppPaths.StateDir`），两者永不拆开：
  `--db D:\xdm\my.db` → 库 `D:\xdm\my.db` + 快照目录 `D:\xdm\state\`（库模式不再往里写文件，见 §7.2）。
- 参数边界：相对路径按**当前工作目录**转绝对路径（`dotnet run -- --db .\dev.db` 落在仓库根）；
  父目录不存在则创建；`--db` 指向已存在的**目录**或缺少取值 → 报错并以返回码 `2` 退出
  （不猜、不静默回退到默认路径，否则用户会对着错误的库排查）。
- 解析位置：**`Xdm.App.Mew` 负责读命令行**（`LaunchOptions.Parse`），解析出 `AppPaths` 后注入
  `TaskStore` / 引擎；`Xdm.Core` 不得调用 `Environment.GetCommandLineArgs()`
  （测试要能直接构造 `new AppPaths(dbPath)`）。
- 单实例：不同 `--db` 视为不同实例，允许并存；同一库由命名 Mutex 挡住第二个进程（见 §7.3）。
- `Xdm.Ui.Mew.Tests` / `Xdm.Core.Tests` 一律用 `:memory:` 或临时目录下的随机文件名，
  **禁止**写到 exe 目录（否则并发测试互相踩库，且污染构建产物）。
  `:memory:` 由 `TaskStore` 映射成 `%TEMP%\xdm-memory\` 下的临时文件，`Dispose` 时删除。
- 发布时不要打包任何预置 `xdm.db`；首次运行自动建表（`CREATE TABLE IF NOT EXISTS`）。

```sql
-- 段表必须有 INTEGER PRIMARY KEY，复合唯一性用索引表达
CREATE TABLE IF NOT EXISTS tasks (
  id               INTEGER PRIMARY KEY,
  kind             INTEGER NOT NULL DEFAULT 0,   -- 0 Http 1 HuggingFace 2 Torrent（V2/V3，首期为 0）
  parent_id        INTEGER,                      -- 两级任务：模型包 → 文件（首期 NULL）
  url              TEXT    NOT NULL,
  file_name        TEXT    NOT NULL,
  target_folder    TEXT    NOT NULL,
  temp_folder      TEXT    NOT NULL,
  total_size       INTEGER,
  downloaded       INTEGER NOT NULL DEFAULT 0,   -- 只记 Synced 之和
  status           INTEGER NOT NULL,             -- 0 Queued 1 Active 2 Paused 4 Finished 5 Failed
  max_segments     INTEGER NOT NULL,
  headers_json     TEXT,
  cookie           TEXT,
  validator        TEXT,                         -- If-Range: Last-Modified 原样串
  no_range_support INTEGER NOT NULL DEFAULT 0,
  content_sha256   TEXT,                         -- V2：LFS sha256 / V3：整体校验（可空）
  source_meta_json TEXT,                         -- V2：repo/revision/path；V3：infohash。首期为 NULL
  error_code       INTEGER,                      -- DownloadError；5 且为 Disk 时恢复成 DiskError
  name_policy      INTEGER NOT NULL DEFAULT 0,   -- 0 Rename（默认）1 Overwrite 2 Fail
  created_at       INTEGER NOT NULL,
  finished_at      INTEGER
);

CREATE TABLE IF NOT EXISTS segments (
  id      INTEGER PRIMARY KEY,
  task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  seg_id  INTEGER NOT NULL,
  offset  INTEGER NOT NULL,
  length  INTEGER NOT NULL,
  synced  INTEGER NOT NULL,
  status  INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_segments_task_seg ON segments(task_id, seg_id);

-- 配置表：整份 AppSettings 一行（id 恒为 1）；PureDb 不支持 text 主键，所以用固定 id
CREATE TABLE IF NOT EXISTS settings (
  id    INTEGER PRIMARY KEY,
  value TEXT NOT NULL
);

-- 任务级附加属性（键值对）。PureDb 不支持 ALTER TABLE，后续新增的任务字段走这里，
-- 不给 tasks 加列；旧库由 CREATE TABLE IF NOT EXISTS 自然补齐。
-- 当前键：proxy_id（任务创建时选定的代理，空 = 用默认代理）
CREATE TABLE IF NOT EXISTS task_extras (
  id      INTEGER PRIMARY KEY,
  task_id INTEGER NOT NULL,
  name    TEXT NOT NULL,
  value   TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_task_extras ON task_extras(task_id, name);
```

- **配置也在这个库里**（`settings` 表，`id=1`，`value` 是整份 `AppSettings` 的 JSON）：
  与任务同库同备份，不再有 `settings.json`。写路径是「`UPDATE` 影响 0 行则 `INSERT`」，同一事务，
  断电不会只写一半；JSON 由 source-generated `XdmJsonContext` 序列化（NativeAOT 下无反射元数据）。
  旧版 `settings.json` 在**库里还没有配置**时于启动阶段导入一次并删除文件；文件损坏则原样保留、按默认值启动。

- 启动：`status=Queued/Active` 的记录一律改 `Paused`（进程被杀残留）；`Finished` 的校验目标文件，
  不存在则降级 `Failed`。
- 写库时机：状态迁移、切分、每 30s 同步、完成/失败。**不在每秒进度里写库**。
  完成路径**先落库再对外报 `Completed`**，避免"UI 已完成、库里还是下载中"的窗口被进程退出放大。
- 当前实现：`temp_folder` 与 `target_folder` 相同（`.xdm-part` 就在目标目录，发布是同目录 `File.Move`）；
  `name_policy` 是首版就带的列（重名策略），不靠 `ALTER` 迁移。
- 参考实现用自定义 tagged 二进制（`task-<id>.info`、`<id>.state`、`active/paused/finished-downloads.dat`），
  这里**不照搬**：带 SQL 的关系库更适合后续加分类/搜索/历史。

### 7.2 段表持久化：任务库优先，`state/<id>.json` 只兜底

**接了任务库（正常启动）**：段表只有一份，在 `xdm.db` 的 `segments` 表里。
`DownloadManager` 用 `writeStateFiles: false` 构造 `DownloadEngine`，暂停 / 失败 / 30s 同步
都只「刷盘 + 落库」，不再产生 `state/*.json`。重启时 `RestoreFromStore()` 把段读回
`DownloadTask`（`ReplaceSegments`），`Resume()` 先探测（`If-Range` + 总大小），
校验通过后直接拿任务自带的段续传——恢复路径不读任何文件。

**没有任务库时**（引擎单测、纯文件模式）：引擎自己写 `state/<id>.json`，
`Resume()` 发现任务没有段表才回退读它。格式与写入方式（tmp → `Flush(true)` → `Move`）不变：

```json
{
  "version": 1,
  "id": 1712,
  "totalSize": 104857600,
  "validator": "Tue, 03 Nov 2026 08:11:00 GMT",
  "noRangeSupport": false,
  "segments": [
    { "id": 0, "offset": 0,        "length": 26214400, "synced": 26214400, "status": "Finished" },
    { "id": 1, "offset": 26214400, "length": 26214400, "synced": 8221137,  "status": "Downloading" }
  ]
}
```

- 同一任务不会有两份权威数据：库模式不写 json；旧版本升级留下的残留 json 只在
  「任务没有段表」时被读一次，随后随暂停 / 完成 / 删除清掉。
- 两种来源共用同一套段表校验：偏移首尾相接、`Σ length == TotalSize`、
  `0 ≤ synced ≤ length`；不合法就整任务重来——宁可重下，也不拿错位的段拼出坏文件。
- `state/` 目录仍跟着 `<AppContext.BaseDirectory>`（与 `xdm.db` 同级，随库一起回退），
  只是库模式下它保持空。

### 7.3 单实例与命令行传参

下载器要能被浏览器「点一下链接就打开」，所以启动路径必须同时解决两件事：
**同一任务库只跑一个进程**、**第二个进程的链接要落到已运行的窗口里**。

```csharp
// Xdm.Core/Configuration/LaunchOptions.cs —— 命令行解析（库路径规则沿用 AppPaths）
var options = LaunchOptions.Parse(args);   // options.Paths, options.Url

// Xdm.Core/Shell/SingleInstanceHost.cs —— 命名 Mutex 定主从，命名管道送 URL
using var primary = SingleInstanceHost.TryAcquire(options.Paths.DbPath, OnSecondInstance);
if (primary is null)                       // 已有实例
    return SingleInstanceHost.TrySend(options.Paths.DbPath, options.Url) ? 0 : 4;
```

约定：

| 项 | 规则 |
|---|---|
| 键 | `SHA256(全路径小写)` 前 8 字节；`Local\xdm-sharp-<key>`（按登录会话隔离）+ 管道 `xdm-sharp-<key>` |
| 主从判定 | `new Mutex(false, name, out createdNew)`，`createdNew == false` 即已有实例（不取所有权，避开「Mutex 归创建线程所有」的坑） |
| 载荷 | 一行 UTF-8 文本：链接，或空串表示「只把窗口提到前面」 |
| 退出码 | `0` 已交接、`4` 交接失败（主实例卡死/权限不足时才出现） |
| 演示模式 | 不抢 Mutex（`XDM_DEMO` 下可同时开多个，避免验证脚本占用真实库的键） |
| 顺序 | **先抢单实例，再打开任务库**——第二个进程不该去碰主实例正占着的库 |

主窗口侧的处理时机很容易踩空：**必须等主窗口真的显示了再弹模态框**，
否则 `ShowDialog` 会把消息循环堵在启动回调里，主窗口永远等不到显示（只剩对话框——
MewUI 首帧前就是这么挂的）。实现用 `Window.FirstFrameRendered` + `Dispatcher.BeginInvoke`；
首帧之前到达的转发请求先排队（`_ready` 闸门）。

## 8. UI ↔ 引擎边界

```csharp
namespace Xdm.Core.Downloads;

public interface IDownloadObserver
{
    void OnTaskStatusChanged(long id, DownloadStatus status);
    void OnProgress(long id, long syncedTotal, double bytesPerSecond, TimeSpan? eta);
    void OnSegmentsChanged(long id, IReadOnlyList<SegmentSnapshot> segments); // 仅段表结构变化（切分/恢复/结束）
    void OnCompleted(long id, string finalPath);
    void OnFailed(long id, DownloadError error, string detail);
}

public readonly record struct SegmentSnapshot(long Id, long Offset, long Length, long Synced, SegmentStatus Status);
```

- 引擎回调发生在任意线程；`Xdm.App.Mew` 里的 `MewDownloadBridge` 负责缓存最新值 + 250ms 合并 +
  `Dispatcher.BeginInvoke` 切回 UI 线程（结构变化立即投递）。
- 任务列表行只订阅 `OnProgress` / `OnTaskStatusChanged`；`OnSegmentsChanged` 只在段表**结构**变化时发
  （建段 / 切分 / 恢复 / 结束），不跟进度节拍。
- 详情窗口不订阅事件，而是每 250ms 拉 `DownloadManager.Segments(id)` / `Children(id)`
  （段表从 `task.Snapshot()` 现取，不读 `entry.Segments` 那份只在建段/暂停时刷新的缓存）。
  窗口关掉即零开销，也不需要「只有窗口开着才有人听」的事件形态。
- ViewModel 不持有 `DownloadTask`，只持有 `DownloadRecord`（不可变视图数据）。

## 9. Ui 层结构

```
src/Xdm.Ui.Shared/                   命名空间根 Xdm.Ui（视图无关）
  ViewModels/
    ViewModelBase.cs                 INotifyPropertyChanged + SetProperty
    MainWindowViewModel.cs
    DownloadRowViewModel.cs
    NewDownloadViewModel.cs
    SettingsViewModel.cs
    CategoryViewModel.cs
    RelayCommand.cs                  10 行 ICommand 实现（换 UI 框架时不动）
  Services/
    IDownloadActions.cs              视图动作回调（新建/暂停/删除…）
  Formatting/
    TextFormat.cs                    字节数/速度/剩余时间 → 中文文案
  Samples/
    SampleDownloads.cs               XDM_DEMO 用的假数据

src/Xdm.Ui.Mew/                      命名空间根 Xdm.Ui.Mew（视图，code-first）
  MewTheme.cs                        色值/尺寸唯一来源（有 token 测试兜底）
  MainWindow.cs                      自绘标题栏 + 工具栏 + 分类 + 列表 + 状态栏
  MewCommandBridge.cs                ICommand → MewUI Command
  WindowDrag.cs                      标题栏拖动 / 双击最大化
  Controls/
    TitleBarChrome.cs                对话框标题栏（关闭按钮）
    SegmentBar.cs                    分段进度条（自绘）
    EditBox.cs                       带中文右键菜单的输入框
    RowHost.cs                       列表行的元素宿主
  Dialogs/
    NewDownloadDialog.cs / SettingsDialog.cs / AboutDialog.cs / ConfirmDialog.cs
  Assets/xdm-logo.png                标题栏 logo（嵌入资源）
```

`Xdm.App.Mew` 只保留：`Program.cs`（`Win32Platform.Register()` + `Direct2DBackend.Register()`
+ 单实例闸门 + `Application.Run`）、`MewRuntime.cs`（组合根：路径 → 任务库 → 设置 → 引擎 → ViewModel）、
`MewDownloadBridge.cs`（引擎事件切 UI 线程）、`MewDownloadActions.cs`（`IDownloadActions` → 引擎）、
`Shell.cs`（打开目录等系统调用）、`Assets/xdm.ico`（`ApplicationIcon`）。

窗口外观（系统 chrome 关闭、标题栏自绘、尺寸与图标规格）见 `UI-DESIGN.md` §11。

## 10. Kotlin → C# 文件对照

| 参考实现 | 本项目 | 处理方式 |
|---|---|---|
| `downloaders/web/http/HttpDownloader.kt` (1038) | `Xdm.Core/Downloads/DownloadEngine.cs` | 照搬结构，锁改 `lock`，删 HLS/DASH/BT 分支 |
| `downloaders/web/http/HttpChunkRetriever.kt` (637) | `Xdm.Core/Downloads/SegmentRunner.cs` | 照搬；OkHttp 换 `IRangeHttpClient` |
| `downloaders/web/http/Models.kt` (214) | `Xdm.Core/Downloads/Segment.cs` + `DownloadTask.cs` | `AtomicLong` → `Interlocked`/`Volatile` |
| `downloaders/web/http/TailRacer.kt` (134) | `Xdm.Core/Downloads/TailRacer.cs` | 照搬（M6） |
| `downloaders/web/http/PartAllocation.kt` (125) | `Xdm.Core/Storage/PartFile.cs` 局部 | 照搬（M6，低优先级） |
| `downloaders/web/StateSaver.kt` (390) | `Xdm.Core/Tasks/TaskStore.cs` + `Storage/AtomicJson.cs` | 二进制格式换 JSON，只保留 HTTP 部分 |
| `downloaders/web/ProgressTracker.kt` (289) | `Xdm.Core/Downloads/ProgressTracker.cs` | 照搬滑动窗口 |
| `downloaders/web/SpeedLimiter.kt` (82) | `Xdm.Core/Scheduling/SpeedLimiter.cs` | 照搬令牌桶；改全局跨任务 |
| `downloaders/web/ConnectionSlots.kt` (82) | `Xdm.Core/Scheduling/ConnectionSlots.cs` | 照搬 |
| `util/RetryPolicy.kt` (170) | `Xdm.Core/Scheduling/RetryPolicy.cs` | 照搬退避与预算 |
| `storage/Storage.kt` + `NioFileOps.kt` (455) | `Xdm.Core/Storage/PartFile.cs` | 用 `SafeFileHandle` + `RandomAccess` 重写，代码量减半 |
| `util/AtomicIO.kt` (132) | `Xdm.Core/Storage/AtomicJson.cs` | 用 `File.Move(overwrite)` |
| `network/http/impl/HttpClientImpl.kt` (376) | `Xdm.Core/Http/RangeHttpClient.cs` | `SocketsHttpHandler` 重写：手动跟重定向（记 FinalUrl）、`MaxConnectionsPerServer`、逐次读超时 |
| `downloaders/TaskInfoDB.kt` | `Xdm.Core/Tasks/TaskStore.cs` | 改 PureDb |
| `xdm-manager/DownloadManager.kt` (2300) | `Xdm.Core/Scheduling/DownloadManager.cs` + `DownloadQueue.cs` | 只取 HTTP 相关：队列、并发、发布、事件 |
| `xdm-app/ui/**` (68 文件) | `Xdm.Ui.Mew/**` | 重新设计，见 `UI-DESIGN.md` |

## 11. HTTP 客户端要点

```csharp
new SocketsHttpHandler {
    MaxConnectionsPerServer = settings.MaxSegments,      // 与段数上限一致
    AllowAutoRedirect = false,                           // 自己跟，才能记 FinalUrl 与 Last-Modified
    AutomaticDecompression = DecompressionMethods.None,   // 下载文件不要透明解压
    ConnectTimeout = TimeSpan.FromSeconds(15),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    Proxy = proxyFor(route),                             // 由任务的 ProxyRoute 决定，见下
}
```

- **代理按路线分客户端**：`ProxyHttpClientProvider.For(route)` 懒建并缓存，一条路线一份连接池
  （系统代理 / 直连 / 每条具名代理各一份），任务之间共享；任务建好后路线不再受设置变更影响
  （`DownloadTask.Route` 在建任务时由 `ProxyResolver` 解析固化，重启按 `task_extras.proxy_id` 重新解析）。
- 读超时用"每次读之间"的计时器实现（对应 Kotlin `readTimeoutSeconds = 60`），
  不能只用 `HttpClient.Timeout`（那是整个请求的超时）。
- 取消：`CancellationToken` + 主动 `response.Dispose()` 打断阻塞读（对应 Kotlin `HttpResponse.cancel`）。
- Cookie：任务级 `Cookie` 头原样附带，不启用 `CookieContainer`（避免跨任务污染）。

## 12. 后续扩展：HF 模型下载（V2）与 BT 下载（V3）

首期只实现 HTTP 单文件下载，但**任务模型、来源接口、schema、UI 行模板必须现在就长成能容纳
多协议 / 多文件 / 带校验的形状**。本节是"现在不做，但别做死"的契约清单。

### 12.1 首期必须守住的 6 条契约

| # | 契约 | 违反后果 |
|---|---|---|
| 1 | 任务表有 `kind` + `parent_id`，任务模型支持父子（父 = 模型包 / torrent，子 = 文件）；首期就写进去，子项数恒为 1 | V2/V3 要改 schema + 迁移 + UI，返工面最大 |
| 2 | 多文件任务的 `Downloaded` 由子任务 `Synced` 汇总，不在父上单独计数 | 父行进度与子行不一致，恢复时对不上 |
| 3 | 引擎只见 `SourceOutcome` 语义，**不见 HTTP 状态码**；429/403 的判定封在 `RangeHttpClient` 内 | BT/镜像站无法复用调度逻辑，只能复制一份 |
| 4 | `Segment` 支持可选 `SegmentVerifier`（校验通过才 `Synced`）；首期传 null 但字段与调用点都在 | V3 piece 校验要改 `SegmentRunner` 核心循环 |
| 5 | 字节写入只经 `PartFile`（偏移写），不允许"整文件顺序写"的捷径 | BT piece 乱序到达无处落盘，等于重写存储层 |
| 6 | UI 行模板按"任务行 + 可选子行"设计，进度/大小/速度对父子同构；首期只渲染单行 | HF 千文件、BT 多文件在列表里无处展开 |

对应的 schema 预留（已在 §7.1 DDL 内）：`tasks.kind`、`tasks.parent_id`、`tasks.content_sha256`、
`tasks.source_meta_json`。`source_meta_json` 存协议私有信息（V2：repo / revision / path；
V3：infohash / torrent 路径），首期恒为 NULL。

### 12.2 HF（HuggingFace）模型下载

一个「模型任务」= 父任务 + N 个文件子任务。每个子文件仍走 `DownloadEngine`（HTTP Range 引擎），
所以**引擎零改动**，需要新写的是"仓库解析 + 文件编排"。

流程：

1. `GET {endpoint}/api/models/{repo}`（或 `/api/models/{repo}/tree/{rev}?recursive=true&expand=true`）
   → 文件清单：`path`、`size`、`lfs.oid`（= sha256）、`lfs.size`。可选 `?blobs=true`。分页用 `Link` 头。
2. 逐文件 `GET {endpoint}/{repo}/resolve/{rev}/{path}` → **302** 到 CDN
   （`cdn-lfs.hf.co` / `cas-bridge.xethub.hf.co` / S3 预签名）。必须 `AllowAutoRedirect=false` 自己跟，
   因为要记录每跳的 `Authorization` 剥离规则：**跨域跳转到 CDN 时不带 Bearer**（否则 S3 拒绝）。
3. 预签名 URL 有时效（约 1 小时）→ 大文件中途失效返回 403 → `SourceOutcome.Expired`
   → 重新调 `resolve` 拿新 URL 续传，**不消耗重试预算**。
4. 认证：`Authorization: Bearer <token>`；token 来源顺序：任务级 → 设置 → `HF_TOKEN` 环境变量。
5. 镜像：`HF_ENDPOINT`（如 `https://hf-mirror.com`），设置页可改；清单与 resolve 都走同一 endpoint。
6. 并发：**文件级并发（`AppSettings.MaxFilesPerTask`，默认 3）+ 文件内分段并发**，两者共用
   `ConnectionSlots` 全局配额；避免 1000 文件 × 8 段 = 8000 连接的灾难（实现见 §6.2）。
7. 校验：`size` 一致 → 写入 `content_sha256`；完成后校验（LFS 文件才有 sha256）。
   已存在且 size/sha256 一致 → 跳过（增量更新模型）。
8. 目录：`path` 里的子目录必须保留（`onnx/model.onnx` → 目标目录下的 `onnx/model.onnx`）；
   `.gitattributes` 等小文件同样下载。
9. revision：记 `main` / tag / commit hash；`source_meta_json` 存 `{endpoint, repo, revision, path}`。

命名空间：`Xdm.Core.HuggingFace/`。不放进 `Http/`，因为它属于"来源编排"，不是传输层。

测试：不访问真实 Hub —— 用 `FakeHttpServer` 起一个假 Hub（`/api/models/...` 返回固定清单、
`/resolve/...` 302 到自身另一路径），再叠加"签名 URL 过期一次"的剧本。

#### 12.2.1 首版已落地（2026-11-23）

| 文件 | 职责 |
|---|---|
| `HfRepo.cs` | `owner/name` 解析与校验、`ResolveUrl(path)` 拼 `{endpoint}/{repo}/resolve/{rev}/{path}?download=true` |
| `HfFile.cs` | 清单条目：`path` / `size` / `sha256`；`RelativeFolder`、`IsSafePath`（拒 `..`、绝对路径、`//`、`\`） |
| `HfHubClient.cs` | `GET {endpoint}/api/models/{repo}/tree/{rev}?recursive=true&expand=true` 分页拉清单；`CreateHandler` 开自动重定向 + 全部解压（与下载客户端相反），按 `ProxyRoute` 挂代理 |
| `HfDownloadPlanner.cs` | `RootFolder` / `Plan`（落盘位置 + 按大小跳过已有文件）/ `ToRequests`（建子目录 → `DownloadRequest`） |

与上文规划的差异（首版取舍）：

1. **父行已在 2026-11-23 补上**（见 §12.2.2）；`HfDownloadPlanner.ToGroup` 出 `DownloadGroupRequest`。
2. **重名策略 `Overwrite`**：模型目录是仓库镜像，大小变了就是文件更新。若走默认 `Rename` 会产出
   `model (1).onnx`，推理框架按原名加载就读不到。大小一致的文件已由 `Plan` 跳过，所以覆盖是安全的。
3. **清单请求与下载请求用两套 handler**：清单要跟 307 并解压 JSON；下载仍关重定向、关自动解压，
   由引擎自己处理 `Expired` 与跨域剥离 `Authorization`。
4. **token 顺序**：设置页 → `HF_TOKEN` 环境变量（任务级 token 首版不做）；只给 `resolve` 下发的请求带 `Bearer`，
   清单请求同样带上以便访问 gated 仓库。
5. **endpoint 归一**：`AppSettings.SanitizeEndpoint` 去空白/去查询串，非法值回落 `https://huggingface.co`。
6. **发布前校验**：`DownloadEngine.Complete` 在 part 已写满并 flush 之后、改名发布之前，用 `SegmentVerifier`
   比对 `ContentSha256`；不符则删掉 part 并报 `DownloadError.Checksum`（文案「内容校验失败」），重试即从头下。
   只有 LFS 文件带 sha256，普通小文件跳过（`ContentSha256` 为空）。清单请求与下载请求共用选中的代理：
   对话框每点一次「拉取文件」都按当前代理单选组重建清单客户端。
7. **未做**：`cache_edit`/权限字段过滤、多连接加速（`PLAN.md` 待决 #6）。

#### 12.2.2 父任务汇总（2026-11-23，契约 1/2 落地）

- **建任务**：`DownloadManager.EnqueueGroup(DownloadGroupRequest)` 先插一条父行（`file_name = owner/name`、
  `max_segments = 1`、`kind = HuggingFace`、`source_meta_json` 带 `container: true`），再逐文件
  `AddTask(file, parentId)` 插子行。父行**不进调度队列**，引擎永远看不到它。
- **汇总规则**（`AggregateLocked`）：`SizeBytes = Σ子.TotalSize`、`SyncedBytes = Σ子.SyncedTotal`、
  `SpeedBps = Σ子速度`、`ChildCount/CompletedChildren = 子数/已完成子数`；状态推导顺序是
  「全完成 → `Completed`；有子在跑 → `Downloading`；有失败且无在跑 → `Failed`/`DiskError`（错误文案取第一个失败的）；
  有排队 → `Queued`；否则 `Paused`」。
- **联动**：`Publish(childId)` 末尾按 `task.ParentId` 调 `RefreshContainer`，父行以 `TaskUpdated` 重新推送；
  状态真变了才 `UpdateStatus` 落库，进度交给 30s 的 `PersistTick`（否则 12 个文件的 4Hz 事件会把库刷爆）。
- **级联**：`Pause` / `Resume` / `Delete` 遇到父行时逐个作用到子任务；`Delete(parent)` 先删子再删父，
  不会留下孤儿行。
- **重启恢复**：`parent_id` 分组即父子关系——**有子行的才是父行**（首版按文件建的 HF 任务没有子行，
  仍按普通任务显示，无需数据迁移）；父行不做「已完成文件是否存在」检查（它的 `file_name` 是仓库名），
  状态与完成时间全部按子任务重算。
- **UI**：`MainWindowViewModel.AddRange` 跳过 `ParentId != null` 的行，列表只有父行；
  第二行中部由「段」换成「个文件」（`3/12 个文件 · D:\Downloads`），图标标签为「包」；
  完成通知只按父行报一次。

### 12.3 BitTorrent（V3）

**结论：BT 用独立引擎，不复用 `DownloadEngine`。** 共享的是基础设施：
`PartFile`（偏移写）、`SpeedLimiter`（含上传限速）、`ConnectionSlots`、`TaskStore`、`ProgressTracker`、UI。

| 维度 | HTTP 分段引擎 | BT 引擎 |
|---|---|---|
| 数据单位 | 段（可任意切） | piece（长度由 torrent 定，**不可切**） |
| 来源 | 单个服务器 | 多个 peer（每个 peer 只给部分 piece） |
| 完成判定 | 字节收到即计入 `Synced` | 整 piece 校验通过才计入 |
| 重试对象 | 同一 URL | 换 peer / 等 swap |
| 上传 | 无 | 有（做种/上报），首期可关 |

要点：

- 协议面：BEP 3、5(DHT)、6(fast)、9(metadata/magnet)、10(扩展)、11(PEX)、15(UDP tracker)、19(webseed)。
- 元数据：`.torrent`（bencode 解析）与 magnet（BEP 9 交换）；v1 SHA1 piece、v2 SHA256 都要认。
- piece ↔ `Segment`：`Segment.Length` 取 piece 长度，`Segment.Verifier` 存 piece hash；
  一个 piece 可能来自多个 peer（分块请求），拼齐后校验，通过才 `Synced += length`。
- 磁盘：稀疏写（`PartFile.SetLength(total)`），不预分配；完成后逐 piece 校验再发布。
  `PartAllocation`（无稀疏文件预填充）对 BT 无意义，BT 只需保证"不写未校验数据到可见区"。
- 多文件 torrent → 天然两级任务；"仅下载选中文件"通过跳过对应 piece 实现（piece 跨文件时要整块下）。
- 做种：V3 只支持"完成后按配置不做种 / 最小做种比例"；上传调度、私有 tracker 严格上报放 V4。
- webseed 是 HTTP Range → 直接复用 `SegmentRunner`，这是 V3 里唯一能复用 HTTP 引擎的路径。
- 库选型：优先 `MonoTorrent`（MIT，GPL-3.0 兼容），封装在 `Xdm.Core.BitTorrent/ITorrentEngine` 之后，
  保证将来可替换为自研最小实现（仅 tracker + 无 DHT）。选型结论记 `PROGRESS.md` 待决事项。

命名空间规划：`Xdm.Core.BitTorrent/`（`TorrentEngine.cs`、`TorrentMetadata.cs`、`PeerSwarm.cs`、
`PieceStore.cs`）。`PieceStore` 内部用 `PartFile` 偏移写，piece 校验逻辑集中在此，不散落到 UI/任务层。

### 12.4 工程演进（不新增工程，除非被迫）

- V2：仍是 `Xdm.Core`，只加命名空间 `Xdm.Core.HuggingFace`；不需要新包（HTTP 已够）。
- V3：若引 MonoTorrent，它进 `Xdm.Core`（或单独 `Xdm.Core.BitTorrent` 程序集后由 Core 引用）。
  **不得**让它污染 `Xdm.Ui.Mew` / `Xdm.App.Mew`（保持 UI 与具体协议无关）。
- 任何阶段都不允许在 `Xdm.Ui.Mew` 里出现协议相关 `using`（`System.Net.Http`、MonoTorrent 等）。
