using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenSense.App.Helpers;
using OpenSense.App.Localization;
using OpenSense.App.ViewModels;
using OpenSense.Core.Hardware;
using Windows.Graphics;
using Windows.UI;

namespace OpenSense.App.Views;

/// <summary>
/// A small always-on-top window with the essentials: CPU and GPU temperature and power, the fans' speeds, the fan mode
/// and the GPU overclock switch. It shows what the dashboard and the fan and GPU clock pages show, from the same view
/// models, so the two always agree. <see cref="WidgetService"/> shows and hides it.
/// </summary>
public sealed partial class WidgetWindow : Window
{
    /// <summary>Wide enough for the three fan modes side by side.</summary>
    public const int WidthDip = 300;

    private static readonly Color UnknownTemperature = Color.FromArgb(255, 0x8A, 0x8A, 0x8A);

    public WidgetWindow(MonitorViewModel monitor, FanControlViewModel fans, GpuClocksViewModel gpu, SettingsViewModel settings)
    {
        Monitor = monitor;
        Fans = fans;
        Gpu = gpu;
        InitializeComponent();

        RootGrid.Language = AppLanguage.Current;
        RootGrid.FlowDirection = AppLanguage.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = "OpenSense";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "OpenSense.ico"));
        AppWindow.IsShownInSwitchers = false; // no taskbar button of its own: the tray icon has the toggle
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        CpuLabel.Text = Names.Chip(FanChip.Cpu);
        GpuLabel.Text = Names.Chip(FanChip.Gpu);
        OverclockLabel.Text = Strings.Get("Notice_GpuClocks_Title");
        ToolTipService.SetToolTip(FanIcon, Strings.Get("Tray_FanMode"));

        ApplyTheme(settings.Theme);
        settings.ThemeChanged += ApplyTheme;
        ContentHost.SizeChanged += (_, _) => FitToContent();
        RootGrid.Loaded += (_, _) => RootGrid.XamlRoot.Changed += (_, _) => FitToContent(); // moved to a screen with another scale
    }

    public MonitorViewModel Monitor { get; }

    public FanControlViewModel Fans { get; }

    public GpuClocksViewModel Gpu { get; }

    /// <summary>Screen pixels per device-independent pixel on the screen the window is on.</summary>
    public double Scale =>
        Windows.Win32.PInvoke.GetDpiForWindow(new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96.0;

    /// <summary>For the temperature colour (grey while unknown or asleep), as on the dashboard.</summary>
    public static SolidColorBrush TemperatureBrush(double celsius) =>
        new(double.IsNaN(celsius) ? UnknownTemperature : TemperatureScale.ColorFor(celsius));

    private void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>The window is as tall as what it shows (rows come and go with what the laptop has), and no taller.</summary>
    private void FitToContent()
    {
        if (ContentHost.ActualHeight <= 0)
            return;
        var scale = Scale;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(WidthDip * scale), (int)Math.Ceiling(ContentHost.ActualHeight * scale)));
    }

    /// <summary>
    /// The user flipped the switch (rather than the view model moving it). Switching on shows the overclock warning, in the
    /// main window because it needs room; if the user declines, the view model puts the switch back.
    /// </summary>
    private void OnOverclockToggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == Gpu.Enabled)
            return;
        if (toggle.IsOn)
            App.Current.ShowMainWindow();
        Gpu.Enabled = toggle.IsOn;
    }
}
