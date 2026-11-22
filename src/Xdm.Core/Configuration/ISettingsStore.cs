namespace Xdm.Core.Configuration;

/// <summary>用户设置的读写抽象（生产用任务库的 <c>settings</c> 表，测试可用内存实现）。</summary>
public interface ISettingsStore
{
    /// <summary>设置存放位置（状态栏/设置页展示用；入库实现返回库路径）。</summary>
    string Path { get; }

    /// <summary>读取设置；文件缺失/损坏时返回默认值，不抛异常。</summary>
    AppSettings Load();

    /// <summary>原子写入设置。</summary>
    void Save(AppSettings settings);
}
