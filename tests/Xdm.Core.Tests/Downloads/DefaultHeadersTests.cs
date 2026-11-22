using Xdm.Core.Downloads;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public sealed class DefaultHeadersTests
{
    [Fact]
    public void DefaultHeaders_AreMerged_TaskOverridesGlobal()
    {
        using var harness = new EngineHarness();
        harness.Server.AddFile("/a.bin", EngineHarness.MakeBytes(120_000));

        using var manager = new DownloadManager(new DownloadManagerOptions
        {
            StateDir = harness.StateDir,
            Client = harness.Client,
            EngineOptions = EngineHarness.FastOptions,
        })
        {
            DefaultHeaders = new Dictionary<string, string>
            {
                ["X-Global"] = "1",
                ["User-Agent"] = "global-agent",
            },
        };

        var id = manager.Enqueue(new DownloadRequest
        {
            Url = harness.Server.BaseUrl + "/a.bin",
            TargetFolder = harness.TargetFolder,
            FileName = "a.bin",
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "task-agent",
                ["X-Task"] = "2",
            },
        });

        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));

        var request = harness.Server.Requests.First(static r => r.Path == "/a.bin");
        Assert.Equal("1", request.Headers["X-Global"]);
        Assert.Equal("2", request.Headers["X-Task"]);
        Assert.Equal("task-agent", request.Headers["User-Agent"]);
    }
}
