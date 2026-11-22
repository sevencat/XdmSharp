using Xdm.Ui.Mew.Dialogs;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 新建下载对话框：设置里的默认目录/分段数必须落到控件与 ViewModel 上（PLAN-MEWUI.md 坑 22）。
/// 点「添加」的逻辑抽成了 <see cref="NewDownloadDialog.Submit"/>，测试不必模拟鼠标。
/// </summary>
public class NewDownloadDialogTests
{
    [Fact]
    public void Applies_Initial_Url()
    {
        var dialog = new NewDownloadDialog("https://example.com/a.bin");

        Assert.Equal("https://example.com/a.bin", dialog.ViewModel.Urls);
    }

    [Fact]
    public void Applies_SaveFolder_From_Settings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xdm-settings-folder");
        var dialog = new NewDownloadDialog(null, folder, null);

        Assert.Equal(folder, dialog.ViewModel.SaveFolder);
        Assert.True(VisualTree.ContainsText(dialog.Content, folder));
    }

    [Fact]
    public void Applies_Segments_From_Settings()
    {
        Assert.Equal(6m, new NewDownloadDialog(null, null, 6).ViewModel.Segments);
    }

    [Fact]
    public void Clamps_Out_Of_Range_Segments()
    {
        // 设置页允许手填，落进对话框时统一夹到 [1, 256]。
        Assert.Equal(1m, new NewDownloadDialog(null, null, 0).ViewModel.Segments);
        Assert.Equal(256m, new NewDownloadDialog(null, null, 9999).ViewModel.Segments);
    }

    [Fact]
    public void Submit_Builds_Request_From_Url_And_Folder()
    {
        var dialog = new NewDownloadDialog("https://example.com/a.bin", Path.GetTempPath(), 6);

        Assert.True(dialog.Submit());
        var request = Assert.Single(dialog.Requests!);

        Assert.Equal("https://example.com/a.bin", request.Url);
        Assert.Equal("a.bin", request.FileName);
        Assert.Equal(Path.GetTempPath(), request.TargetFolder);
        Assert.Equal(6, request.MaxSegments);
    }

    [Fact]
    public void Submit_Creates_One_Request_Per_Line()
    {
        var dialog = new NewDownloadDialog(null, Path.GetTempPath());
        dialog.ViewModel.Urls = "https://example.com/a.bin\nhttps://example.com/b.bin";

        Assert.True(dialog.Submit());
        Assert.Equal(2, dialog.Requests!.Count);
    }

    [Fact]
    public void Submit_Rejects_Invalid_Url_And_Shows_Error()
    {
        var dialog = new NewDownloadDialog("not a url", Path.GetTempPath());

        Assert.False(dialog.Submit());
        Assert.Null(dialog.Requests);
        Assert.True(dialog.ViewModel.HasError);
        Assert.True(VisualTree.ContainsText(dialog.Content, dialog.ViewModel.ErrorText));
    }

    [Fact]
    public void Submit_Rejects_Missing_SaveFolder()
    {
        var missing = Path.Combine(Path.GetTempPath(), "xdm-missing-" + Guid.NewGuid().ToString("N"));
        var dialog = new NewDownloadDialog("https://example.com/a.bin", missing);

        Assert.False(dialog.Submit());
        Assert.Contains("保存目录不存在", dialog.ViewModel.ErrorText, StringComparison.Ordinal);
    }
}
