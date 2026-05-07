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
    private IntPtr _hwnd;
    private double _scale = 1.0;

    // Logical (DIP) heights tuned to each phase's content — no wasted space.
    private const int LogicalWidth       = 380;
    private const int LogicalAlertHeight = 240;
    private const int LogicalTimerHeight = 230;

    public BreakPopup()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        // Solid card per spec § 11 — no Mica (Mica bleeds the wallpaper through).
        Title = "20/20 — break";

        ConfigureChrome();

        _sm.PropertyChanged += OnStateChanged;
        Closed += (_, _) => _sm.PropertyChanged -= OnStateChanged;
        Activated += OnActivatedFocus;
        Root.Loaded += (_, _) =>
        {
            PlayEnterAnimation();
            // Belt-and-braces: the Activated event might fire before Root has
            // loaded. Once it's loaded, take focus so Enter / 1-9 / Esc work
            // without the user having to click first.
            Root.Focus(FocusState.Programmatic);
        };

        UpdateUi();
    }

    private void OnActivatedFocus(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        Root.Focus(FocusState.Programmatic);
    }

    public void BringToFrontAndFocus()
    {
        // Activate first so the window is actually on screen — without it,
        // SetForegroundWindow has nothing visible to bring up. Then
        // ForceForegroundWindow uses the AttachThreadInput trick to bypass
        // foreground-lock (the popup is shown from a timer tick, not user
        // input, so a plain SetForegroundWindow would get demoted to a
        // taskbar flash and the user's Enter / 1-9 / Esc keystrokes would
        // go to whatever window currently has focus instead of us).
        try { Activate(); } catch { /* best-effort */ }
        Win32Helper.ForceForegroundWindow(_hwnd);
        Root.Focus(FocusState.Programmatic);
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
        Win32Helper.ForceImmersiveDark(_hwnd);
        Win32Helper.RoundCorners(_hwnd);
        Win32Helper.RemoveBorder(_hwnd);

        _scale = Win32Helper.GetDpiScale(_hwnd);
        ResizeForCurrentPhase();
    }

    private void ResizeForCurrentPhase()
    {
        if (_appWindow is null) return;

        int logicalHeight = _sm.Phase == Phase.Alert ? LogicalAlertHeight : LogicalTimerHeight;

        // The window may currently live on the primary monitor (where it was
        // first created off-screen) but we're about to move it onto a
        // user-selected monitor with potentially different DPI. Compute size
        // in the *target* monitor's physical pixels, otherwise the WinUI
        // content gets clipped on higher-DPI displays.
        var target = App.Current.GetTargetDisplayArea();
        double scale = Win32Helper.GetDpiScaleForDisplayArea(target);
        _scale = scale;
        int width  = (int)Math.Round(LogicalWidth   * scale);
        int height = (int)Math.Round(logicalHeight  * scale);

        var workArea = target.WorkArea;
        // Defensive clamp: if the popup is taller/wider than the target
        // monitor's work area (unusual: small portrait monitor + 200% DPI),
        // shrink to fit so nothing gets clipped off-screen.
        width  = Math.Min(width,  Math.Max(1, workArea.Width));
        height = Math.Min(height, Math.Max(1, workArea.Height));

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
