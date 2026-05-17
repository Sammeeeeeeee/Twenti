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
// Disambiguate: both Microsoft.UI.Dispatching and Windows.System export
// DispatcherQueueTimer; the WinUI 3 one is what App.UIQueue.CreateTimer()
// returns, so alias it here and leave Windows.System.VirtualKey alone.
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Twenti.Views;

public sealed partial class BreakPopup : Window
{
    private readonly BreakStateMachine _sm;
    private bool _enterPlayed;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private double _scale = 1.0;
    private DateTime _shownAt = DateTime.UtcNow;

    // Logical (DIP) heights tuned to each phase's content — no wasted space.
    // Alert and timer footprints are close enough to share one constant,
    // which avoids a noticeable resize on phase change. Both panels use
    // a Grid with a star-sized spacer row so the action buttons stay
    // anchored to the bottom no matter how the height is tweaked.
    private const int LogicalWidth       = 380;
    private const int LogicalAlertHeight = 220;
    private const int LogicalTimerHeight = 220;

    // Pulse animation tuning.
    private static readonly TimeSpan EnterAnimationGrace = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(380);
    private const float PulsePeakScale = 1.05f;

    // Pulse animation state. Bell-curve animation driven by a 16ms tick.
    private DispatcherQueueTimer? _pulseTimer;
    private bool _pulseInFlight;
    private DateTime _pulseStartTime;
    private PointInt32 _pulseOrigPos;
    private SizeInt32 _pulseOrigSize;
    private int _pulseDeltaW;
    private int _pulseDeltaH;
    private Visual? _pulseVisual;

    // Foreground watcher — Activated/Deactivated only fires on state CHANGE,
    // so it misses every click after the first one if focus never comes back
    // to the popup. Polling the foreground HWND catches every focus shift.
    private DispatcherQueueTimer? _foregroundPoll;
    private IntPtr _lastForeground;
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(120);

    public BreakPopup()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        // Solid card per spec § 11 — no Mica (Mica bleeds the wallpaper through).
        Title = "20/20 — break";

        ConfigureChrome();

        _sm.PropertyChanged += OnStateChanged;
        Closed += OnClosed;
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
        StartForegroundPoll();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _sm.PropertyChanged -= OnStateChanged;
        StopForegroundPoll();
        StopPulseTimer();
    }

    private void OnActivatedFocus(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // User just clicked away (or alt-tabbed). Pulse the card so
            // their eye is drawn back — popup is always-on-top so it's
            // still visible, the pulse just gives it a soft attention nudge
            // without stealing focus back (which would fight the user).
            PulseAttention();
            return;
        }
        Root.Focus(FocusState.Programmatic);
    }

    private void StartForegroundPoll()
    {
        var ui = (Application.Current as App)?.UIQueue;
        if (ui is null) return;
        _foregroundPoll ??= ui.CreateTimer();
        _foregroundPoll.Interval = ForegroundPollInterval;
        _foregroundPoll.Tick -= OnForegroundTick;
        _foregroundPoll.Tick += OnForegroundTick;
        _lastForeground = Win32Helper.GetForegroundWindow();
        _foregroundPoll.Start();
    }

    private void StopForegroundPoll()
    {
        if (_foregroundPoll is null) return;
        try { _foregroundPoll.Stop(); } catch { }
        _foregroundPoll.Tick -= OnForegroundTick;
    }

    private void OnForegroundTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            var fg = Win32Helper.GetForegroundWindow();
            if (fg == _lastForeground) return;
            _lastForeground = fg;
            if (fg == _hwnd) return;
            // Skip pulses (and the refocus that follows) when the user
            // interacts with our own surfaces — the tray icon, our flyout,
            // our context menu, our toast. Without this, every tray click
            // while the popup is up would yank focus back from Shell_TrayWnd
            // before the click handler could open the flyout/menu, and the
            // user would experience the icon as unresponsive.
            if (Win32Helper.IsFriendlyForeground(fg)) return;
            // Real external interaction — nudge the popup so they notice
            // it's still waiting.
            PulseAttention();
        }
        catch
        {
            // Polling must never crash the popup.
        }
    }

    private void PulseAttention()
    {
        // Don't pulse during the entry animation or another in-flight
        // pulse — the bell curves would fight on the same window size.
        if (DateTime.UtcNow - _shownAt < EnterAnimationGrace) return;
        if (_pulseInFlight) return;
        if (_appWindow is null) return;

        _pulseOrigPos  = _appWindow.Position;
        _pulseOrigSize = _appWindow.Size;
        if (_pulseOrigSize.Width <= 0 || _pulseOrigSize.Height <= 0) return;

        int peakW = (int)Math.Round(_pulseOrigSize.Width  * PulsePeakScale);
        int peakH = (int)Math.Round(_pulseOrigSize.Height * PulsePeakScale);
        _pulseDeltaW = peakW - _pulseOrigSize.Width;
        _pulseDeltaH = peakH - _pulseOrigSize.Height;

        // Composition scale on Root, synced to the window scale, so the
        // XAML content visibly grows with the window instead of leaving
        // a band of background brush around the edges.
        try
        {
            _pulseVisual = ElementCompositionPreview.GetElementVisual(Root);
            _pulseVisual.CenterPoint = new Vector3(
                (float)(Root.ActualWidth  / 2),
                (float)(Root.ActualHeight / 2),
                0);
        }
        catch
        {
            _pulseVisual = null;
        }

        var ui = (Application.Current as App)?.UIQueue;
        if (ui is null) return;

        _pulseTimer ??= ui.CreateTimer();
        _pulseTimer.Interval = TimeSpan.FromMilliseconds(16);
        _pulseTimer.Tick -= OnPulseTick;
        _pulseTimer.Tick += OnPulseTick;
        _pulseStartTime = DateTime.UtcNow;
        _pulseInFlight = true;
        _pulseTimer.Start();
    }

    private void OnPulseTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_appWindow is null) { EndPulse(); return; }

            var elapsed = (DateTime.UtcNow - _pulseStartTime).TotalMilliseconds;
            var t = Math.Clamp(elapsed / PulseDuration.TotalMilliseconds, 0, 1);
            // sin(πt) bell — rises 0→1 by t=0.5, returns to 0 by t=1.
            var bell = Math.Sin(Math.PI * t);

            int w = _pulseOrigSize.Width  + (int)Math.Round(_pulseDeltaW * bell);
            int h = _pulseOrigSize.Height + (int)Math.Round(_pulseDeltaH * bell);
            // Re-center so the window grows from its center, not its top-left.
            int x = _pulseOrigPos.X - (w - _pulseOrigSize.Width)  / 2;
            int y = _pulseOrigPos.Y - (h - _pulseOrigSize.Height) / 2;
            _appWindow.MoveAndResize(new RectInt32(x, y, w, h));

            if (_pulseVisual is not null)
            {
                float s = 1.0f + (float)((PulsePeakScale - 1.0) * bell);
                _pulseVisual.Scale = new Vector3(s, s, 1f);
            }

            if (t >= 1)
            {
                // Snap back to exact original — accumulated rounding error
                // from the int-based MoveAndResize would otherwise leave the
                // window 1–2 px off after every pulse.
                _appWindow.MoveAndResize(new RectInt32(
                    _pulseOrigPos.X, _pulseOrigPos.Y,
                    _pulseOrigSize.Width, _pulseOrigSize.Height));
                if (_pulseVisual is not null)
                    _pulseVisual.Scale = Vector3.One;
                EndPulse();
                // Reclaim foreground so the next external click also
                // registers as a foreground change. Without this, clicking
                // the same window twice only pulses once (the foreground
                // HWND doesn't change between the two clicks).
                BringToFrontAndFocus();
                _lastForeground = _hwnd;
            }
        }
        catch
        {
            EndPulse();
        }
    }

    private void EndPulse()
    {
        if (_pulseTimer is not null)
        {
            try { _pulseTimer.Stop(); } catch { }
            _pulseTimer.Tick -= OnPulseTick;
        }
        _pulseInFlight = false;
    }

    private void StopPulseTimer()
    {
        if (_pulseTimer is null) return;
        try { _pulseTimer.Stop(); } catch { }
        _pulseTimer.Tick -= OnPulseTick;
        _pulseInFlight = false;
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
            // Cancel any in-flight pulse before the phase-driven resize —
            // the pulse's cached "original size" would otherwise be stale.
            EndPulse();
            ResizeForCurrentPhase();
        }
    }

    private void UpdateUi()
    {
        bool isLong = _sm.IsLongBreak;
        TitleText.Text = isLong ? "Long break" : "Eye break";
        CycleText.Text = $"{_sm.Cycle}/3";

        bool prompt = _sm.Phase == Phase.Alert;
        PromptPanel.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;
        TimerPanel.Visibility  = prompt ? Visibility.Collapsed : Visibility.Visible;

        // Manually-triggered breaks (flyout's "Start break now") have no
        // work-timer slip to recover, so Snooze is meaningless — the user
        // wants out, back into the work countdown they were already in.
        // Flip the Snooze button to "Cancel" and re-route Esc accordingly.
        bool cancelMode = _sm.IsManuallyTriggered;
        string actionLabel = cancelMode ? "Cancel" : "Snooze";
        SnoozeButtonLabel.Text = actionLabel;
        SnoozeTimerButtonLabel.Text = actionLabel;

        if (prompt)
        {
            PromptBody.Text = isLong ? "Step away for 2 minutes." : "Look 20 feet away for 20 seconds.";
            StartButtonLabel.Text = isLong ? "Start 2 min" : "Start 20 sec";

            int auto = Math.Max(0, _sm.AutoSnoozeLeftSec);
            int autoTotal = 12;
            double pct = 1.0 - (auto / (double)autoTotal);
            double width = Math.Max(0, Root.ActualWidth - 40);
            AutoSnoozeFill.Width = pct * width;
            string snoozeHint = cancelMode
                ? "Esc to cancel · 1–9 to snooze that many minutes"
                : "press 1–9 to snooze that many minutes";
            AutoSnoozeCaption.Text = isLong
                ? $"Auto-snoozing in {auto}s · 1–9 to snooze · Del to skip to 20s"
                : $"Auto-snoozing in {auto}s · {snoozeHint}";
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

            // During a long-break countdown, expose the same Del → 20s
            // shortcut that exists during the Alert phase, with the
            // elapsed time discounted against the 20s target.
            bool showSkip = isLong && _sm.Phase == Phase.Break;
            SkipTimerButton.Visibility = showSkip ? Visibility.Visible : Visibility.Collapsed;
            SkipTimerGapCol.Width = showSkip ? new GridLength(8) : new GridLength(0);
            SkipTimerBtnCol.Width = showSkip ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) => _sm.StartBreak();
    private void OnSnoozeOrCancel(object sender, RoutedEventArgs e) => SnoozeOrCancel();
    private void OnSkipLong(object sender, RoutedEventArgs e) => _sm.SkipLongBreak();

    private void SnoozeOrCancel()
    {
        if (_sm.IsManuallyTriggered) _sm.CancelBreak();
        else _sm.Snooze(5);
    }

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
            case VirtualKey.Delete when (phase == Phase.Alert || phase == Phase.Break) && _sm.IsLongBreak:
                _sm.SkipLongBreak();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                SnoozeOrCancel();
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
