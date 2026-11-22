using Xdm.Core.Downloads;
using Xdm.Core.HuggingFace;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.HuggingFace;

/// <summary>
/// 落盘位置端到端：清单 → 规划 → 入队 → 下载完成，文件必须落在
/// <c>{下载目录}/{owner}/{模型名}/&lt;仓库内相对路径&gt;</c>（对话框里选的「保存到」就是 <c>{下载目录}</c>）。
/// </summary>
public sealed class HfGroupDownloadTests
{
    private static DownloadManager CreateManager(EngineHarness harness) => new(new DownloadManagerOptions
    {
        MaxConcurrent = 2,
        MaxFilesPerTask = 3,
        StateDir = harness.StateDir,
        Client = harness.Client,
        EngineOptions = EngineHarness.FastOptions,
    });

    [Fact]
    public void DownloadedFiles_LandUnderSaveFolderOwnerName()
    {
        using var harness = new EngineHarness();
        var saveRoot = Path.Combine(harness.Root, "hf-save");
        Directory.CreateDirectory(saveRoot);

        var config = EngineHarness.MakeBytes(1_500, seed: 3);
        var weights = EngineHarness.MakeBytes(2_500, seed: 4);
        harness.Server.AddFile("/owner/name/resolve/main/config.json", config);
        harness.Server.AddFile("/owner/name/resolve/main/onnx/model.onnx", weights);

        var repo = new HfRepo { Endpoint = harness.Server.BaseUrl, Repo = "owner/name" };
        HfFile[] files =
        [
            new() { Path = "config.json", Size = config.Length },
            new() { Path = "onnx/model.onnx", Size = weights.Length },
        ];

        var plan = HfDownloadPlanner.Plan(repo, files, saveRoot, skipExisting: false);
        var group = HfDownloadPlanner.ToGroup(repo, plan, 2, null, null);

        using var manager = CreateManager(harness);
        var parentId = manager.EnqueueGroup(group);

        Assert.True(
            harness.WaitFor(() => manager.Find(parentId)?.Status == DownloadStatus.Completed),
            "父行没走到完成");

        var modelFolder = Path.Combine(saveRoot, "owner", "name");
        Assert.Equal(modelFolder, plan.RootFolder);
        Assert.Equal(modelFolder, group.TargetFolder);

        var configPath = Path.Combine(modelFolder, "config.json");
        var weightsPath = Path.Combine(modelFolder, "onnx", "model.onnx");
        Assert.True(File.Exists(configPath), $"没落在 {configPath}");
        Assert.True(File.Exists(weightsPath), $"没落在 {weightsPath}");
        Assert.Equal(config, File.ReadAllBytes(configPath));
        Assert.Equal(weights, File.ReadAllBytes(weightsPath));

        // 子任务记录里的目标目录同样是模型子目录（「打开目录」不会开到上一级）。
        var children = manager.Snapshot().Where(row => row.ParentId == parentId).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Contains(children, row => row.TargetFolder == modelFolder);
        Assert.Contains(children, row => row.TargetFolder == Path.Combine(modelFolder, "onnx"));
    }

    [Fact]
    public void SecondRun_SkipsAlreadyCompleteModelFolder()
    {
        using var harness = new EngineHarness();
        var saveRoot = Path.Combine(harness.Root, "hf-save");
        Directory.CreateDirectory(saveRoot);

        var config = EngineHarness.MakeBytes(900, seed: 5);
        harness.Server.AddFile("/owner/name/resolve/main/config.json", config);

        var repo = new HfRepo { Endpoint = harness.Server.BaseUrl, Repo = "owner/name" };
        HfFile[] files = [new() { Path = "config.json", Size = config.Length }];

        // 模拟上一次下载已完成：模型子目录里已经有同大小文件。
        var modelFolder = HfDownloadPlanner.RootFolder(saveRoot, repo);
        Directory.CreateDirectory(modelFolder);
        File.WriteAllBytes(Path.Combine(modelFolder, "config.json"), config);

        var plan = HfDownloadPlanner.Plan(repo, files, saveRoot);

        Assert.Empty(plan.Files);
        Assert.Equal(1, plan.SkippedCount);
    }
}
