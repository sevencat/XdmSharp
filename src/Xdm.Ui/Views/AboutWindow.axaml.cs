using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Xdm.Ui.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {FormatVersion()}";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private static string FormatVersion()
    {
        var asm = typeof(AboutWindow).Assembly;

        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉 SourceLink 追加的 "+<commit>" 后缀
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var v = asm.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
