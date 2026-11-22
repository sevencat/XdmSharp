using Xdm.Core.Configuration;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 设置页「多代理」编辑 → 设置对象 的映射：默认代理下标、id 稳定、非法地址拦在保存前。
/// </summary>
public sealed class ProxySettingsViewModelTests
{
    private static SettingsViewModel NewViewModel(AppSettings? settings = null) =>
        new(settings ?? new AppSettings { DefaultSaveFolder = Path.GetTempPath() }, "x.db");

    [Fact]
    public void TryBuild_SavesProxyListAndDefaultIndex()
    {
        var vm = NewViewModel();
        vm.Proxies.Add(new ProxyEditRow { Name = "公司", Url = "http://127.0.0.1:7890" });
        vm.Proxies.Add(new ProxyEditRow { Name = "本地", Url = "socks5://127.0.0.1:1080" });
        vm.DefaultProxyIndex = 3; // 0 系统、1 直连、2 第一条、3 第二条

        Assert.True(vm.TryBuild(out var settings), vm.ErrorText);

        Assert.Equal(2, settings.Proxies.Count);
        Assert.Equal("公司", settings.Proxies[0].Name);
        Assert.Equal("socks5://127.0.0.1:1080", settings.Proxies[1].Url);
        Assert.Equal(settings.Proxies[1].Id, settings.DefaultProxyId);

        // 旧版单代理字段保持清空，避免下次启动又迁一遍。
        Assert.Equal(default, settings.ProxyMode);
        Assert.Null(settings.ProxyUrl);
    }

    [Fact]
    public void TryBuild_KeepsExistingProxyIds()
    {
        var settings = new AppSettings
        {
            DefaultSaveFolder = Path.GetTempPath(),
            Proxies = [new ProxyProfile { Id = "corp", Name = "公司", Url = "http://127.0.0.1:7890" }],
            DefaultProxyId = "corp",
        };

        var vm = NewViewModel(settings);
        vm.Proxies[0].Name = "公司（新）";
        vm.Proxies.Add(new ProxyEditRow { Name = "备用", Url = "http://127.0.0.1:7891" });

        Assert.True(vm.TryBuild(out var updated), vm.ErrorText);

        Assert.Equal("corp", updated.Proxies[0].Id);
        Assert.Equal("公司（新）", updated.Proxies[0].Name);
        Assert.Equal("corp", updated.DefaultProxyId);
        Assert.False(string.IsNullOrWhiteSpace(updated.Proxies[1].Id));
    }

    [Fact]
    public void TryBuild_DirectDefaultIndexMapsToReservedId()
    {
        var vm = NewViewModel();
        vm.DefaultProxyIndex = 1;

        Assert.True(vm.TryBuild(out var settings), vm.ErrorText);

        Assert.Equal(ProxyIds.Direct, settings.DefaultProxyId);
    }

    [Fact]
    public void TryBuild_InvalidProxyUrlBlocksSave()
    {
        var vm = NewViewModel();
        vm.Proxies.Add(new ProxyEditRow { Name = "坏", Url = "127.0.0.1:7890" });

        Assert.False(vm.TryBuild(out _));
        Assert.Contains("第 1 条代理地址无效", vm.ErrorText);
    }

    [Fact]
    public void TryBuild_EmptyProxyRowsAreIgnored()
    {
        var vm = NewViewModel();
        vm.Proxies.Add(new ProxyEditRow());

        Assert.True(vm.TryBuild(out var settings), vm.ErrorText);
        Assert.Empty(settings.Proxies);
        Assert.Null(settings.DefaultProxyId);
    }

    [Fact]
    public void DefaultProxyIndex_ReflectsExistingDefault()
    {
        var settings = new AppSettings
        {
            DefaultSaveFolder = Path.GetTempPath(),
            Proxies =
            [
                new ProxyProfile { Id = "a", Url = "http://127.0.0.1:7890" },
                new ProxyProfile { Id = "b", Url = "http://127.0.0.1:7891" },
            ],
            DefaultProxyId = "b",
        };

        Assert.Equal(3, NewViewModel(settings).DefaultProxyIndex);

        settings.DefaultProxyId = ProxyIds.Direct;
        Assert.Equal(1, NewViewModel(settings).DefaultProxyIndex);
    }
}

/// <summary>
/// 新建任务窗口的代理选项（第一项「默认」= 设置里的默认代理）。
/// </summary>
public sealed class ProxyChoiceTests
{
    [Fact]
    public void Build_StartsWithDefaultThenDirectThenProfiles()
    {
        var settings = new AppSettings
        {
            Proxies =
            [
                new ProxyProfile { Id = "corp", Name = "公司", Url = "http://127.0.0.1:7890" },
            ],
            DefaultProxyId = "corp",
        };

        var choices = ProxyChoice.Build(settings);

        Assert.Equal(3, choices.Count);
        Assert.Null(choices[0].ProxyId);
        Assert.Contains("公司", choices[0].Label);
        Assert.Equal(ProxyIds.Direct, choices[1].ProxyId);
        Assert.Equal("corp", choices[2].ProxyId);
        Assert.Equal("公司", choices[2].Label);
    }

    [Fact]
    public void Build_WithoutProfiles_LabelsDefaultAsSystemProxy()
    {
        var choices = ProxyChoice.Build(new AppSettings());

        Assert.Equal(2, choices.Count);
        Assert.Contains("系统代理", choices[0].Label);
    }

    [Fact]
    public void NewDownload_PassesSelectedProxyToRequest()
    {
        var settings = new AppSettings
        {
            Proxies = [new ProxyProfile { Id = "corp", Name = "公司", Url = "http://127.0.0.1:7890" }],
        };

        var vm = new NewDownloadViewModel
        {
            Urls = "https://example.com/a.bin",
            SaveFolder = Path.GetTempPath(),
            ProxyChoices = ProxyChoice.Build(settings),
        };
        vm.SelectedProxyIndex = 2;

        Assert.True(vm.TryCreate(out var requests), vm.ErrorText);
        Assert.Equal("corp", Assert.Single(requests).ProxyId);
    }

    [Fact]
    public void NewDownload_DefaultChoiceLeavesProxyIdEmpty()
    {
        var vm = new NewDownloadViewModel
        {
            Urls = "https://example.com/a.bin",
            SaveFolder = Path.GetTempPath(),
            ProxyChoices = ProxyChoice.Build(new AppSettings()),
        };

        Assert.True(vm.TryCreate(out var requests), vm.ErrorText);
        Assert.Null(Assert.Single(requests).ProxyId);
    }
}
