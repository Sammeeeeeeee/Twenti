using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Twenti.Services;
using Windows.Graphics;
using WinRT.Interop;

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

    // Foreground-change hook. Strong reference to the delegate keeps it pinned
    // for the OS while we hold the function pointer.
    private IntPtr _hook = IntPtr.Zero;
    private Win32Helper.WinEventCallback? _hookProc;

    // Brief grace period right after Show() — we may not be foreground yet,
    // so don't self-dismiss on the activation churn.
    private DateTime _shownAt;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(250);

    // When the flyout was last auto-hidden by the foreground hook. A tray click
    // racing with that auto-hide should "close" rather than re-open.
    private DateTime _lastAutoHiddenAt = DateTime.MinValue;
    private static readonly TimeSpan ToggleDebounce = TimeSpan.FromMilliseconds(500);

    public TrayFlyout()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Title = "20/20 flyout";

        ConfigureChromeOnce();

        _sm.PropertyChanged += OnStateChanged;
        Closed += OnClosed;

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
    /// Caller decides when — at startup we defer this off the critical path.
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

    public bool ToggleAt()
    {
        if (_isVisible)
        {
            HideQuiet();
            return false;
        }
        // The foreground hook may already have hidden us in response to the
        // SAME tray click that's now invoking the toggle. In that case the
        // user's intent is "close" — don't reopen. We only honour the
        // debounce when the hide came from the auto-hide path; explicit
        // user-initiated hides (button presses) leave _lastAutoHiddenAt
        // untouched so re-clicking the tray can immediately reopen.
        if (DateTime.UtcNow - _lastAutoHiddenAt < ToggleDebounce)
        {
            _lastAutoHiddenAt = DateTime.MinValue;
            return false;
        }
        ShowAt();
        return true;
    }

    public void ShowAt()
    {
        if (_appWindow is null) return;

        UpdateUi();

        int width  = (int)Math.Round(LogicalWidth  * _scale);
        int height = (int)Math.Round(LogicalHeight * _scale);

        var workArea = App.Current.GetTargetDisplayArea().WorkArea;
        int margin = (int)Math.Round(12 * _scale);
        int x = workArea.X + workArea.Width  - width  - margin;
        int y = workArea.Y + workArea.Height - height - margin;

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _appWindow.Show(activateWindow: true);

        // Click came from the tray (Explorer.exe), so we don't auto-receive
        // focus. Drag ourselves to the foreground.
        try { Win32Helper.SetForegroundWindow(_hwnd); } catch { /* best-effort */ }
        try { Activate(); } catch { /* best-effort */ }

        _shownAt = DateTime.UtcNow;
        _isVisible = true;

        InstallForegroundHook();
    }

    public void HideQuiet() => HideInternal(autoHide: false);

    private void HideInternal(bool autoHide)
    {
        if (!_isVisible) return;
        _isVisible = false;
        if (autoHide) _lastAutoHiddenAt = DateTime.UtcNow;
        UninstallForegroundHook();
        try { _appWindow?.Hide(); } catch { /* window may be tearing down */ }
    }

    private void InstallForegroundHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hookProc = OnForegroundChanged;
        _hook = Win32Helper.SetWinEventHook(
            Win32Helper.EVENT_SYSTEM_FOREGROUND,
            Win32Helper.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookProc,
            idProcess: 0,
            idThread: 0,
            dwFlags: Win32Helper.WINEVENT_OUTOFCONTEXT);
    }

    private void UninstallForegroundHook()
    {
        if (_hook == IntPtr.Zero) return;
        try { Win32Helper.UnhookWinEvent(_hook); } catch { /* ignore */ }
        _hook = IntPtr.Zero;
        _hookProc = null;
    }

    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Hook fires on a non-UI thread, sometimes in storms (e.g. when the
        // session is locking / system suspending). Keep this body ultra-cheap
        // and bounce to the dispatcher inside a try/catch — a stray exception
        // here propagates into CoreMessagingXP and fails-fast the whole
        // process (seen in the wild as 0xc000027b).
        try
        {
            var app = App.Current;
            if (app is null) return;
            var queue = app.UIQueue;
            if (queue is null) return;
            var ownHwnd = _hwnd;

            queue.TryEnqueue(() =>
            {
                try
                {
                    if (!_isVisible) return;
                    if (DateTime.UtcNow - _shownAt < GracePeriod) return;
                    if (hwnd == ownHwnd) return;
                    HideInternal(autoHide: true);
                }
                catch
                {
                    // Last-ditch swallow: failing here would crash the app.
                }
            });
        }
        catch
        {
            // Same: the OS is still holding our function pointer; never throw.
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        UninstallForegroundHook();
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

    private void OnSnooze5(object sender, RoutedEventArgs e)
    {
        _sm.Snooze(5);
        HideQuiet();
    }

    private void OnSnooze15(object sender, RoutedEventArgs e)
    {
        _sm.Snooze(15);
        HideQuiet();
    }
}
