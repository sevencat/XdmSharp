using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Xdm.Ui.Controls;

/// <summary>
/// 自绘标题栏（<c>UI-DESIGN.md</c> §11）：深色 30px，左侧图标 + 标题，右侧最小化/最大化/关闭。
/// 宿主窗口必须设 <c>WindowDecorations="BorderOnly"</c> 去掉系统 chrome，
/// 否则系统标题栏会与这里叠两层（<c>ExtendClientArea*</c> 在 Avalonia 12.1.6 去不干净）。
/// </summary>
public partial class TitleBar : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<TitleBar, string>(nameof(Title), string.Empty);

    /// <summary>对话框窗口关掉它（不显示最大化按钮）。</summary>
    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMaximize), true);

    /// <summary>模态对话框关掉它（不显示最小化按钮）。</summary>
    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMinimize), true);

    private Window? _window;

    public TitleBar() => InitializeComponent();

    /// <summary>标题文字；与宿主的 <see cref="Window.Title"/> 分开维护，后者仍供测试与任务栏使用。</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty
            || change.Property == ShowMaximizeProperty
            || change.Property == ShowMinimizeProperty)
        {
            SyncState();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        SyncState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            SyncState();
        }
    }

    private void SyncState()
    {
        // XAML 加载过程中属性先于子控件就位，此时字段还是 null。
        if (TitleText is null)
        {
            return;
        }

        TitleText.Text = Title;
        MinButton.IsVisible = ShowMinimize;
        MaxButton.IsVisible = ShowMaximize;

        var maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeGlyph.IsVisible = !maximized;
        RestoreGlyph.IsVisible = maximized;
        ToolTip.SetTip(MaxButton, maximized ? "向下还原" : "最大化");
    }

    /// <summary>按钮自己吃掉按下事件，避免点按钮时把窗口拖起来。</summary>
    private void OnButtonPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_window is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        _window.BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => _window?.Close();
}
