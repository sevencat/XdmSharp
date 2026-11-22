using Xdm.Core.Storage;

namespace Xdm.Core.Configuration;

/// <summary>
/// 把 <see cref="AppSettings"/> 落到 <c>settings.json</c>：复用 <see cref="AtomicJson"/> 的
/// 「写 tmp → fsync → 改名」，断电时不会留下半个 JSON。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>设置文件名（与库同目录）。</summary>
    public const string FileName = "settings.json";

    public JsonSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public AppSettings Load()
    {
        var loaded = AtomicJson.Read<AppSettings>(Path);
        return (loaded ?? new AppSettings()).Normalized();
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicJson.Write(Path, settings.Normalized());
    }
}
