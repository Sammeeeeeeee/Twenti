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

    public TrayFlyout()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Title = "20/20 flyout";

        ConfigureChromeAndPosition();

        _sm.PropertyChanged += OnStateChanged;
        Closed += (_, _) => _sm.PropertyChanged -= OnStateChanged;
        Activated += OnActivated;

        UpdateUi();
    }

    private void ConfigureChromeAndPosition()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);

        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
            // Note: NOT IsAlwaysOnTop — keeping it false lets Activated/Deactivated
            // fire reliably so the flyout dismisses when you click elsewhere.
            p.SetBorderAndTitleBar(false, false);
        }

        appWindow.IsShownInSwitchers = false;
        Win32Helper.HideFromAltTab(hwnd);
        Win32Helper.MakeBorderless(hwnd);
        Win32Helper.ForceImmersiveDark(hwnd);
        Win32Helper.RoundCorners(hwnd);
        Win32Helper.RemoveBorder(hwnd);

        // Logical (DIP) — sized to fit the actual content with no waste.
        const int logicalWidth = 280;
        const int logicalHeight = 165;

        double scale = Win32Helper.GetDpiScale(hwnd);
        int width  = (int)Math.Round(logicalWidth  * scale);
        int height = (int)Math.Round(logicalHeight * scale);

        // Anchor bottom-right of whichever monitor the cursor is on.
        var workArea = Win32Helper.GetCursorDisplayArea().WorkArea;
        int margin = (int)Math.Round(12 * scale);
        int x = workArea.X + workArea.Width  - width  - margin;
        int y = workArea.Y + workArea.Height - height - margin;
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Close();
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => UpdateUi();

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
        Close();
    }

    private void OnSnooze5(object sender, RoutedEventArgs e)
    {
        _sm.Snooze(5);
        Close();
    }

    private void OnSnooze15(object sender, RoutedEventArgs e)
    {
        _sm.Snooze(15);
        Close();
    }
}
