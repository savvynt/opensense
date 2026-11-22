using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using OpenSense.App.Views;
using Windows.Graphics;

namespace OpenSense.App.Services;

/// <summary>
/// Shows and hides the small always-on-top widget (<see cref="WidgetWindow"/>) and remembers whether it was on screen
/// and where, so it comes back after a restart. The window is made the first time it is shown.
/// </summary>
public sealed class WidgetService(IServiceProvider services, SettingsService settings) : IDisposable
{
    /// <summary>Distance from the screen's edge when the widget is first shown, in device-independent pixels.</summary>
    private const int EdgeMargin = 16;

    private WidgetWindow? _window;
    private PointInt32? _position;

    public bool IsVisible => _window?.AppWindow.IsVisible == true;

    /// <summary>The widget was shown or hidden.</summary>
    public event Action? VisibilityChanged;

    /// <summary>Brings the widget back if it was on screen when OpenSense last ran (without taking the keyboard from what the user is doing).</summary>
    public void Restore()
    {
        if (settings.Current.Ui.WidgetVisible)
            Show(activate: false);
    }

    public void Toggle()
    {
        if (IsVisible)
            Hide();
        else
            Show(activate: true);
    }

    public void Show(bool activate = true)
    {
        var window = EnsureWindow();
        window.AppWindow.Show(activate);
        Remember(true);
        VisibilityChanged?.Invoke();
    }

    public void Hide()
    {
        if (_window is not { } window)
            return;
        SavePosition();
        window.AppWindow.Hide();
        Remember(false);
        VisibilityChanged?.Invoke();
    }

    private WidgetWindow EnsureWindow()
    {
        if (_window is { } existing)
            return existing;

        var window = services.GetRequiredService<WidgetWindow>();
        window.AppWindow.Closing += OnClosing;
        window.AppWindow.Changed += OnChanged;
        Place(window);
        _window = window;
        return window;
    }

    /// <summary>The close button hides the widget; the app itself quitting lets it close.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Current.IsExiting)
            return;
        args.Cancel = true;
        Hide();
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange && sender.IsVisible)
            _position = sender.Position;
    }

    /// <summary>Where it was left, if that is still on a screen; else the top right corner of the main one.</summary>
    private void Place(WidgetWindow window)
    {
        var ui = settings.Current.Ui;
        if (ui is { WidgetX: { } x, WidgetY: { } y } && DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.None) is not null)
        {
            window.AppWindow.Move(new PointInt32(x, y));
            return;
        }
        var work = DisplayArea.Primary.WorkArea;
        var scale = window.Scale;
        window.AppWindow.Move(new PointInt32(
            work.X + work.Width - (int)((WidgetWindow.WidthDip + EdgeMargin) * scale),
            work.Y + (int)(EdgeMargin * scale)));
    }

    private void SavePosition()
    {
        if (_position is not { } position || (settings.Current.Ui.WidgetX == position.X && settings.Current.Ui.WidgetY == position.Y))
            return;
        settings.Update(s => s with { Ui = s.Ui with { WidgetX = position.X, WidgetY = position.Y } });
    }

    /// <summary>Only the user's own show and hide count: quitting leaves it as it was, so it comes back.</summary>
    private void Remember(bool visible)
    {
        if (settings.Current.Ui.WidgetVisible != visible)
            settings.Update(s => s with { Ui = s.Ui with { WidgetVisible = visible } });
    }

    public void Dispose() => SavePosition();
}
