using Xdm.Core.Shell;

namespace Xdm.Core.Tests.Shell;

/// <summary>
/// 单实例闸门与管道转发。每个用例用独立的库路径，键因此互不相同，不会和并行跑的其它用例抢同一个 Mutex。
/// </summary>
public class SingleInstanceHostTests
{
    private static string UniqueDb() =>
        Path.Combine(Path.GetTempPath(), $"xdm-si-{Guid.NewGuid():N}.db");

    [Fact]
    public void Second_Acquire_Returns_Null_While_First_Held()
    {
        var db = UniqueDb();
        using var host = SingleInstanceHost.TryAcquire(db, _ => { });

        Assert.NotNull(host);
        Assert.Null(SingleInstanceHost.TryAcquire(db, _ => { }));
    }

    [Fact]
    public void Acquire_Succeeds_Again_After_Dispose()
    {
        var db = UniqueDb();
        var host = SingleInstanceHost.TryAcquire(db, _ => { });
        Assert.NotNull(host);

        host.Dispose();

        using var again = SingleInstanceHost.TryAcquire(db, _ => { });
        Assert.NotNull(again);
    }

    [Fact]
    public void Different_Db_Paths_Are_Independent()
    {
        using var first = SingleInstanceHost.TryAcquire(UniqueDb(), _ => { });
        using var second = SingleInstanceHost.TryAcquire(UniqueDb(), _ => { });

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void TrySend_Delivers_Url_To_Primary()
    {
        var db = UniqueDb();
        using var received = new SemaphoreSlim(0);
        string? payload = "not-delivered";

        using var host = SingleInstanceHost.TryAcquire(db, url =>
        {
            payload = url;
            received.Release();
        });

        Assert.NotNull(host);
        Assert.True(SingleInstanceHost.TrySend(db, "https://example.com/a.bin", 5000));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "主实例没有收到转发");
        Assert.Equal("https://example.com/a.bin", payload);
    }

    [Fact]
    public void TrySend_Without_Url_Means_Activate_Only()
    {
        var db = UniqueDb();
        using var received = new SemaphoreSlim(0);
        string? payload = "not-delivered";

        using var host = SingleInstanceHost.TryAcquire(db, url =>
        {
            payload = url;
            received.Release();
        });

        Assert.NotNull(host);
        Assert.True(SingleInstanceHost.TrySend(db, url: null, timeoutMs: 5000));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "主实例没有收到激活请求");
        Assert.Null(payload);
    }

    [Fact]
    public void TrySend_Returns_False_Without_Primary()
    {
        Assert.False(SingleInstanceHost.TrySend(UniqueDb(), "https://example.com/a.bin", timeoutMs: 500));
    }

    [Fact]
    public void Names_Are_Stable_And_Differ_By_Path()
    {
        var db = Path.Combine(Path.GetTempPath(), "xdm-si-names.db");
        var other = Path.Combine(Path.GetTempPath(), "xdm-si-names-2.db");

        // 大小写不影响键：Windows 路径不区分大小写。
        Assert.Equal(SingleInstanceHost.KeyOf(db), SingleInstanceHost.KeyOf(db.ToUpperInvariant()));
        Assert.NotEqual(SingleInstanceHost.KeyOf(db), SingleInstanceHost.KeyOf(other));
        Assert.StartsWith(@"Local\xdm-sharp-", SingleInstanceHost.MutexNameFor(db), StringComparison.Ordinal);
        Assert.StartsWith("xdm-sharp-", SingleInstanceHost.PipeNameFor(db), StringComparison.Ordinal);
    }
}
