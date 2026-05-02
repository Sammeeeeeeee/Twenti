using System;
using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Twenti.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Twenti.Views;

public sealed partial class BreakPopup : Window
{
    private readonly BreakStateMachine _sm;
    private bool _enterPlayed;
    private AppWindow? _appWindow;
    private double _scale = 1.0;

    // Logical (DIP) heights tuned to each phase's content — no wasted space.
    private const int LogicalWidth      = 380;
    private const int LogicalAlertHeight = 240;
    private const int LogicalTimerHeight = 220;

    public BreakPopup()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        // Solid card per spec § 11 — no Mica (Mica bleeds the wallpaper through).
        Title = "20/20 — break";

        ConfigureChrome();

        _sm.PropertyChanged += OnStateChanged;
        Closed += (_, _) => _sm.PropertyChanged -= OnStateChanged;
        Activated += (_, _) => Root.Focus(FocusState.Programmatic);
        Root.Loaded += (_, _) => PlayEnterAnimation();

        UpdateUi();
    }

    private void ConfigureChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
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
        Win32Helper.HideFromAltTab(hwnd);

        // Aggressive border kill — this is the order that worked:
        // 1. Strip the OS window styles entirely (WS_POPUP), so DWM has nothing to draw.
        // 2. Force the chrome theme dark so any residual paint matches the card.
        // 3. Round the corners.
        // 4. Tell DWM "no border colour" as belt-and-braces.
        Win32Helper.MakeBorderless(hwnd);
        Win32Helper.ForceImmersiveDark(hwnd);
        Win32Helper.RoundCorners(hwnd);
        Win32Helper.RemoveBorder(hwnd);

        _scale = Win32Helper.GetDpiScale(hwnd);
        ResizeForCurrentPhase();
    }

    private void ResizeForCurrentPhase()
    {
        if (_appWindow is null) return;

        int logicalHeight = _sm.Phase == Phase.Alert ? LogicalAlertHeight : LogicalTimerHeight;
        int width  = (int)Math.Round(LogicalWidth   * _scale);
        int height = (int)Math.Round(logicalHeight  * _scale);

        // True center of the monitor where the cursor lives.
        var workArea = Win32Helper.GetCursorDisplayArea().WorkArea;
        int x = workArea.X + (workArea.Width  - width)  / 2;
        int y = workArea.Y + (workArea.Height - height) / 2;
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void PlayEnterAnimation()
    {
        if (_enterPlayed) return;
        _enterPlayed = true;

        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)(Root.ActualWidth / 2), (float)(Root.ActualHeight / 2), 0);
        visual.Scale = new Vector3(0.96f, 0.96f, 1f);
        visual.Opacity = 0f;

        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, new Vector3(0.96f, 0.96f, 1f));
        scaleAnim.InsertKeyFrame(1f, Vector3.One, ease);
        scaleAnim.Duration = TimeSpan.FromMilliseconds(140);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f, ease);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(140);

        visual.StartAnimation("Scale", scaleAnim);
        visual.StartAnimation("Opacity", opacityAnim);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateUi();
        if (e.PropertyName == nameof(BreakStateMachine.Phase))
        {
            ResizeForCurrentPhase();
        }
    }

    private void UpdateUi()
    {
        bool isLong = _sm.IsLongBreak;
        TitleText.Text = isLong ? "Long break 🌿" : "Break time";
        CycleText.Text = isLong ? "🌿" : $"{_sm.Cycle}/3";

        bool prompt = _sm.Phase == Phase.Alert;
        PromptPanel.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;
        TimerPanel.Visibility  = prompt ? Visibility.Collapsed : Visibility.Visible;

        if (prompt)
        {
            PromptHeading.Text = isLong ? "Long break time! 🌿" : "Eye break!";
            PromptBody.Text = isLong ? "Step away for 2 minutes." : "Look 20 feet away for 20 seconds.";
            StartButtonLabel.Text = isLong ? "Start 2 min" : "Start 20 sec";

            int auto = Math.Max(0, _sm.AutoSnoozeLeftSec);
            int autoTotal = 12;
            double pct = 1.0 - (auto / (double)autoTotal);
            double width = Math.Max(0, Root.ActualWidth - 40);
            AutoSnoozeFill.Width = pct * width;
            AutoSnoozeCaption.Text = $"Auto-snoozing in {auto}s · press 1–9 to snooze that many minutes";
        }
        else
        {
            int total = Math.Max(_sm.CurrentBreakTotalSec, 1);
            int left = _sm.BreakLeftSec;
            if (isLong && left >= 60)
            {
                TimerNumber.Text = $"{left / 60}:{left % 60:00}";
                TimerUnit.Text = "min:sec";
            }
            else
            {
                TimerNumber.Text = left.ToString();
                TimerUnit.Text = "seconds";
            }
            double pct = Math.Clamp((total - left) / (double)total, 0, 1);
            double width = Math.Max(0, Root.ActualWidth - 40);
            TimerFill.Width = pct * width;
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) => _sm.StartBreak();
    private void OnSnooze(object sender, RoutedEventArgs e) => _sm.Snooze(5);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var phase = _sm.Phase;
        if (phase != Phase.Alert && phase != Phase.Break) return;

        switch (e.Key)
        {
            case VirtualKey.Enter when phase == Phase.Alert:
                _sm.StartBreak();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                _sm.Snooze(5);
                e.Handled = true;
                break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9:
                _sm.Snooze(e.Key - VirtualKey.Number0);
                e.Handled = true;
                break;
            case >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9:
                _sm.Snooze(e.Key - VirtualKey.NumberPad0);
                e.Handled = true;
                break;
        }
    }
}
