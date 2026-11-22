# 测试策略

命名空间根 `Xdm`，工程 `Xdm.Core.Tests` / `Xdm.Ui.Tests`，目标框架 `net10.0`。

## 1. 分层

| 层 | 工程 | 范围 | 依赖 |
|---|---|---|---|
| L1 单元 | `Xdm.Core.Tests` | 文件名推导、重名策略、退避算法、令牌桶、进度滑动窗口、JSON 状态读写 | 无网络（临时目录除外） |
| L2 引擎集成 | `Xdm.Core.Tests` | 真实 HTTP 流程，跑自建 TCP 假服务器 | `TcpListener` 本地回环 |
| L3 界面 | `Xdm.Ui.Tests` | 视图结构、绑定、命令、交互 | `Avalonia.Headless.XUnit`（无窗口） |
| L4 手工 | — | 大文件、真实网络、断电恢复、发布产物 | 人工 |

原则：L1/L2/L3 必须能在无网络、无桌面窗口的机器上全绿（CI 可跑）。

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
| `TestTaskInfoDB.kt` (311) | `TaskStoreTests.cs` | SQLite 增删改查、启动恢复 | M5 |
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

## 4. 界面测试（Avalonia Headless）

首期渲染后端是 SimdAIO（`Avalonia` + `Sevencat.Avalonia.SimdAIO`），**AIO 不进测试进程**：
测试工程只引用 `Xdm.Ui` + `Xdm.Core`，Headless 用官方包，理由见 4.1 的注释。

### 4.1 工程配置

```xml
<!-- tests/Xdm.Ui.Tests/Xdm.Ui.Tests.csproj -->
<ItemGroup>
  <PackageReference Include="Avalonia.Headless.XUnit" Version="12.1.6" />
  <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.6" />
  <PackageReference Include="xunit.v3" Version="3.2.2" />
  <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\Xdm.Ui\Xdm.Ui.csproj" />
  <ProjectReference Include="..\..\src\Xdm.Core\Xdm.Core.csproj" />
</ItemGroup>
```

> 为什么仍不引用 `Xdm.App`：App 带 SimdAIO（合并了 Win32/OpenGL/MicroCom，禁用官方
> Desktop/Win32/Skia），而 Headless 自带一套官方平台实现，**两者同进程必然冲突**。
> 测试只依赖 `Xdm.Ui`，所以 AIO 改动完全不影响测试；顺带测试跑得更快。
> Ui 测试需要主题，故直接引用同版本的 `Avalonia.Themes.Fluent`（只在测试工程，不进 App）。

### 4.2 应用装配

```csharp
// tests/Xdm.Ui.Tests/TestAppBuilder.cs
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Xdm.Ui.Tests.TestAppBuilder))]

namespace Xdm.Ui.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = true,   // 不接 Skia：纯结构测试
        });
}

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
```

### 4.3 测试写法

```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;
using Xdm.Ui.Views;

namespace Xdm.Ui.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void 空列表显示空态提示()
    {
        var vm = new MainWindowViewModel(FakeTasks.Empty());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var hint = window.FindControl<TextBlock>("Main_EmptyHint");
        Assert.NotNull(hint);
        Assert.True(hint!.IsVisible);
    }

    [AvaloniaFact]
    public void 已暂停任务时恢复按钮可用()
    {
        var vm = new MainWindowViewModel(FakeTasks.One(DownloadStatus.Paused));
        vm.SelectedIndex = 0;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<Button>("Main_ResumeButton")!.IsEnabled);
    }
}
```

规则：

- 一律 `[AvaloniaFact]` / `[AvaloniaTheory]`，**不能**用 `[Fact]`（Dispatcher 未初始化会炸）。
- 控件可见性/虚拟化容器需 `window.Show()` + `Dispatcher.UIThread.RunJobs()`。
- 时间推进用 `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`，**禁止 `Thread.Sleep`**。
- 外部副作用（打开文件、打开目录、系统对话框）全部走注入接口，测试里换 `FakeShellService`。

### 4.4 能做与不能做

| 能做 | 不能做 / 限制 |
|---|---|
| 控件树、`x:Name` 查找、绑定值断言 | 像素级比对（`UseHeadlessDrawing=true` 无真实光栅） |
| 点击 / 键盘输入 / 焦点 / 选择 | 字体度量与排版精确值 |
| `IsVisible`、`IsEnabled`、样式类、主题变体切换 | 真实渲染性能（只能人工测） |
| 命令执行、`INotifyPropertyChanged` 生效 | 拖放模拟（改走 ViewModel 命令间接测） |
| 虚拟化容器生成（给足窗口高度） | 托盘、系统通知（M6 用接口打桩） |

**截图基线**：`CaptureRenderedFrame()` 需要真实渲染后端（Skia）。
首期不额外引 `Avalonia.Skia` 到测试工程，故不做视觉回归；真要做就单建
`Xdm.Ui.Snapshot.Tests` 引用 `Avalonia.Skia`，只在本地/可选 CI 跑（P2）。
注意该工程将来无法与 SimdAIO 后端共用输出目录。

### 4.5 M2 交付的界面测试清单

| 对象 | 用例 |
|---|---|
| 主窗口 | 空态提示可见；3 条假数据 → `Main_TaskList.ItemCount == 3`；行内进度文本 `76%`；各状态徽标文案；分类过滤后计数正确；搜索过滤；多选时删除按钮可用 |
| 新建下载 | 空 URL 阻断；非法 scheme 阻断；三行 URL → 按钮显示`批量添加（3）`；目录不存在 → 错误提示可见 |
| 进度详情 | 4 段快照 → 4 个段项；>200 段 → 切聚合视图；速度曲线控件有点数据 |
| 设置 | 改段数 → VM 属性变化；取消 → 不触发保存；越界（0 / 300）被钳制 |
| 交互 | 双击已完成行触发 `OpenFileCommand`（`FakeShellService` 记录调用）；`Del` 弹出确认框 |

---

## 5. 命令

```powershell
dotnet test                                     # 全部
dotnet test tests/Xdm.Core.Tests                # 引擎 + 单元
dotnet test tests/Xdm.Ui.Tests                  # 界面（无窗口）
dotnet test --filter "FullyQualifiedName~ResumeTests"
dotnet test --collect:"XPlat Code Coverage"
```

覆盖率目标：`Xdm.Core` ≥ 80% 行覆盖，引擎类（`DownloadEngine` / `SegmentRunner`）≥ 90%；
`Xdm.Ui` 覆盖 `UI-DESIGN.md` 第 9 节列出的所有可断言点。

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
