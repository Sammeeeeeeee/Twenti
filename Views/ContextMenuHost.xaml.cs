using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Twenti.Services;
using Windows.Graphics;
using Windows.Foundation;
using WinRT.Interop;
using FlyoutPlacementMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode;

namespace Twenti.Views;

/// <summary>
/// A 1×1 invisible WinUI window that exists solely to host a
/// <see cref="MenuFlyout"/> we open programmatically. Why this exists:
/// H.NotifyIcon's built-in <c>SecondWindow</c> ContextMenuMode creates
/// its own WinUI window and subclasses it via Win32
/// <c>SetWindowSubclass</c>. The SUBCLASSPROC delegate isn't strongly
/// rooted, so .NET 8's GC collects it; the next window message Windows
/// sends in (e.g. WM_ACTIVATEAPP from any other top-level window
/// closing) hits the dangling pointer and FailFasts the process.
///
/// By owning the host window ourselves and showing the MenuFlyout
/// attached to a XAML element inside it, we get the same Fluent menu
/// visuals (it's the same MenuFlyout type) without ever invoking
/// H.NotifyIcon's subclass-installing code path.
/// </summary>
public sealed partial class ContextMenuHost : Window
{
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private MenuFlyout? _currentMenu;
    private DateTime _shownAt;

    // Brief grace right after open: the MenuFlyout's own popup briefly
    // takes activation, which would otherwise trigger our auto-dismiss.
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(250);

    // Same polling pattern as TrayFlyout. WinUI 3's Activated event does
    // not fire reliably on borderless WS_POPUP windows, so we watch the
    // foreground window manually and close when it's not us / not the
    // tray.
    private DispatcherQueueTimer? _foregroundPoll;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private bool _warmedUp;

    public ContextMenuHost()
    {
        InitializeComponent();
        Title = "20/20 menu host";
        ConfigureChrome();
    }

    /// <summary>
    /// Pay the first-show cost up front: park the window off-screen and
    /// briefly Show/Hide it so DWM / Composition finishes initialising.
    /// Without this, the first right-click after launch positions the
    /// menu slightly differently from subsequent clicks because the
    /// window's first Show races with chrome configuration.
    /// </summary>
    public void WarmUp()
    {
        if (_appWindow is null || _warmedUp) return;
        _warmedUp = true;
        _appWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
        _appWindow.Show(activateWindow: false);
        _appWindow.Hide();
    }

    private void ConfigureChrome()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(false, false);
        }

        _appWindow.IsShownInSwitchers = false;
        Win32Helper.HideFromAltTab(_hwnd);
        Win32Helper.MakeBorderless(_hwnd);
    }

    /// <summary>
    /// Shows the menu pinned to the right edge of the cursor's monitor.
    /// The host window is parked 1×1 at <c>(monitorRight − 1, cursorY)</c>
    /// rather than at the raw cursor coordinates, because WinUI's
    /// placement engine won't right-align a flyout against a 1-pixel
    /// anchor sitting mid-screen (TopEdgeAlignedRight silently fails to
    /// position and the catch below would just hide the host). Pinning
    /// the anchor to the edge lets the proven TopEdgeAlignedLeft +
    /// auto-shift behaviour produce the standard tray-menu placement
    /// (right edge flush against the screen edge) without relying on any
    /// edge-aligned placement mode.
    /// </summary>
    public void ShowMenuAt(int screenX, int screenY, MenuFlyout menu)
    {
        if (_appWindow is null) return;

        var area = DisplayArea.GetFromPoint(
            new PointInt32(screenX, screenY), DisplayAreaFallback.Nearest);
        var bounds = area.OuterBounds;
        int anchorX = bounds.X + bounds.Width - 1;

        _appWindow.MoveAndResize(new RectInt32(anchorX, screenY, 1, 1));
        _shownAt = DateTime.UtcNow;
        _appWindow.Show(activateWindow: true);

        try { Activate(); } catch { /* best-effort */ }
        Win32Helper.ForceForegroundWindow(_hwnd);

        if (_currentMenu is not null)
        {
            try { _currentMenu.Closed -= OnMenuClosed; } catch { /* swallow */ }
        }
        _currentMenu = menu;
        menu.Closed += OnMenuClosed;

        menu.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;

        try
        {
            menu.ShowAt(Anchor, new Point(0, 0));
        }
        catch (Exception ex)
        {
            Logger.Warn($"ContextMenuHost.ShowMenuAt failed: {ex.Message}");
            HideHost();
            return;
        }

        StartForegroundPoll();
    }

    private void StartForegroundPoll()
    {
        var ui = (Application.Current as App)?.UIQueue;
        if (ui is null) return;
        if (_foregroundPoll is null)
        {
            _foregroundPoll = ui.CreateTimer();
            _foregroundPoll.Interval = PollInterval;
            _foregroundPoll.Tick += OnPollTick;
        }
        _foregroundPoll.Start();
    }

    private void StopForegroundPoll() => _foregroundPoll?.Stop();

    private void OnPollTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_currentMenu is null) { StopForegroundPoll(); return; }
            if (DateTime.UtcNow - _shownAt < GracePeriod) return;

            IntPtr fg = Win32Helper.GetForegroundWindow();
            if (fg == _hwnd) return;
            if (Win32Helper.IsFriendlyForeground(fg)) return;

            // User clicked elsewhere — dismiss the menu, which routes
            // through OnMenuClosed and hides the host.
            try { _currentMenu.Hide(); }
            catch { HideHost(); }
        }
        catch
        {
            // Polling failure must never crash the app.
        }
    }

    private void OnMenuClosed(object? sender, object e)
    {
        if (sender is MenuFlyout mf)
        {
            try { mf.Closed -= OnMenuClosed; } catch { /* swallow */ }
        }
        _currentMenu = null;
        StopForegroundPoll();
        HideHost();
    }

    private void HideHost()
    {
        try { _appWindow?.Hide(); } catch { /* swallow */ }
    }
}
