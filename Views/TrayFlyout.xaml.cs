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
    private double _scale = 1.0;
    private bool _isVisible;

    public TrayFlyout()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Title = "20/20 flyout";

        ConfigureChromeOnce();

        _sm.PropertyChanged += OnStateChanged;
        Activated += OnActivated;

        UpdateUi();

        // Stay hidden until first ShowFlyout(). Avoids the window flashing on app start.
        _appWindow?.Hide();
    }

    private void ConfigureChromeOnce()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
            // IsAlwaysOnTop must be true so the flyout always lands above the
            // window the user clicked from. Click-away is handled via the
            // Activated event — it still fires for topmost windows.
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(false, false);
        }

        _appWindow.IsShownInSwitchers = false;
        Win32Helper.HideFromAltTab(hwnd);
        Win32Helper.MakeBorderless(hwnd);
        Win32Helper.ForceImmersiveDark(hwnd);
        Win32Helper.RoundCorners(hwnd);
        Win32Helper.RemoveBorder(hwnd);

        _scale = Win32Helper.GetDpiScale(hwnd);
    }

    /// <summary>
    /// Repositions and shows the flyout. Cheap because the Window already exists —
    /// we just move the AppWindow and call Show().
    /// </summary>
    public void ShowAt()
    {
        if (_appWindow is null) return;

        UpdateUi();

        // Recalculate target monitor every time, since the cursor may have moved.
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
        _isVisible = true;
        Activate();
    }

    public void HideQuiet()
    {
        if (!_isVisible || _appWindow is null) return;
        _isVisible = false;
        _appWindow.Hide();
    }

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            HideQuiet();
        }
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
