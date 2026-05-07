using System;
using System.ComponentModel;
using System.Threading.Tasks;
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

    // Global foreground-change hook. Strong reference to the delegate keeps
    // it pinned for the OS while we hold the function pointer.
    //
    // Why a global hook (SetWinEventHook) instead of WinUI's Window.Activated:
    // a popup-style window with WS_POPUP + WS_EX_TOOLWINDOW does not reliably
    // receive Activated(Deactivated) events for foreground changes to other
    // processes — clicks outside the flyout simply wouldn't dismiss it. The
    // global hook fires on every foreground change in the session and is the
    // mechanism that actually works for this window class.
    private IntPtr _hook = IntPtr.Zero;
    private Win32Helper.WinEventCallback? _hookProc;

    // Brief grace period right after Show() — we may not be foreground yet
    // and a stray foreground-change for the activation churn would
    // self-dismiss us before the user even sees the flyout.
    private DateTime _shownAt;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(250);

    // Monotonic counter incremented on every Show. Stale auto-hide callbacks
    // queued from the previous show capture the old sequence number; if a
    // new ShowAt has happened in between, the queued hide bails.
    private int _showSequence;

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

    /// <summary>
    /// True iff the flyout window is currently shown. Read-only snapshot so
    /// the tray-click handler in App.xaml.cs can capture user intent at the
    /// click instant, before any queued hide/show races change the state.
    /// </summary>
    public bool IsVisible => _isVisible;

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

        UpdateUi();

        // Different monitors can have different DPI. Compute size in the
        // *target* monitor's physical pixels — using the source monitor's
        // scale would clip the flyout on higher-DPI displays.
        var target = App.Current.GetTargetDisplayArea();
        double scale = Win32Helper.GetDpiScaleForDisplayArea(target);
        _scale = scale;

        int width  = (int)Math.Round(LogicalWidth  * scale);
        int height = (int)Math.Round(LogicalHeight * scale);

        var workArea = target.WorkArea;
        int margin = (int)Math.Round(12 * scale);

        // Clamp to the work area: on small/rotated monitors at high DPI the
        // flyout could otherwise spill past the screen edge.
        width  = Math.Min(width,  Math.Max(1, workArea.Width  - 2 * margin));
        height = Math.Min(height, Math.Max(1, workArea.Height - 2 * margin));

        int x = workArea.X + workArea.Width  - width  - margin;
        int y = workArea.Y + workArea.Height - height - margin;

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // Set timing/sequence fields BEFORE Show so any in-flight hook
        // callbacks see the new _shownAt and skip via the grace period.
        _shownAt = DateTime.UtcNow;
        _isVisible = true;
        unchecked { _showSequence++; }

        _appWindow.Show(activateWindow: true);

        // Click came from the tray (Explorer.exe), so we don't auto-receive
        // focus. Force ourselves up via the AttachThreadInput trick.
        try { Activate(); } catch { /* best-effort */ }
        Win32Helper.ForceForegroundWindow(_hwnd);

        InstallForegroundHook();
        ScheduleGraceRecheck();
    }

    /// <summary>
    /// One-shot foreground check that fires just past the grace period.
    /// The grace window suppresses the foreground hook so spurious activation
    /// churn during ShowAt doesn't immediately self-dismiss us — but if the
    /// user clicks elsewhere INSIDE that window, the hook fired, the lambda
    /// returned without hiding, and no further hook fires until something
    /// else changes foreground. The popup then sits open behind whatever the
    /// user is doing. This recheck closes that gap: if, just after grace
    /// expires, the foreground isn't us and isn't the tray, hide.
    /// </summary>
    private void ScheduleGraceRecheck()
    {
        var ui = (Application.Current as App)?.UIQueue;
        if (ui is null) return;
        int seq = _showSequence;
        IntPtr ownHwnd = _hwnd;
        var delay = GracePeriod + TimeSpan.FromMilliseconds(50);

        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            ui.TryEnqueue(() =>
            {
                try
                {
                    if (_showSequence != seq) return;
                    if (!_isVisible) return;
                    IntPtr fg = Win32Helper.GetForegroundWindow();
                    if (fg == ownHwnd) return;
                    if (Win32Helper.IsShellTrayWindow(fg)) return;
                    HideInternal(autoHide: true);
                }
                catch
                {
                    // Don't crash the app on a teardown race.
                }
            });
        }, TaskScheduler.Default);
    }

    public void HideQuiet() => HideInternal(autoHide: false);

    private void HideInternal(bool autoHide)
    {
        if (!_isVisible) return;
        _isVisible = false;
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
            var app = Application.Current as App;
            if (app is null) return;
            var queue = app.UIQueue;
            if (queue is null) return;
            var ownHwnd = _hwnd;
            int seq = _showSequence;

            queue.TryEnqueue(() =>
            {
                try
                {
                    // A new ShowAt happened between the hook firing and this
                    // running — leaving the lambda would kill the new popup.
                    if (_showSequence != seq) return;
                    if (!_isVisible) return;
                    // The activated window IS us — we just took foreground
                    // (e.g. via ForceForegroundWindow). Don't self-dismiss.
                    if (hwnd == ownHwnd) return;
                    // The activation went to the tray (Shell_TrayWnd or one
                    // of its kin) — that means the user clicked our tray
                    // icon, and the click handler will toggle visibility
                    // synchronously. If we hide here, the click handler
                    // would then see _isVisible=false and reopen us, making
                    // the click look like it did nothing (the original
                    // "75% of clicks don't close" report). Let the click
                    // handler win.
                    if (Win32Helper.IsShellTrayWindow(hwnd)) return;
                    if (DateTime.UtcNow - _shownAt < GracePeriod) return;
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
