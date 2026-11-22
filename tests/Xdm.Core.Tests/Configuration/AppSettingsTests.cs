using Xdm.Core.Configuration;
using Xdm.Core.Http;
using Xdm.Core.Storage;
using Xdm.Core.Tasks;

namespace Xdm.Core.Tests.Configuration;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "xdm-settings-tests", Guid.NewGuid().ToString("N"));

    public AppSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Normalize_ClampsNumbers()
    {
        var settings = new AppSettings
        {
            MaxConcurrent = 0,
            MaxSegments = 9999,
            SpeedLimitBytesPerSecond = -5,
            MaxRetries = 99,
            RetryBaseDelayMs = 1,
        }.Normalized();

        Assert.Equal(1, settings.MaxConcurrent);
        Assert.Equal(256, settings.MaxSegments);
        Assert.Equal(0, settings.SpeedLimitBytesPerSecond);
        Assert.Equal(10, settings.MaxRetries);
        Assert.Equal(50, settings.RetryBaseDelayMs);
    }

    [Fact]
    public void Normalize_CustomProxyWithBadUrl_FallsBackToSystem()
    {
        var settings = new AppSettings
        {
            ProxyMode = ProxyMode.Custom,
            ProxyUrl = "ftp://127.0.0.1:21",
        }.Normalized();

        Assert.Equal(ProxyMode.System, settings.ProxyMode);
        Assert.Null(settings.ProxyUrl);
    }

    [Fact]
    public void Normalize_KeepsValidCustomProxy()
    {
        var settings = new AppSettings
        {
            ProxyMode = ProxyMode.Custom,
            ProxyUrl = " socks5://127.0.0.1:1080 ",
        }.Normalized();

        Assert.Equal(ProxyMode.Custom, settings.ProxyMode);
        Assert.Equal("socks5://127.0.0.1:1080", settings.ProxyUrl);
    }

    [Fact]
    public void Normalize_DropsBadHeaders()
    {
        var settings = new AppSettings
        {
            DefaultHeaders = new Dictionary<string, string>
            {
                [" Referer "] = " https://example.com ",
                ["Bad Header"] = "x",
                ["X-Crlf"] = "a\r\nInjected: 1",
                ["Empty"] = "",
                [""] = "x",
            },
        }.Normalized();

        var header = Assert.Single(settings.DefaultHeaders);
        Assert.Equal("https://example.com", header.Value);
        Assert.True(settings.DefaultHeaders.ContainsKey("referer"), "大小写不敏感");
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        var original = new AppSettings
        {
            DefaultHeaders = new Dictionary<string, string> { ["X-A"] = "1" },
        };

        var clone = original.Clone();
        clone.DefaultHeaders["X-A"] = "2";
        clone.MaxConcurrent = 9;

        Assert.Equal("1", original.DefaultHeaders["X-A"]);
        Assert.Equal(3, original.MaxConcurrent);
    }

    [Fact]
    public void DbStore_RoundTripsAllFields()
    {
        using var db = new TaskStore(":memory:");
        var store = new DbSettingsStore(db);
        var settings = new AppSettings
        {
            MaxConcurrent = 5,
            MaxSegments = 12,
            SpeedLimitBytesPerSecond = 262_144,
            ProxyMode = ProxyMode.Custom,
            ProxyUrl = "http://127.0.0.1:7890",
            DefaultHeaders = new Dictionary<string, string> { ["Referer"] = "https://example.com" },
            DefaultSaveFolder = Path.Combine(_root, "downloads"),
            NotifyOnComplete = false,
            MaxRetries = 2,
            RetryBaseDelayMs = 800,
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(5, loaded.MaxConcurrent);
        Assert.Equal(12, loaded.MaxSegments);
        Assert.Equal(262_144, loaded.SpeedLimitBytesPerSecond);
        Assert.Equal(ProxyMode.Custom, loaded.ProxyMode);
        Assert.Equal("http://127.0.0.1:7890", loaded.ProxyUrl);
        Assert.Equal("https://example.com", loaded.DefaultHeaders["Referer"]);
        Assert.Equal(settings.DefaultSaveFolder, loaded.DefaultSaveFolder);
        Assert.False(loaded.NotifyOnComplete);
        Assert.Equal(2, loaded.MaxRetries);
        Assert.Equal(800, loaded.RetryBaseDelayMs);
        Assert.Equal(db.DbPath, store.Path);
    }

    [Fact]
    public void DbStore_SaveReplacesPreviousValue()
    {
        using var db = new TaskStore(":memory:");
        var store = new DbSettingsStore(db);

        store.Save(new AppSettings());
        store.Save(new AppSettings { MaxConcurrent = 4 });

        Assert.Equal(4, store.Load().MaxConcurrent);
    }

    [Fact]
    public void DbStore_EmptyOrCorruptRow_ReturnsDefaults()
    {
        using var db = new TaskStore(":memory:");
        var store = new DbSettingsStore(db);

        // 从没存过
        Assert.Equal(3, store.Load().MaxConcurrent);

        // 手改坏了（或旧版本写入的结构）：退回默认值，不抛
        db.SaveSettingsJson("{ not json");
        var fallback = store.Load();
        Assert.Equal(3, fallback.MaxConcurrent);
        Assert.Equal(ProxyMode.System, fallback.ProxyMode);
    }

    [Fact]
    public void DbStore_ImportLegacyJson_ImportsOnceAndDeletesFile()
    {
        using var db = new TaskStore(":memory:");
        var store = new DbSettingsStore(db);
        var legacy = Path.Combine(_root, AppPaths.LegacySettingsFileName);
        AtomicJson.Write(legacy, new AppSettings { MaxConcurrent = 7 }, XdmJsonContext.Default.AppSettings);

        Assert.True(store.ImportLegacyJson(legacy));
        Assert.False(File.Exists(legacy));
        Assert.Equal(7, store.Load().MaxConcurrent);

        // 库里已有配置，不再迁第二次
        Assert.False(store.ImportLegacyJson(legacy));
    }

    [Fact]
    public void AppPaths_LegacySettingsPath_SitsNextToDb()
    {
        var db = Path.Combine(_root, "xdm.db");
        var paths = AppPaths.FromDbPath(db);

        Assert.Equal(Path.Combine(_root, "settings.json"), paths.SettingsPath);
    }
}
