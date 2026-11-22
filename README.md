# Xdm

Windows 桌面多线程下载工具（HTTP/HTTPS 分段下载 + 断点续传），C# + Avalonia 12 + .NET 10。

- 参考实现：`refcode/xdm-master`（XDM，Kotlin / Swing / GPL-3.0-or-later）
- 已安装的参考程序：`D:\network\XDM\xdm-app.exe`（v9.0.11，jpackage + JDK 25；数据目录
  `%LOCALAPPDATA%\xdm-app`，日志在 `logs/`）—— 行为/文案/布局有疑问时直接开它对照
- 本项目许可：GPL-3.0（见根目录 `LICENSE`），属参考实现的衍生作品
- 首期范围：普通 HTTP/HTTPS 文件下载（多段并发 + 断点续传 + 限速 + 队列）
- 后续路线：**V2 HF（HuggingFace）模型下载** → **V3 BT / 磁力下载**（架构预留见 `docs/ARCHITECTURE.md` §12）
- 首期**不含**：HF、BT、视频嗅探、HLS/DASH、浏览器扩展、媒体转封装

## 命名约定

| 项 | 值 |
|---|---|
| 仓库目录 | `F:\code\XdmSharp`（不变） |
| 解决方案 | `XdmSharp.sln` |
| 工程 / 程序集 | `Xdm.Core`、`Xdm.Ui`、`Xdm.App`、`Xdm.Core.Tests`、`Xdm.Ui.Tests` |
| 命名空间根 | **`Xdm`**（如 `Xdm.Core.Http`、`Xdm.Ui.ViewModels`） |
| XAML | `x:Class="Xdm.Ui.Views.MainWindow"` |

## 渲染后端：SimdAIO

首期即使用 [`Sevencat.Avalonia.SimdAIO`](https://www.nuget.org/packages/Sevencat.Avalonia.SimdAIO)
（纯托管 SimdSkia，无原生库，`publish` 出来是单个 ~12MB 的 exe）。工程只需两个包：

```xml
<PackageReference Include="Avalonia" />
<PackageReference Include="Sevencat.Avalonia.SimdAIO" />
```

启动链固定在 `src/Xdm.App/Program.cs`：`UseStandardRuntimePlatformSubsystem()` + `UseWin32()`
+ `Win32RenderingMode.Software`（SimdSkia 走软件 framebuffer，不能留 AngleEgl）
+ `UseSimdSkia(SimdSkiaGpuOptions.UseGpuPreferred)`（Direct2D 优先、失败回退 CPU）。

配套红线见 `docs/ARCHITECTURE.md` §2.1：不能再引 `Avalonia.Desktop` / `Avalonia.Win32` /
`Avalonia.Skia` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter`（AIO 已自带 Fluent 主题）。
若将来要回退官方桌面栈，改动同样只在 `Program.cs` + `Xdm.App.csproj`，约束见 §2.2。

## 技术栈

| 项 | 版本 | 首期 | 说明 |
|---|---|---|---|
| .NET | `net10.0` | ✔ | TFM 不写 `-windows`；`RuntimeIdentifier` 只在 `dotnet publish -r win-x64` 时给 |
| Avalonia | `12.1.6` | ✔ | 12.x API，与 11.x 差异较大 |
| Sevencat.Avalonia.SimdAIO | `12.1.6.2` | ✔ | 启动栈与渲染；自带 Win32/OpenGL/MicroCom 合并实现 + Fluent 主题；仅 win-x64 |
| Avalonia.Themes.Fluent | `12.1.6` | 仅测试 | `Xdm.Ui.Tests` 的 Headless 宿主用；**App 不能引** |
| Avalonia.Headless.XUnit | `12.1.6` | ✔ | 界面测试（只进 `Xdm.Ui.Tests`） |
| xunit.v3 | `3.2.2` | ✔ | 与 Headless.XUnit 的依赖版本一致 |
| PureDb | `1.0.0-rc.1` | ✔ | 任务库（纯托管嵌入式，无原生依赖；仅 net10.0） |

以上版本本机 NuGet 缓存（`E:\program\nuget\cache`）均已存在，可离线还原。

## 数据文件

任务是**绿色版**布局：任务库与 exe 同目录。

| 文件 | 位置 | 说明 |
|---|---|---|
| `xdm.db` | `AppContext.BaseDirectory\xdm.db` | PureDb 任务库；旁边有 `xdm.db.lock`（访问锁），写入量上去后还会生成 `xdm.db.pidx`（页索引），备份/迁移要一起拷 |
| `state/<id>.json` | 同目录 `state/` | 分段进度快照（见 `ARCHITECTURE.md` §7.2） |
| `.<id>.xdm-part` | 任务的目标目录 | 未完成的临时文件（点前缀 + `.xdm-part`，与参考实现一致），发布时原子改名 |

exe 目录不可写（装在 `Program Files`、只读介质）时自动回退到 `%LOCALAPPDATA%\Xdm\`，不弹窗阻断。

库位置可以用命令行覆盖（`state\` 始终跟着库文件所在目录走）：

```powershell
.\xdm-app.exe                              # 默认：exe 同目录 xdm.db
.\xdm-app.exe --db D:\XdmData\xdm.db       # 指定库文件；state\ 落在 D:\XdmData\state\
dotnet run --project src/Xdm.App -- --db .\dev.db   # 开发期放仓库根，不污染 bin/
```

优先级：`--db <path>` > `XDM_DB` > `XDM_DATA_DIR`（目录语义）> exe 同目录 > `%LOCALAPPDATA%\Xdm\`。
`--db` 指向已存在的目录或缺少取值时直接报错退出（返回码 2）；库文件打不开（权限/磁盘）返回码 3，
不会静默回退或空表启动 —— 免得对着错误的库排查。

## 工程结构

```
XdmSharp.sln
global.json                    SDK 固定 10.0.101
Directory.Build.props          统一 TFM / Nullable / TreatWarningsAsErrors
Directory.Packages.props       中央包版本管理
build.ps1                      build [-Test] [-Publish] 快捷脚本
src/
  Xdm.Core/                    下载引擎、HTTP 客户端、存储、任务库（不依赖 UI）
    Configuration/AppPaths.cs  --db / 环境变量 / 目录回退的路径解析
    Errors/DownloadError.cs    协议无关的失败原因 + 中文文案
    Http/                      RangeHttpClient（自跟重定向、状态码→SourceOutcome）、请求/响应模型
    Storage/PartFile.cs        未完成文件：单句柄 + 偏移写（多段并发无需锁）
    Storage/AtomicJson.cs      .tmp → fsync → rename 的原子 JSON
    Storage/FileNaming.cs      Content-Disposition / URL → 文件名，非法字符与重名
    Storage/NameConflictPolicy.cs 发布重名策略（自动改名 / 覆盖 / 失败）
    Storage/SegmentVerifier.cs SHA256 校验挂点（V2 LFS / V3 piece）
    Tasks/TaskStore.cs         PureDb 任务库（建表、CRUD、启动恢复）
    Tasks/StoredTask.cs        任务行 DTO（含 kind/parent_id/段快照等预留字段）
    Scheduling/DownloadQueue.cs FIFO 待启动队列（并发闸门在管理器）
    Downloads/DownloadEngine.cs  探测 → 等分建段 → 段线程 → 发布；暂停/恢复/删除
    Downloads/DownloadManager.cs 任务表 + 全局闸门（默认 3）+ 调度线程 + 快照事件 + 任务库落盘
    Downloads/DownloadRequest.cs 新建下载入参（URL / 目录 / 文件名 / 段数 / 是否立即开始）
    Downloads/SegmentRunner.cs   单段 Range 循环：fsync 水位、退避重试、断流续写
    Downloads/                  Segment / DownloadTask / DownloadState / ProgressTracker
                                DownloadStatus（状态枚举）、DownloadRecord（不可变视图数据）
  Xdm.Ui/                      Avalonia 视图 / ViewModel / 样式（只引 Avalonia，不引启动栈）
    Views/MainWindow.axaml     主窗口：工具栏 + 分类 + 任务列表 + 状态栏
    Views/NewDownloadWindow.axaml   新建下载（多行 URL / 目录选择 / 段数）
    Views/SettingsWindow.axaml      设置页骨架（常规/连接/目录/网络）
    Controls/DownloadRow.axaml      列表行模板（两行式，悬停出操作）
    Controls/SegmentBar.cs          分段进度条（自绘 Control，不走 ProgressBar）
    ViewModels/                 MainWindow / DownloadRow / Category / NewDownload + ViewModelBase/RelayCommand
    Formatting/TextFormat.cs    字节 / 速度 / 剩余时间 / 百分比的唯一文案入口
    Services/IDownloadActions.cs 界面命令到引擎的出口（App 实现，测试可注入假实现）
  Xdm.App/                     Windows 壳：Program.cs + App.axaml + SimdAIO 启动链
    DownloadUiBridge.cs        引擎事件 → UI 线程（结构变化立即投递，进度 4Hz 合并）
    UiDownloadActions.cs       IDownloadActions → DownloadManager
    SampleDownloads.cs         假数据（`XDM_DEMO=1000` 时启用，虚拟化压测用）
tests/
  Xdm.Core.Tests/              引擎单测 + 自建 TCP 级假 HTTP 服务器（Fakes/FakeHttpServer）
  Xdm.Ui.Tests/                Avalonia.Headless.XUnit 界面测试（无窗口）
artifacts/                     手工冒烟截图（m1-main.png 等，不入库）
docs/
  PLAN.md                      总体计划、里程碑（界面优先）
  ARCHITECTURE.md              分层、类设计、渲染后端切换口、Kotlin→C# 对照
  UI-DESIGN.md                 窗口清单、列表行规范、交互
  TESTING.md                   测试策略与 Headless 配置
  PROGRESS.md                  进度台账
refcode/xdm-master/            参考实现（只读，勿改）
```

拆出 `Xdm.Ui` 而不是把视图塞进 App：一是界面测试工程可以只依赖 Avalonia、不拖入启动栈；
二是将来换渲染后端时 UI 层完全不动。详见 `docs/TESTING.md`。

## 后续协议扩展（现在不做，但形状已留）

| 阶段 | 内容 | 复用 / 新增 |
|---|---|---|
| V2 | HF 模型下载：仓库清单 + 多文件 + LFS sha256 + 镜像/Token | 复用 HTTP 分段引擎；新增 `Xdm.Core.HuggingFace`（Hub 解析、文件编排） |
| V3 | BT / 磁力：peer swarm + piece 校验 | 复用 `PartFile` / 限速 / 任务库 / UI；**独立 `TorrentEngine`** |

首期已经落地的预留点：`TaskKind` + `parent_id`（两级任务）、`SourceOutcome`（协议无关的连接结果）、
`SegmentVerifier`（piece/LFS 校验挂点）、`tasks.content_sha256` / `source_meta_json` 字段。
细节与"违反就返工"的 6 条契约见 `docs/ARCHITECTURE.md` §12.1。

## 常用命令

```powershell
dotnet restore
dotnet build                                  # 或 .\build.ps1
dotnet run --project src/Xdm.App -- --db .\dev.db
$env:XDM_DEMO=1000; dotnet run --project src/Xdm.App   # 1000 条假数据，复验列表虚拟化
dotnet test                                   # 全部测试（157 条：Core 106 + Ui 51）
dotnet test tests/Xdm.Ui.Tests                # 界面测试（Headless，无需窗口，约 1 s）
.\build.ps1 -Configuration Release -Test -Publish   # ①构建 ②测试 ③发布 win-x64

# 发布产物：publish\win-x64\xdm-app.exe（AIO 单文件，约 12MB，framework-dependent）
# 目标机需 .NET 10 运行时；是否改 self-contained 见 docs/PROGRESS.md 待决事项
```

## 文档

| 文档 | 内容 |
|---|---|
| [docs/PLAN.md](docs/PLAN.md) | 总体原则、首期范围、里程碑、验收、风险、工作量 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 分层依赖、Core 类设计、线程模型、持久化格式、UI↔引擎边界、SimdAIO 渲染栈与回退口、HF/BT 扩展预留（§12） |
| [docs/UI-DESIGN.md](docs/UI-DESIGN.md) | 界面结构与视觉规范（界面先行的依据） |
| [docs/TESTING.md](docs/TESTING.md) | 引擎测试矩阵、Avalonia Headless 用法与限制 |
| [docs/PROGRESS.md](docs/PROGRESS.md) | 里程碑进度与验证记录 |
