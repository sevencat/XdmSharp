using Xdm.Core.HuggingFace;
using Xdm.Ui.Mew.Dialogs;

namespace Xdm.Ui.Mew.Tests;

public sealed class HfModelDetailsWindowTests
{
    [Fact]
    public void Shows_ModelId_Counts_And_Summary()
    {
        var window = new HfModelDetailsWindow(new HfModelDetails
        {
            Id = "Qwen/Qwen2.5-7B-Instruct",
            FileCount = 32,
            TotalBytes = 13_000_000_000,
            PipelineTag = "text-generation",
            Downloads = 1_234,
            Likes = 56,
            Summary = "Qwen2.5 是 Qwen 最新一代大语言模型。",
        });

        Assert.Equal("Qwen/Qwen2.5-7B-Instruct", window.Details.Id);
        Assert.True(VisualTree.ContainsText(window.Content, "Qwen/Qwen2.5-7B-Instruct"));
        Assert.True(VisualTree.ContainsText(window.Content, "32 个文件"));
        Assert.True(VisualTree.ContainsText(window.Content, "text-generation"));
        Assert.True(VisualTree.ContainsText(window.Content, "1,234 次下载"));
        Assert.True(VisualTree.ContainsText(window.Content, "56 赞"));
        Assert.True(VisualTree.ContainsText(window.Content, "Qwen2.5 是 Qwen 最新一代大语言模型。"));
    }

    [Fact]
    public void EmptyRepo_ShowsHint_InsteadOfBlankSummary()
    {
        var window = new HfModelDetailsWindow(new HfModelDetails
        {
            Id = "owner/empty",
            FileCount = 0,
            TotalBytes = 0,
        });

        Assert.True(VisualTree.ContainsText(window.Content, "没有文件"));
        Assert.True(VisualTree.ContainsText(window.Content, "没有写 README"));
    }
}
