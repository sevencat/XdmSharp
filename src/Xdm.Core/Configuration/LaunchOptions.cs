namespace Xdm.Core.Configuration;

/// <summary>
/// 启动参数解析结果：任务库路径 + 可选的待下载链接。
/// 命令行带 URL（<c>xdm-app.exe https://…</c>）或第二个实例把 URL 转给主实例时，
/// 主窗口起来后直接弹「新建下载」并预填链接（PLAN.md M6）。
/// </summary>
/// <param name="Paths">任务库与 state 目录的解析结果（见 <see cref="AppPaths"/>）。</param>
/// <param name="Url">命令行里的第一个 HTTP/HTTPS 链接，没有则为 <see langword="null"/>。</param>
public sealed record LaunchOptions(AppPaths Paths, string? Url)
{
    /// <summary>解析命令行：库路径规则完全沿用 <see cref="AppPaths.Resolve"/>，只额外捞一个 URL。</summary>
    public static LaunchOptions Parse(
        IReadOnlyList<string> args,
        string? baseDirectory = null,
        string? workingDirectory = null,
        Func<string, string?>? getEnvironment = null,
        Func<string, bool>? canWriteDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var paths = AppPaths.Resolve(args, baseDirectory, workingDirectory, getEnvironment, canWriteDirectory);
        return new LaunchOptions(paths, FindUrl(args));
    }

    /// <summary>取第一个 HTTP/HTTPS 链接；<c>--db</c> 后面跟的库路径不算链接。</summary>
    public static string? FindUrl(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // --db <path>：值不是链接，跳过；--db=<path> 自己带等号，不消费下一个参数。
                if (arg.Equals(AppPaths.DbArgument, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                {
                    i++;
                }

                continue;
            }

            if (IsHttpUrl(arg))
            {
                return arg;
            }
        }

        return null;
    }

    /// <summary>是否是可直接下载的 HTTP/HTTPS 绝对链接。</summary>
    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
