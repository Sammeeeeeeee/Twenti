using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Twenti.Services;
using Windows.Graphics;
using Windows.Foundation;
using WinRT.Interop;

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

    public ContextMenuHost()
    {
        InitializeComponent();
        Title = "20/20 menu host";
        ConfigureChrome();
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
    /// Shows the menu at the given screen-pixel coordinates. The host
    /// window is parked at that point (1×1, transparent), the
    /// MenuFlyout is opened relative to it. WinUI auto-positions to
    /// keep the menu on-screen.
    /// </summary>
    public void ShowMenuAt(int screenX, int screenY, MenuFlyout menu)
    {
        if (_appWindow is null) return;

        // Park the host at the cursor, sized just large enough that
        // WinUI can compute a placement target — actual menu visuals
        // come from the MenuFlyout's own popup chrome.
        _appWindow.MoveAndResize(new RectInt32(screenX, screenY, 1, 1));
        _appWindow.Show(activateWindow: true);

        try { Activate(); } catch { /* best-effort */ }
        Win32Helper.ForceForegroundWindow(_hwnd);

        // Hide the host the moment the user dismisses or selects from
        // the menu. We also detach the handler so a stale reference
        // doesn't keep a captured menu alive.
        if (_currentMenu is not null)
        {
            try { _currentMenu.Closed -= OnMenuClosed; } catch { /* swallow */ }
        }
        _currentMenu = menu;
        menu.Closed += OnMenuClosed;

        try
        {
            menu.ShowAt(Anchor, new Point(0, 0));
        }
        catch (Exception ex)
        {
            Logger.Warn($"ContextMenuHost.ShowMenuAt failed: {ex.Message}");
            HideHost();
        }
    }

    private void OnMenuClosed(object? sender, object e)
    {
        if (sender is MenuFlyout mf)
        {
            try { mf.Closed -= OnMenuClosed; } catch { /* swallow */ }
        }
        _currentMenu = null;
        HideHost();
    }

    private void HideHost()
    {
        try { _appWindow?.Hide(); } catch { /* swallow */ }
    }
}
