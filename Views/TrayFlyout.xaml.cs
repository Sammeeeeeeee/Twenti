using System;
using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Twenti.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Twenti.Views;

public sealed partial class TrayFlyout : Window
{
    private readonly BreakStateMachine _sm;
    private bool _enterPlayed;

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
        Root.Loaded += (_, _) => PlayEnterAnimation();

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
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(false, false);
        }

        appWindow.IsShownInSwitchers = false;
        Win32Helper.HideFromAltTab(hwnd);
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
        int x = workArea.X + workArea.Width - width - margin;
        int y = workArea.Y + workArea.Height - height - margin;
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void PlayEnterAnimation()
    {
        if (_enterPlayed) return;
        _enterPlayed = true;

        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var compositor = visual.Compositor;

        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        visual.Properties.InsertVector3("Translation", new Vector3(0, 14f, 0));
        visual.Opacity = 0f;

        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var translateAnim = compositor.CreateVector3KeyFrameAnimation();
        translateAnim.InsertKeyFrame(0f, new Vector3(0, 14f, 0));
        translateAnim.InsertKeyFrame(1f, Vector3.Zero, ease);
        translateAnim.Duration = TimeSpan.FromMilliseconds(180);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f, ease);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(180);

        visual.Properties.StartAnimation("Translation", translateAnim);
        visual.StartAnimation("Opacity", opacityAnim);
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
