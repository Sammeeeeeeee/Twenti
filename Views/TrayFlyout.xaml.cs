using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Twenti.Services;
using Windows.Graphics;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace Twenti.Views;

public sealed partial class TrayFlyout : Window
{
    private readonly BreakStateMachine _sm;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private double _scale = 1.0;
    private bool _isVisible;
    private bool _warmedUp;

    private const int LogicalWidth  = 280;
    private const int LogicalHeight = 165;

    // Activation handshake (ShowAt → ForceForegroundWindow) churns Activated
    // events. Ignore Deactivated until the dust settles so we don't hide
    // ourselves in the first 300 ms of being open.
    private DateTime _shownAtUtc;
    private DateTime _lastHideUtc = DateTime.MinValue;
    private static readonly TimeSpan ShowGrace = TimeSpan.FromMilliseconds(300);

    public TrayFlyout()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Title = "20/20 flyout";

        ConfigureChromeOnce();

        _sm.PropertyChanged += OnStateChanged;
        Closed += OnClosed;
        // Standard dropdown pattern: hide when the flyout loses activation.
        // Vastly more reliable than the previous foreground-polling timer
        // (which hid the flyout on transient focus blips and then the
        // user's "close" click reopened it because flyoutVisible was
        // already false).
        Activated += OnActivated;

        UpdateUi();
    }

    private void ConfigureChromeOnce()
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
        Win32Helper.ForceImmersiveDark(_hwnd);
        Win32Helper.RoundCorners(_hwnd);
        Win32Helper.RemoveBorder(_hwnd);

        _scale = Win32Helper.GetDpiScale(_hwnd);
    }

    /// <summary>
    /// Pay the first-show cost up front: size and show the window fully off-screen,
    /// then hide. Forces DWM/Composition/Acrylic to fully initialise so the real
    /// first ShowAt() is instant instead of stuttering for ~half a second.
    /// </summary>
    public void WarmUp()
    {
        if (_appWindow is null || _warmedUp) return;
        _warmedUp = true;

        int width  = (int)Math.Round(LogicalWidth  * _scale);
        int height = (int)Math.Round(LogicalHeight * _scale);
        _appWindow.MoveAndResize(new RectInt32(-32000, -32000, width, height));
        _appWindow.Show(activateWindow: false);
        _appWindow.Hide();
    }

    /// <summary>
    /// Read-only visibility snapshot — exposed so external code can know
    /// whether the flyout is currently shown.
    /// </summary>
    public bool IsVisible => _isVisible;

    /// <summary>
    /// True if the flyout is visible OR was hidden within the last
    /// <paramref name="ms"/> milliseconds. Used by the tray-click handler
    /// to defeat the auto-hide-then-click race: when the user clicks the
    /// tray to close, the OS deactivates this window before our click
    /// handler runs, so by the time the click is processed the flyout
    /// already says "not visible". Without this debounce we'd reopen.
    /// </summary>
    public bool WasVisibleWithin(int ms) =>
        _isVisible || (DateTime.UtcNow - _lastHideUtc) < TimeSpan.FromMilliseconds(ms);

    public bool ToggleAt()
    {
        if (_isVisible)
        {
            HideQuiet();
            return false;
        }
        ShowAt();
        return true;
    }

    public void ShowAt()
    {
        if (_appWindow is null) return;

        // Reset transient state — a fresh open should never inherit the
        // "awaiting custom-snooze keypress" mode from the previous session.
        SetCustomSnoozeMode(false);
        UpdateUi();

        // Different monitors can have different DPI. Compute size in the
        // *target* monitor's physical pixels.
        var target = App.Current.GetTargetDisplayArea();
        double scale = Win32Helper.GetDpiScaleForDisplayArea(target);
        _scale = scale;

        int width  = (int)Math.Round(LogicalWidth  * scale);
        int height = (int)Math.Round(LogicalHeight * scale);

        var workArea = target.WorkArea;
        int margin = (int)Math.Round(12 * scale);

        width  = Math.Min(width,  Math.Max(1, workArea.Width  - 2 * margin));
        height = Math.Min(height, Math.Max(1, workArea.Height - 2 * margin));

        int x = workArea.X + workArea.Width  - width  - margin;
        int y = workArea.Y + workArea.Height - height - margin;

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        _shownAtUtc = DateTime.UtcNow;
        _isVisible = true;

        _appWindow.Show(activateWindow: true);

        // Click came from the tray (Explorer.exe), so we don't auto-receive
        // focus. Force ourselves up via the AttachThreadInput trick.
        try { Activate(); } catch { /* best-effort */ }
        Win32Helper.ForceForegroundWindow(_hwnd);

        // Take keyboard focus so 1–9 / Esc work after clicking "Custom…"
        // without the user having to click into the flyout body first.
        try { Root.Focus(FocusState.Programmatic); } catch { /* best-effort */ }
    }

    public void HideQuiet()
    {
        if (!_isVisible) return;
        _isVisible = false;
        _lastHideUtc = DateTime.UtcNow;
        try { _appWindow?.Hide(); } catch { /* window may be tearing down */ }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if (!_isVisible) return;
        // The first ~300 ms of being open is full of activation churn from
        // ForceForegroundWindow + AttachThreadInput. Ignore Deactivated
        // during that window so we don't self-dismiss right after opening.
        if (DateTime.UtcNow - _shownAtUtc < ShowGrace) return;
        HideQuiet();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _sm.PropertyChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isVisible) UpdateUi();
    }

    private void UpdateUi()
    {
        bool snoozed = _sm.Phase == Phase.Snoozed;
        bool isBreak = _sm.Phase is Phase.Alert or Phase.Break;

        int leftSec = snoozed ? _sm.SnoozeLeftSec
                              : isBreak ? _sm.BreakLeftSec
                              : _sm.WorkLeftSec;
        int totalSec = snoozed ? Math.Max(_sm.SnoozeLeftSec, 1)
                               : isBreak ? Math.Max(_sm.CurrentBreakTotalSec, 1)
                               : Math.Max(_sm.WorkTotalSec, 1);

        LabelText.Text = snoozed ? "Snoozed — resumes in"
                       : isBreak ? "Break time!"
                       : "Next break in";

        TimeText.Text = FormatTime(leftSec);
        CycleText.Text = $"{_sm.Cycle}/3";

        bool nearBreak = !snoozed && !isBreak && _sm.WorkLeftSec <= 120;
        var brushKey = snoozed ? "StatusGreyBrush"
                     : isBreak ? "AccentFillColorDefaultBrush"
                     : nearBreak ? "StatusYellowBrush"
                     : "StatusGreenBrush";

        if (Application.Current.Resources.TryGetValue(brushKey, out var b) && b is Brush brush)
        {
            TimeText.Foreground = brush;
            ProgressFill.Background = brush;
        }

        double width = Root.ActualWidth - 24;
        if (width < 1) width = LogicalWidth - 24;
        double pct = Math.Clamp(leftSec / (double)totalSec, 0, 1);
        ProgressFill.Width = pct * width;
    }

    private static string FormatTime(int totalSec)
    {
        if (totalSec >= 60) return $"{totalSec / 60}m {totalSec % 60:00}s";
        return $"{totalSec}s";
    }

    private void OnStartNow(object sender, RoutedEventArgs e)
    {
        _sm.TriggerBreakNow();
        HideQuiet();
    }

    private void OnSnooze15(object sender, RoutedEventArgs e)
    {
        _sm.Snooze(15);
        HideQuiet();
    }

    private bool _awaitingCustomSnooze;

    private void OnSnoozeCustom(object sender, RoutedEventArgs e)
    {
        SetCustomSnoozeMode(true);
        try { Root.Focus(FocusState.Programmatic); } catch { /* best-effort */ }
    }

    private void SetCustomSnoozeMode(bool on)
    {
        _awaitingCustomSnooze = on;
        SnoozeButtonsRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CustomHintBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_awaitingCustomSnooze) return;

        if (e.Key == VirtualKey.Escape)
        {
            SetCustomSnoozeMode(false);
            e.Handled = true;
            return;
        }
        int? minutes = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number9 => e.Key - VirtualKey.Number0,
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => e.Key - VirtualKey.NumberPad0,
            _ => null,
        };
        if (minutes is int m)
        {
            _sm.Snooze(m);
            SetCustomSnoozeMode(false);
            HideQuiet();
            e.Handled = true;
        }
    }
}
