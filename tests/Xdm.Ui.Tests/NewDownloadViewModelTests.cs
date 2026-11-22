using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Tests;

public class NewDownloadViewModelTests
{
    private static NewDownloadViewModel CreateVm() => new()
    {
        SaveFolder = Path.GetTempPath(),
    };

    [Fact]
    public void EmptyUrls_IsRejected()
    {
        var vm = CreateVm();

        Assert.False(vm.TryCreate(out _));
        Assert.True(vm.HasError);
    }

    [Fact]
    public void NonHttpScheme_IsRejected()
    {
        var vm = CreateVm();
        vm.Urls = "ftp://example.com/a.bin";

        Assert.False(vm.TryCreate(out _));
        Assert.Contains("http/https", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFolder_IsRejected()
    {
        var vm = CreateVm();
        vm.Urls = "https://example.com/a.bin";
        vm.SaveFolder = Path.Combine(Path.GetTempPath(), "xdm-does-not-exist-" + Guid.NewGuid().ToString("N"));

        Assert.False(vm.TryCreate(out _));
        Assert.Contains("保存目录不存在", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleUrl_UsesExplicitFileNameAndOptions()
    {
        var vm = CreateVm();
        vm.Urls = "https://example.com/path/data.bin";
        vm.FileName = "custom.bin";
        vm.Segments = 16;

        Assert.True(vm.TryCreate(out var requests));

        var request = Assert.Single(requests);
        Assert.Equal("custom.bin", request.FileName);
        Assert.Equal(16, request.MaxSegments);
        Assert.True(request.StartImmediately);
        Assert.Equal(vm.SaveFolder, request.TargetFolder);
        Assert.Equal("https://example.com/path/data.bin", request.Url);
    }

    [Fact]
    public void MultipleUrls_DeriveNamesPerLine()
    {
        var vm = CreateVm();
        vm.Urls = "https://example.com/a.iso\r\nhttps://example.com/dir/b.zip";

        Assert.True(vm.TryCreate(out var requests));

        Assert.Equal(2, requests.Count);
        Assert.Equal(["a.iso", "b.zip"], requests.Select(r => r.FileName));
    }

    [Fact]
    public void NotStartingImmediately_FlagsRequestAsPaused()
    {
        var vm = CreateVm();
        vm.Urls = "https://example.com/a.iso";
        vm.StartImmediately = false;

        Assert.True(vm.TryCreate(out var requests));

        Assert.False(Assert.Single(requests).StartImmediately);
    }

    [Fact]
    public void UrlWithoutFileName_GetsGeneratedName()
    {
        var vm = CreateVm();
        vm.Urls = "https://example.com/";

        Assert.True(vm.TryCreate(out var requests));
        Assert.StartsWith("download-", Assert.Single(requests).FileName, StringComparison.Ordinal);
    }
}
