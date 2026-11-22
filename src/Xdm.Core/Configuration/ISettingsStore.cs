namespace Xdm.Core.Configuration;

/// <summary>用户设置的读写抽象（测试用内存实现，生产用 JSON 文件）。</summary>
public interface ISettingsStore
{
    /// <summary>设置文件路径（状态栏/设置页展示用）。</summary>
    string Path { get; }

    /// <summary>读取设置；文件缺失/损坏时返回默认值，不抛异常。</summary>
    AppSettings Load();

    /// <summary>原子写入设置。</summary>
    void Save(AppSettings settings);
}
