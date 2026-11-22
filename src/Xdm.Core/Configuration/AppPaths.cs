namespace Xdm.Core.Configuration;

/// <summary>
/// 运行期数据的路径解析。任务库与 <c>state/</c> 永属同一目录：
/// <c>--db D:\x\my.db</c> → 库 <c>D:\x\my.db</c> + 快照 <c>D:\x\state\</c>。
/// 优先级：<c>--db</c> &gt; <c>XDM_DB</c> &gt; <c>XDM_DATA_DIR</c> &gt; 程序目录 &gt; LocalAppData（仅当程序目录不可写）。
/// </summary>
public sealed class AppPaths
{
    public const string DbFileName = "xdm.db";

    /// <summary>旧版设置文件名（0.1.0 之前配置写 json，现在只用于迁移）。</summary>
    public const string LegacySettingsFileName = "settings.json";
    public const string DbArgument = "--db";
    public const string EnvDb = "XDM_DB";
    public const string EnvDataDir = "XDM_DATA_DIR";

    private AppPaths(string dbPath, bool isFallback)
    {
        DbPath = dbPath;
        DataDir = Path.GetDirectoryName(dbPath)
                  ?? throw new ArgumentException($"无法从路径推导目录：{dbPath}", nameof(dbPath));
        IsFallback = isFallback;
    }

    /// <summary>任务库的绝对路径。</summary>
    public string DbPath { get; }

    /// <summary>库文件所在目录，也是 <see cref="StateDir"/> 的父目录。</summary>
    public string DataDir { get; }

    /// <summary>分段进度快照目录（与库同级）。</summary>
    public string StateDir => Path.Combine(DataDir, "state");

    /// <summary>旧版设置文件（与库同级）。配置现已存进库的 <c>settings</c> 表，这个路径只用于启动时一次性迁移。</summary>
    public string SettingsPath => Path.Combine(DataDir, LegacySettingsFileName);

    /// <summary>是否因程序目录不可写而回退到 LocalAppData。</summary>
    public bool IsFallback { get; }

    /// <summary>直接用库路径构造（测试与已解析场景）。</summary>
    public static AppPaths FromDbPath(string dbPath, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var full = Path.GetFullPath(
            Path.IsPathFullyQualified(dbPath) ? dbPath : Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), dbPath));

        if (Directory.Exists(full))
        {
            throw new ArgumentException($"--db 指向的是目录而不是文件：{full}", nameof(dbPath));
        }

        return new AppPaths(full, isFallback: false);
    }

    /// <summary>按优先级解析命令行与环境变量。</summary>
    public static AppPaths Resolve(
        IReadOnlyList<string> args,
        string? baseDirectory = null,
        string? workingDirectory = null,
        Func<string, string?>? getEnvironment = null,
        Func<string, bool>? canWriteDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        getEnvironment ??= Environment.GetEnvironmentVariable;
        canWriteDirectory ??= CanWriteDirectory;

        var explicitDb = ReadDbArgument(args, workingDirectory);
        if (explicitDb is not null)
        {
            return FromDbPath(explicitDb, workingDirectory);
        }

        var envDb = getEnvironment(EnvDb);
        if (!string.IsNullOrWhiteSpace(envDb))
        {
            return FromDbPath(envDb, workingDirectory);
        }

        var dataDir = getEnvironment(EnvDataDir);
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            var dir = Path.GetFullPath(
                Path.IsPathFullyQualified(dataDir)
                    ? dataDir
                    : Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), dataDir));
            return new AppPaths(Path.Combine(dir, DbFileName), isFallback: false);
        }

        var appDir = baseDirectory ?? AppContext.BaseDirectory;
        var appDb = Path.GetFullPath(Path.Combine(appDir, DbFileName));
        if (canWriteDirectory(Path.GetDirectoryName(appDb)!))
        {
            return new AppPaths(appDb, isFallback: false);
        }

        var localAppData = getEnvironment("LOCALAPPDATA")
                           ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            // 兜底：程序目录不可写且没有 LOCALAPPDATA 时，仍然返回程序目录（随后建库会明确报错）
            return new AppPaths(appDb, isFallback: true);
        }

        var fallbackDb = Path.Combine(localAppData, "Xdm", DbFileName);
        return new AppPaths(Path.GetFullPath(fallbackDb), isFallback: true);
    }

    private static string? ReadDbArgument(IReadOnlyList<string> args, string? workingDirectory)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.Equals(DbArgument, StringComparison.OrdinalIgnoreCase))
            {
                var value = i + 1 < args.Count ? args[i + 1] : null;
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("--db 缺少路径参数，用法：--db <path>");
                }

                return value;
            }

            var prefix = DbArgument + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[prefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("--db 缺少路径参数，用法：--db=<path>");
                }

                return value;
            }
        }

        return null;
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
