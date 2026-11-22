using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Xdm.Ui.Tests.TestAppBuilder))]

namespace Xdm.Ui.Tests;

/// <summary>Headless 宿主：不引入任何渲染栈，只用官方 Fluent 主题。</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true, // 纯结构测试，不接 Skia
            });
}

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
