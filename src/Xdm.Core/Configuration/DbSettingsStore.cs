using System.Text.Json;
using Xdm.Core.Storage;
using Xdm.Core.Tasks;

namespace Xdm.Core.Configuration;

/// <summary>
/// 把 <see cref="AppSettings"/> 存在任务库的 <c>settings</c> 表里（键 <see cref="TaskStore.SettingsKey"/>，
/// 整份配置一个 JSON 值）。设置与任务同库：备份/搬家只要复制一个 <c>.db</c>，也不会出现
/// 「任务在库里、设置还在 json」的两套状态。
/// </summary>
/// <remarks>
/// 序列化走 source-generated <see cref="XdmJsonContext"/>，NativeAOT 下不需要反射元数据。
/// 库里没有行时按默认值启动；行内容损坏（手改库/旧版本结构）同样退回默认值，不阻塞启动。
/// </remarks>
public sealed class DbSettingsStore : ISettingsStore
{
    private readonly TaskStore _store;

    public DbSettingsStore(TaskStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public string Path => _store.DbPath;

    /// <inheritdoc />
    public AppSettings Load()
    {
        var json = _store.LoadSettingsJson();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(json, XdmJsonContext.Default.AppSettings);
            return (loaded ?? new AppSettings()).Normalized();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings.Normalized(), XdmJsonContext.Default.AppSettings);
        _store.SaveSettingsJson(json);
    }

    /// <summary>
    /// 一次性迁移旧版 <c>settings.json</c>：只在库里还没有配置时导入，导入成功后删掉 json 文件。
    /// 返回是否发生了迁移。老文件损坏/不可读时原样保留，交还用户处理。
    /// </summary>
    public bool ImportLegacyJson(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath) || _store.LoadSettingsJson() is not null)
        {
            return false;
        }

        try
        {
            var legacy = AtomicJson.Read(jsonPath, XdmJsonContext.Default.AppSettings);
            if (legacy is null)
            {
                return false;
            }

            Save(legacy);
            AtomicJson.Delete(jsonPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不动就保持原状，下次启动再试。
            return false;
        }
    }
}
