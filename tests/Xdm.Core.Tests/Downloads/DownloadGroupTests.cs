using Xdm.Core.Downloads;
using Xdm.Core.Storage;
using Xdm.Core.Tasks;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

/// <summary>
/// 多文件任务（HF 模型包）的父行语义：父行只做汇总，暂停/恢复/删除级联到子任务，
/// 状态按「有文件在跑就算下载中、全完成才算完成」推导（<c>ARCHITECTURE.md</c> §12.1 契约 1/2）。
/// </summary>
public sealed class DownloadGroupTests
{
    private static DownloadManager CreateManager(
        EngineHarness harness,
        TaskStore? store = null,
        int maxFilesPerTask = 3,
        int maxConcurrent = 2)
        => new(new DownloadManagerOptions
        {
            MaxConcurrent = maxConcurrent,
            MaxFilesPerTask = maxFilesPerTask,
            StateDir = harness.StateDir,
            Client = harness.Client,
            Store = store,
            EngineOptions = EngineHarness.FastOptions,
        });

    private static string GroupFolder(EngineHarness harness)
    {
        var folder = Path.Combine(harness.TargetFolder, "owner", "name");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static DownloadGroupRequest NewGroup(EngineHarness harness, params string[] files)
    {
        var folder = GroupFolder(harness);
        return new DownloadGroupRequest
        {
            Name = "owner/name",
            Url = "https://hf.test/owner/name",
            TargetFolder = folder,
            Kind = TaskKind.HuggingFace,
            SourceMetaJson = """{"kind":"huggingface","container":true}""",
            Files =
            [
                .. files.Select(file => new DownloadRequest
                {
                    Url = harness.Server.BaseUrl + "/" + file,
                    TargetFolder = folder,
                    FileName = file,
                    MaxSegments = 2,
                    Kind = TaskKind.HuggingFace,

                    // 模型目录是仓库镜像：同名文件要覆盖，不能变成 x (1).bin。
                    NameConflict = NameConflictPolicy.Overwrite,
                }),
            ],
        };
    }

    [Fact]
    public void EnqueueGroup_CreatesParent_AndAggregatesChildren()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var a = EngineHarness.MakeBytes(20_000, seed: 1);
        var b = EngineHarness.MakeBytes(30_000, seed: 2);
        harness.Server.AddFile("/a.bin", a);
        harness.Server.AddFile("/b.bin", b);

        var parentId = manager.EnqueueGroup(NewGroup(harness, "a.bin", "b.bin"));

        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Completed),
            "父行没走到完成");

        var parent = manager.Find(parentId)!;
        Assert.True(parent.IsContainer);
        Assert.Null(parent.ParentId);
        Assert.Equal("owner/name", parent.FileName);
        Assert.Equal(2, parent.ChildCount);
        Assert.Equal(2, parent.CompletedChildren);
        Assert.Equal(a.Length + b.Length, parent.SizeBytes);
        Assert.Equal(a.Length + b.Length, parent.SyncedBytes);
        Assert.NotNull(parent.CompletedAt);

        var all = manager.Snapshot();
        Assert.Equal(3, all.Count);
        var children = all.Where(r => r.ParentId == parentId).ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, child => Assert.False(child.IsContainer));
        Assert.All(children, child => Assert.Equal(DownloadStatus.Completed, child.Status));
        Assert.True(File.Exists(Path.Combine(GroupFolder(harness), "a.bin")));
    }

    [Fact]
    public void Children_ExposesChildRecords_InIdOrder()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/b.bin", EngineHarness.MakeBytes(6_000, seed: 11));
        harness.Server.AddFile("/a.bin", EngineHarness.MakeBytes(7_000, seed: 12));

        var parentId = manager.EnqueueGroup(NewGroup(harness, "a.bin", "b.bin"));
        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Completed));

        var children = manager.Children(parentId);
        Assert.Equal(2, children.Count);
        Assert.Equal(["a.bin", "b.bin"], children.Select(c => c.FileName));
        Assert.All(children, child => Assert.Equal(parentId, child.ParentId));
        Assert.All(children, child => Assert.Equal(DownloadStatus.Completed, child.Status));
        Assert.Equal(13_000, children.Sum(c => c.SizeBytes));

        // id 升序（详情窗口按这个顺序渲染）。
        Assert.Equal(children.Select(c => c.Id).Order(), children.Select(c => c.Id));

        // 非父行 / 未知 id 都是空。
        Assert.Empty(manager.Children(children[0].Id));
        Assert.Empty(manager.Children(999_999));
    }

    [Fact]
    public void MaxFilesPerTask_One_SerializesSiblingFiles()
    {
        using var harness = new EngineHarness();

        // 全局任务槽位放到 4：这里要看的是「同一个包内的兄弟文件」闸门，别被 MaxConcurrent 挡住。
        using var manager = CreateManager(harness, maxFilesPerTask: 1, maxConcurrent: 4);
        var files = AddSlowFiles(harness, count: 4);

        var parentId = manager.EnqueueGroup(NewGroup(harness, files));
        var peak = SamplePeakRunningChildren(manager, parentId, out var completed);

        Assert.True(completed, "父行没走到完成");
        Assert.Equal(1, peak);
        Assert.All(manager.Children(parentId), child => Assert.Equal(DownloadStatus.Completed, child.Status));
    }

    [Fact]
    public void MaxFilesPerTask_Two_RunsSiblingFilesInPairs()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness, maxFilesPerTask: 2, maxConcurrent: 4);
        var files = AddSlowFiles(harness, count: 5);

        var parentId = manager.EnqueueGroup(NewGroup(harness, files));
        var peak = SamplePeakRunningChildren(manager, parentId, out var completed);

        Assert.True(completed, "父行没走到完成");

        // 既要爬到 2（闸门真在按容量放行），又不能到 3。
        Assert.Equal(2, peak);
        Assert.Equal(5, manager.Children(parentId).Count);
    }

    [Fact]
    public void MaxFilesPerTask_DoesNotThrottleIndependentTasks()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness, maxFilesPerTask: 1, maxConcurrent: 4);
        var slow = new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(15) };
        harness.Server.AddFile("/x.bin", EngineHarness.MakeBytes(32 * 1024, seed: 41), slow);
        harness.Server.AddFile("/y.bin", EngineHarness.MakeBytes(32 * 1024, seed: 42), slow);

        // 两个独立任务（没有父行）各自占一个槽位，同时跑——闸门只对同一父行的兄弟生效。
        var x = manager.Enqueue(new DownloadRequest
        {
            Url = harness.Server.BaseUrl + "/x.bin",
            TargetFolder = harness.TargetFolder,
            FileName = "x.bin",
            MaxSegments = 2,
        });
        var y = manager.Enqueue(new DownloadRequest
        {
            Url = harness.Server.BaseUrl + "/y.bin",
            TargetFolder = harness.TargetFolder,
            FileName = "y.bin",
            MaxSegments = 2,
        });

        var deadline = Environment.TickCount64 + 10_000;
        var sawBoth = false;
        while (Environment.TickCount64 < deadline)
        {
            var running = manager.Snapshot().Count(
                r => (r.Id == x || r.Id == y)
                     && r.Status is DownloadStatus.Connecting or DownloadStatus.Downloading);
            if (running == 2)
            {
                sawBoth = true;
                break;
            }

            if (manager.Find(x)?.Status == DownloadStatus.Completed
                && manager.Find(y)?.Status == DownloadStatus.Completed)
            {
                break;
            }

            Thread.Sleep(2);
        }

        Assert.True(
            harness.WaitFor(
                () => manager.Find(x)?.Status == DownloadStatus.Completed
                      && manager.Find(y)?.Status == DownloadStatus.Completed),
            "两个独立任务没跑完");
        Assert.True(sawBoth, "两个独立任务没同时跑过，闸门误伤了独立任务");
    }

    /// <summary>4 个 32 KB 的慢速文件：单文件约 60ms，够采样器看到并发状态。</summary>
    private static string[] AddSlowFiles(EngineHarness harness, int count)
    {
        var slow = new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(15) };
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = $"f{i}.bin";
            harness.Server.AddFile($"/f{i}.bin", EngineHarness.MakeBytes(32 * 1024, seed: 100 + i), slow);
        }

        return names;
    }

    /// <summary>边等父行完成边采样子任务并发峰值（2ms 一次）。</summary>
    private static int SamplePeakRunningChildren(
        DownloadManager manager,
        long parentId,
        out bool completed,
        int timeoutMs = 15_000)
    {
        var peak = 0;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var running = manager.Snapshot().Count(
                r => r.ParentId == parentId && r.Status is DownloadStatus.Connecting or DownloadStatus.Downloading);
            peak = Math.Max(peak, running);

            if (manager.Find(parentId)?.Status == DownloadStatus.Completed)
            {
                completed = true;
                return peak;
            }

            Thread.Sleep(2);
        }

        completed = false;
        return peak;
    }

    [Fact]
    public void Segments_IsLive_WhileTaskRuns()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var bytes = EngineHarness.MakeBytes(900_000, seed: 13);
        harness.Server.AddFile(
            "/live.bin",
            bytes,
            new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(12) });

        var id = manager.Enqueue(new DownloadRequest
        {
            Url = harness.Server.BaseUrl + "/live.bin",
            TargetFolder = harness.TargetFolder,
            FileName = "live.bin",
            MaxSegments = 3,
        });

        Assert.True(harness.WaitFor(() => manager.Segments(id).Count == 3), "没读到 3 个段");
        Assert.True(
            harness.WaitFor(() => manager.Segments(id).Sum(s => s.Synced) > 0),
            "段快照没跟着涨（缓存的 entry.Segments 只在建段/暂停时刷新）");

        var segments = manager.Segments(id);
        Assert.All(segments, segment => Assert.InRange(segment.Offset, 0, bytes.Length));
        Assert.Equal(bytes.Length, segments.Sum(s => s.Length));

        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));
        Assert.Equal(bytes.Length, manager.Segments(id).Sum(s => s.Synced));

        // 未知 id 不抛。
        Assert.Empty(manager.Segments(999_999));
    }

    [Fact]
    public void ParentShowsDownloading_WhileAnyChildRuns()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var big = EngineHarness.MakeBytes(1_200_000, seed: 3);
        harness.Server.AddFile(
            "/slow.bin",
            big,
            new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(12) });

        var parentId = manager.EnqueueGroup(NewGroup(harness, "slow.bin"));

        Assert.True(
            harness.WaitFor(() =>
            {
                var parent = manager.Find(parentId);
                return parent is { Status: DownloadStatus.Downloading, SyncedBytes: > 0 };
            }),
            "父行没有跟着子任务变成下载中/涨进度");

        Assert.Equal(0, manager.Find(parentId)!.CompletedChildren);
        Assert.True(manager.Find(parentId)!.SpeedBps >= 0);
    }

    [Fact]
    public void PauseOnParent_PausesEveryChild_AndResumeFinishes()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var bytes = EngineHarness.MakeBytes(600_000, seed: 4);
        var slow = new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(20) };
        harness.Server.AddFile("/a.bin", bytes, slow);
        harness.Server.AddFile("/b.bin", bytes, slow);

        var parentId = manager.EnqueueGroup(NewGroup(harness, "a.bin", "b.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Downloading));

        Assert.True(manager.Pause(parentId));
        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Paused),
            "父行没跟着子任务变成暂停");

        var children = manager.Snapshot().Where(r => r.ParentId == parentId).ToArray();
        Assert.All(children, child => Assert.Equal(DownloadStatus.Paused, child.Status));

        Assert.True(manager.Resume(parentId));
        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Completed),
            "恢复后父行没完成");
        Assert.All(
            manager.Snapshot().Where(r => r.ParentId == parentId),
            child => Assert.Equal(DownloadStatus.Completed, child.Status));
    }

    [Fact]
    public void DeleteOnParent_RemovesChildrenRowsAndFiles()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        using var manager = CreateManager(harness, store);
        harness.Server.AddFile("/a.bin", EngineHarness.MakeBytes(8_000, seed: 5));

        var parentId = manager.EnqueueGroup(NewGroup(harness, "a.bin"));
        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Completed));
        Assert.Equal(2, manager.Snapshot().Count);

        Assert.True(manager.Delete(parentId, deleteFile: true));

        Assert.Empty(manager.Snapshot());
        Assert.Equal(0, store.CountTasks());
        Assert.False(File.Exists(Path.Combine(GroupFolder(harness), "a.bin")));
    }

    [Fact]
    public void ParentFails_WhenAChildFails()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/ok.bin", EngineHarness.MakeBytes(4_000, seed: 6));

        // /missing.bin 没登记 → 404
        var parentId = manager.EnqueueGroup(NewGroup(harness, "ok.bin", "missing.bin"));

        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Failed),
            "子任务失败没有汇总到父行");

        var parent = manager.Find(parentId)!;
        Assert.Equal(1, parent.CompletedChildren);
        Assert.Equal(2, parent.ChildCount);
        Assert.False(string.IsNullOrWhiteSpace(parent.ErrorText));
    }

    [Fact]
    public void ParentAndChildren_ArePersisted_AndRestored()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        harness.Server.AddFile("/a.bin", EngineHarness.MakeBytes(12_000, seed: 7));

        long parentId;
        using (var manager = CreateManager(harness, store))
        {
            parentId = manager.EnqueueGroup(NewGroup(harness, "a.bin"));
            Assert.True(harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Completed));
        }

        var rows = store.LoadAll();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows.Single(r => r.Id == parentId).ParentId);
        Assert.Equal(parentId, rows.Single(r => r.Id != parentId).ParentId);

        using var restarted = CreateManager(harness, store);
        var snapshot = restarted.Snapshot();
        Assert.Equal(2, snapshot.Count);

        var parent = snapshot.Single(r => r.Id == parentId);
        Assert.True(parent.IsContainer);
        Assert.Equal(1, parent.ChildCount);
        Assert.Equal(1, parent.CompletedChildren);
        Assert.Equal(DownloadStatus.Completed, parent.Status);
        Assert.Equal(DownloadStatus.Completed, snapshot.Single(r => r.Id != parentId).Status);
    }
}
