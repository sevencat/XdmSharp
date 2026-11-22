using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Xdm.Core.Storage;

/// <summary>
/// 原子写 JSON：写 <c>.tmp</c> → fsync → <see cref="File.Move(string,string,bool)"/> 覆盖。
/// 断电时要么是旧文件、要么是新文件，不会出现半个 JSON（<c>ARCHITECTURE.md</c> §7.2）。
/// </summary>
/// <remarks>
/// 序列化一律走调用方传入的 <see cref="JsonTypeInfo{T}"/>（source-generated，见
/// <see cref="XdmJsonContext"/>）：NativeAOT 下泛型 <c>JsonSerializer.Serialize&lt;T&gt;</c>
/// 会被判为 <c>RequiresDynamicCode</c>/<c>RequiresUnreferencedCode</c>，且发布包里不带属性元数据。
/// </remarks>
public static class AtomicJson
{
    /// <summary>原子写。<paramref name="path"/> 的父目录不存在时自动创建。</summary>
    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>读 JSON；文件不存在或内容损坏时返回 <c>null</c>（调用方按"没有快照"处理）。</summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>删除文件；不存在时静默返回。</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 文件被占用时留到下次启动清理，不影响下载本身。
        }
    }
}
