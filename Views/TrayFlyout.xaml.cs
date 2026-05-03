using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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

    // Foreground-change hook. We keep a strong reference to the delegate so the
    // GC doesn't reclaim it while the OS still has the function pointer.
    private IntPtr _hook = IntPtr.Zero;
    private Win32Helper.WinEventCallback? _hookProc;

    // Brief grace period right after Show() — Windows may not have made us the
    // foreground window yet, and we don't want to immediately self-dismiss.
    private DateTime _shownAt;
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(250);

    public TrayFlyout()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Title = "20/20 flyout";

        ConfigureChromeOnce();

        _sm.PropertyChanged += OnStateChanged;

        UpdateUi();
        _appWindow?.Hide();
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
            // Topmost so it draws above the user's app windows when shown.
            // The foreground-change hook below handles dismissal.
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

    public void ShowAt()
    {
        if (_appWindow is null) return;

        UpdateUi();

        const int logicalWidth  = 280;
        const int logicalHeight = 165;
        int width  = (int)Math.Round(logicalWidth  * _scale);
        int height = (int)Math.Round(logicalHeight * _scale);

        var workArea = App.Current.GetTargetDisplayArea().WorkArea;
        int margin = (int)Math.Round(12 * _scale);
        int x = workArea.X + workArea.Width  - width  - margin;
        int y = workArea.Y + workArea.Height - height - margin;

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _appWindow.Show(activateWindow: true);

        // Drag ourselves to the foreground — necessary because the click came
        // from the tray (Explorer.exe), so we don't automatically get focus.
        Win32Helper.SetForegroundWindow(_hwnd);
        Activate();

        _shownAt = DateTime.UtcNow;
        _isVisible = true;

        InstallForegroundHook();
    }

    public void HideQuiet()
    {
        if (!_isVisible || _appWindow is null) return;
        _isVisible = false;
        UninstallForegroundHook();
        _appWindow.Hide();
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
        Win32Helper.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
    }

    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Hook fires on a non-UI thread — bounce back to the dispatcher.
        App.Current.UIQueue.TryEnqueue(() =>
        {
            if (!_isVisible) return;
            // Ignore foreground changes during the grace period after show, so
            // we don't dismiss before SetForegroundWindow has taken effect.
            if (DateTime.UtcNow - _shownAt < GracePeriod) return;
            // Hide whenever any window other than ourselves becomes foreground.
            if (hwnd != _hwnd) HideQuiet();
        });
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
        if (width < 1) width = 280 - 24;
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
