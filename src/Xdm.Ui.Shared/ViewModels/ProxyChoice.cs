using Xdm.Core.Configuration;

namespace Xdm.Ui.ViewModels;

/// <summary>
/// 新建任务里的一个代理选项。<see cref="ProxyId"/> 为 <see langword="null"/> 表示「用设置的默认代理」。
/// </summary>
public sealed record ProxyChoice(string Label, string? ProxyId)
{
    /// <summary>
    /// 由设置生成选项：默认代理 → 直连 → 每条具名代理。
    /// 「系统代理」不单列：它就是默认代理在没配具名代理时的取值。
    /// </summary>
    public static List<ProxyChoice> Build(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var defaultLabel = DescribeDefault(settings);
        var choices = new List<ProxyChoice>
        {
            new($"默认（{defaultLabel}）", null),
            new("直连", ProxyIds.Direct),
        };

        foreach (var profile in settings.Proxies)
        {
            choices.Add(new(profile.ToString(), profile.Id));
        }

        return choices;
    }

    private static string DescribeDefault(AppSettings settings)
    {
        if (string.Equals(settings.DefaultProxyId, ProxyIds.Direct, StringComparison.Ordinal))
        {
            return "直连";
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultProxyId))
        {
            return "系统代理";
        }

        var profile = settings.Proxies.FirstOrDefault(p => string.Equals(p.Id, settings.DefaultProxyId, StringComparison.Ordinal));
        return profile?.ToString() ?? "系统代理";
    }
}
