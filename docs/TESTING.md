# 测试策略

命名空间根 `Xdm`，工程 `Xdm.Core.Tests` / `Xdm.Ui.Mew.Tests`，目标框架 `net10.0`。

## 1. 分层

| 层 | 工程 | 范围 | 依赖 |
|---|---|---|---|
| L1 单元 | `Xdm.Core.Tests` | 文件名推导、重名策略、退避算法、令牌桶、进度滑动窗口、JSON 状态读写 | 无网络（临时目录除外） |
| L2 引擎集成 | `Xdm.Core.Tests` | 真实 HTTP 流程，跑自建 TCP 假服务器 | `TcpListener` 本地回环 |
| L3 视图 | `Xdm.Ui.Mew.Tests` | 控件树结构、文案、命令桥、对话框、主题 token | 无需窗口（布局类按需注册后端） |
| L4 手工 | — | 大文件、真实网络、断电恢复、发布产物、视觉截图 | 人工 |

原则：L1/L2/L3 必须能在无网络、无桌面窗口的机器上全绿（CI 可跑）；只有布局类用例可能
因注册不到 Direct2D 后端而 `Assert.Skip`，不算失败。

---

## 2. 假 HTTP 服务器

**用 `TcpListener` 手写最小 HTTP/1.1 服务器**，不用 `HttpListener`：

- `HttpListener` 依赖 http.sys，需要 URL ACL，且无法构造畸形响应；
- 本项目要测"服务器不守规矩"的场景（忽略 Range、长度造假、中途断流、慢速、并发拒绝），
  必须能逐字节控制响应。参考实现也是这么做的（`MockHttpServer.kt` / `RawDropServer.kt`）。

```csharp
namespace Xdm.Core.Tests.Fakes;

public sealed class FakeHttpServer : IDisposable
{
    public FakeHttpServer();                                    // 端口自动分配，仅回环
    public int Port { get; }
    public string BaseUrl { get; }                              // http://127.0.0.1:{Port}
    public IReadOnlyList<RecordedRequest> Requests { get; }      // 断言收到的 Range / If-Range
    public void AddFile(string path, byte[] content, FileBehavior? behavior = null);
    public void UpdateFile(string path, byte[] content, FileBehavior? behavior = null); // 造"服务器文件变了"
    public void ResetRequests();
}

public sealed record RecordedRequest(string Path, string? Range, string? IfRange);

public sealed class FileBehavior
{
    public bool SupportsRange { get; init; } = true;      // false → 恒回 200 全量
    public bool OmitContentLength { get; init; }           // close 定界
    public long? LengthOverride { get; init; }             // 造假 Content-Length
    public long? ForcedRangeLength { get; init; }          // 造假 Content-Range 长度（拒写用例）
    public int? DropAfterBytes { get; init; }              // 首次真下载写 N 字节后硬断
    public TimeSpan PerChunkDelay { get; init; }           // 慢速回包（暂停/续传用例靠它）
    public int ChunkSize { get; init; } = 16 * 1024;
    public string? ContentDisposition { get; init; }       // 含 RFC5987 编码用例
    public string? LastModified { get; init; }
    public int? StatusCode { get; init; }                  // 404 / 429 / 503
    public int? RetryAfterSeconds { get; init; }
}
```

行为清单（每条至少对应一个测试）：

| 能力 | 用途 |
|---|---|
| 标准 Range（206 + `Content-Range`） | 多段基线 |
| 忽略 Range（恒 200 全量） | 能力判定、退化单流 |
| 无 `Content-Length`（close 定界 / chunked） | EOF 模式 |
| `Content-Length` 与请求区间不符 | 拒写，不放坏数据 |
| 传输中途断流 | 重试与续传 |
| 429 / 503 + `Retry-After` | 服务端繁忙等待、不耗重试预算 |
| 403/410（其他连接仍有流量） | 判定"连接数限制"而非"链接过期" |
| 慢速回包（逐块延迟） | 读超时、限速吞吐 |
| 302 跳转链（跳转后 `Last-Modified` 变化） | `FinalUrl`、`If-Range` 行为 |
| 要求认证（401 + `WWW-Authenticate`） | 请求头透传 |
| 并发连接数上限 | 段数协商 |

> 现状（2026-11-22）：Range、忽略 Range、无 `Content-Length`、长度造假、中途断流、429/503、
> 403/410、慢速回包均已实现并被用例覆盖。302 跳转链、401 认证、并发上限、"读挂住不回包"
> 四类还没加进 `FakeHttpServer`，等 M5 / M6 真正需要时再补，
> 避免假服务器先长出没人用的分支。

---

## 3. 引擎测试矩阵

来源为参考实现 `xdm-core/src/test/java/xdm/core/`（Kotlin），逐条对应：

| 参考测试 | C# 测试类（`Xdm.Core.Tests`） | 覆盖点 | 状态 |
|---|---|---|---|
| `MockHttpServer.kt` (353) | `Fakes/FakeHttpServer.cs` | 测试基础设施（Range / 断流 / 无长度 / 慢速 / 状态码） | 已落地 |
| `HttpDownloadTestBase.kt` (280) | `Fakes/EngineHarness.cs` | 夹具：临时目录、服务器、任务构造、观察者 | 已落地 |
| `TestHttpSegmentedDownload.kt` (472) | `MultiSegmentDownloadTests.cs` + `SingleSegmentDownloadTests.cs` | 等分、段数上限、区间起点、进度汇总 | 等分已落地；动态二分 → M6 |
| `TestHttpSegmentStealing.kt` (392) | `SegmentTakeoverTests.cs` | 段接管、连接复用、接管冷却 5s | M6 |
| `TestHttpTailRace.kt` (371) | `TailRaceTests.cs` | 尾部竞速、限速时禁用、竞速段不重叠 | M6 |
| `TestHttpResumeCompletedChunk.kt` (471) | `ResumeTests.cs` | 已完成段跳过、暂停→恢复从非零 offset 续、`If-Range`、文件变化后整任务重来 | 已落地 |
| `TestHttpDownloadIntegrity.kt` (248) | `IntegrityTests.cs` | 最终文件 SHA256/逐字节比对、段区间不重叠且全覆盖 | 已落地 |
| `TestHttpRetryHandling.kt` (160) | `FailureTests.cs` | 断流重试从已写字节续、503 + `Retry-After` 等待上限、404 不重试 | 已落地（退避曲线未单测） |
| `TestHttpRateLimit.kt` (168) + `TestSpeedLimiter.kt` (41) | `SpeedLimitTests.cs` | 吞吐上限、跨任务共享全局额度 | M6 |
| `TestHttpStalls.kt` (97) + `RawDropServer.kt` (115) | `FailureTests.cs`（`TruncatedResponse_RetriesFromWrittenBytes`） | 断流后恢复 | 断流已落地；纯读超时用例待补 |
| `TestHttpConnectionLimit.kt` (59) | `ConnectionLimitTests.cs` | 段数与服务器上限协商 | M6 |
| `TestHttpDownloadEdgeCases.kt` (74) | `SingleSegmentDownloadTests.cs` | 0 字节、无 Range、无 `Content-Length` 的 EOF 模式 | 已落地（1 字节边界待补） |
| `TestHttpResourceCleanup.kt` (106) | `PartFileTests.cs` + `ResumeTests.Delete_RemovesPartAndState` | 暂停/删除后句柄释放、临时文件可删 | 已落地（句柄计数统计未做） |
| `TestHttpSharedOutput.kt` (382) | `PartFileTests.cs` + 多段下载用例 | 单写句柄并发偏移写 | 多线程逐字节正确性已覆盖，显式并发压测 → M6 |
| `TestPositionalOutput.kt` (292) | `PartFileTests.cs` | 偏移写、越界断言、未知长度增长 | 已落地 |
| `TestHttpPartAllocation.kt` (309) | `PartAllocationTests.cs` | 无稀疏文件预填充 | M6 |
| `TestLocalStorage.kt` (126) / `TestAtomicIO.kt` (127) | `AtomicJsonTests.cs` | 原子写、临时文件不残留、损坏快照读回 `null` | 已落地（断电模拟未做） |
| `TestTaskInfoDB.kt` (311) | `TaskStoreTests.cs` | PureDb 增删改查、启动恢复 | M5 |
| `TestFileMove.kt` (311) | `PublishTests.cs` | 发布、重名三策略、跨盘 | 发布 + 重名已落地，跨盘/三策略 → M5 |
| `TestDispositionFileName.kt` (62) | `FileNamingTests.cs` | `Content-Disposition` / RFC5987 / 非法字符 / 保留名 / 重名 | 已落地 |

不在上表但已落地的：`ProgressTrackerTests.cs`（滑动窗口速率与 ETA）、
`IntegrityTests.Sha256_KnownVectors`（空串 / `abc` 标准向量）。

断言要点（防止"看起来完成了"）：

1. **区间不重叠**：所有段 `[Offset, Offset+Length)` 两两不交，并集等于 `[0, TotalSize)`。
2. **内容一致**：最终文件 SHA256 == 服务器内容 SHA256。
3. **进度单调**：`Synced` 之和只增不减；崩溃恢复后回退量 ≤ 30 秒数据量。
4. **句柄归零**：暂停/失败后自建句柄计数归零，临时文件可被删除。
5. **不越界写**：`PartFile.Write(offset, buffer)` 内部断言 `offset + len <= Length`。

---

## 4. 视图层测试（MewUI）

MewUI 0.22 **没有 Headless 宿主**：`Measure/Arrange` 需要一个真实渲染后端，`Show()` 会真开窗口。
所以测试分两档：

1. **结构断言**（绝大多数）：不 `Show()` 的控件树就是普通对象图，可以直接构造、遍历、查文案。
2. **布局断言**（少数）：按需注册 Win32 + Direct2D 后端后 `Measure`，注册失败就 `Assert.Skip`。

### 4.1 工程配置

```xml
<!-- tests/Xdm.Ui.Mew.Tests/Xdm.Ui.Mew.Tests.csproj -->
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="3.2.2" />
  <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
  <PackageReference Include="Aprillz.MewUI.Platform.Win32" Version="0.22.1" />
  <PackageReference Include="Aprillz.MewUI.Backend.Direct2D" Version="0.22.1" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\Xdm.Ui.Mew\Xdm.Ui.Mew.csproj" />
</ItemGroup>
```

> 只依赖 `Xdm.Ui.Mew`，**不引 `Xdm.App.Mew`**：壳里的平台/后端注册是进程级全局状态，
> 测试要自己控制注册时机（且允许跳过）。

### 4.2 必须关并行

```csharp
// tests/Xdm.Ui.Mew.Tests/AssemblyInfo.cs
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

MewUI 的控件**不是线程安全的**：父子关系建立、继承属性刷新都假定单线程。并行跑会让
`RefreshInheritedSubtree` 抛 `ArgumentOutOfRangeException`（踩过，见 `PLAN-MEWUI.md` 坑 23）。

### 4.3 测试写法

```csharp
public sealed class MainWindowTests
{
    [Fact]
    public void 主窗口包含工具栏与分类()          // 纯结构，不 Show
    {
        var window = new MainWindow(viewModel);
        var texts = VisualTree.Texts(window);      // 遍历子树收集文案
        Assert.Contains("新建", texts);
    }

    [Fact]
    public void 列表行高度符合规范()              // 需要后端
    {
        TestPlatform.EnsureRegistered();           // 失败即 Assert.Skip
        var row = new RowHost(...);
        row.Measure(new Size(600, double.PositiveInfinity));
        Assert.Equal(MewTheme.RowHeight, row.DesiredSize.Height);
    }
}
```

规则：

- **一律 `[Fact]`**（没有 `[AvaloniaFact]` 这种宿主钩子）；需要后端的用例显式调 `TestPlatform.EnsureRegistered()`。
- 文案三处存放：`TextBlock.Text`、`AccessText.RawText`（内部类型，只能反射）、`TextBox.Text`——
  断言文案一律走 `VisualTree.Texts`，别自己找控件（`AccessText` 是 internal）。
- 命令测 `MewCommandBridge.CanExecute()/Execute()`，不模拟鼠标。
- 外部副作用（打开文件/目录、系统对话框）走注入接口，测试换假实现。

### 4.4 能做与不能做

| 能做 | 不能做 / 限制 |
|---|---|
| 控件树结构、文案、`IsEnabled`、颜色 token | 像素级比对（无 headless 光栅） |
| 命令桥 `CanExecute`/`Execute` 与 VM 联动 | 真实点击/键盘输入（可调内部方法，但不是端到端） |
| 对话框标题栏/按钮布局（`Measure` 后） | 列表行内容（`ListBox` 不为每行建元素，只能截图验收） |
| 主题 token 全量（`MewTheme` 与规范对齐） | 托盘、系统通知（需要真实桌面，用接口打桩） |

**视觉回归**：MewUI 没有 `CaptureRenderedFrame` 这类 API。视觉验收靠 `PROGRESS.md` 里的
端到端截图脚本（启动 → 截图 → 人工比对），不进 CI。

### 4.5 交付的视图层测试清单（42 条）

| 文件 | 覆盖 |
|---|---|
| `MainWindowTests` | 结构/工具栏/分类/空态、VM 绑定、命令可用性 |
| `MewCommandBridgeTests` | `ICommand` → `Command` 的 CanExecute/Execute 映射 |
| `DialogChromeTests` | 各对话框标题栏、按钮区、尺寸 |
| `NewDownloadDialogTests` | 预填 URL、多行、目录/段数校验、`Submit()` 行为 |
| `EditBoxTests` | 中文右键菜单项 |
| `MewThemeTests` | 色值/尺寸 token 与 `UI-DESIGN.md` 对齐 |
| `TitleBarChromeTests` | 自绘标题栏的拖拽区与按钮 |
| `LayoutTests` | 依赖后端的度量（失败 `Assert.Skip`） |

---

## 5. 命令

```powershell
dotnet test                                     # 全部（197 条）
dotnet test tests/Xdm.Core.Tests                # 引擎 + 单元（155 条）
dotnet test tests/Xdm.Ui.Mew.Tests              # 视图层（42 条）
dotnet test --filter "FullyQualifiedName~ResumeTests"
dotnet test --collect:"XPlat Code Coverage"
```

覆盖率目标：`Xdm.Core` ≥ 80% 行覆盖，引擎类（`DownloadEngine` / `SegmentRunner`）≥ 90%；
`Xdm.Ui.Mew` 覆盖 `UI-DESIGN.md` 第 9 节列出的所有可断言点。

---

## 6. 手工冒烟清单（每个里程碑结束）

- [ ] 启动无异常，界面完整（明/暗主题各看一遍）
- [ ] 5000 条假任务滚动不掉帧（M1 起每次回归）
- [ ] 下载 1GB 真实文件，暂停 → 继续 → 完成，文件可用
- [ ] 下载中断网 30 秒，恢复后继续且文件校验通过
- [ ] 下载中直接杀进程，重启后任务在列表中且进度合理
- [ ] 目标目录只读 / 磁盘写满，报错清楚且不留垃圾
- [ ] 高 DPI（150%）缩放下布局不错位

---

## 7. V2/V3 协议的测试预留（现在只留口，不实现）

原则：新协议**不许把测试变成"打真实公网"**。每个协议都要有本地假对端。

| 阶段 | 假对端 | 关键剧本 |
|---|---|---|
| V2 HF | `FakeHttpServer` 扮成 Hub：`/api/models/{repo}` 返回固定清单；`/resolve/{repo}/{rev}/{path}` 302 到本机另一路径 | 签名 URL 过期一次后重新解析成功；跨域跳转时 `Authorization` 被剥离；LFS sha256 不符 → 该文件失败但父任务继续；已存在且校验通过的文件被跳过；1000 文件的编排不爆连接数 |
| V3 BT | 本地 `ITorrentEngine` 假实现（`FakeTorrentEngine`，直接喂 piece）+ 真实现时用合成 torrent（`tests/data/mini.torrent`，2 个文件、3 个 piece） | piece 校验失败位图不变；乱序 piece 落盘位置正确；跨文件 piece 只下一次；完成后整体校验通过才发布；上传开关关闭时不上报 |

同时要求：

- `SegmentVerifier` 在首期就要有单测（用固定字节 + SHA256 断言），保证 V2/V3 接上时不是死代码。
- `SourceOutcome` 的 6 个分支在首期 HTTP 侧全部覆盖（Ok / Retry / Busy / Unsupported / Expired / Rejected），
  否则 V2 的"签名 URL 过期"无处可落。
- 父子任务聚合进度的单测：父 = Σ 子 `Synced`，子失败不改变其他子，父状态机为"全部完成才完成"。
  这条首期就该有（子项恒为 1），V2/V3 直接复用。
