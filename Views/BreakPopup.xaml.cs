using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Twenti.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Twenti.Views;

public sealed partial class BreakPopup : Window
{
    private readonly BreakStateMachine _sm;

    public BreakPopup()
    {
        InitializeComponent();
        _sm = App.Current.StateMachine;

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        Title = "20/20 — break";

        ConfigureChromeAndPosition();

        _sm.PropertyChanged += OnStateChanged;
        Closed += (_, _) => _sm.PropertyChanged -= OnStateChanged;
        Activated += (_, _) => Root.Focus(FocusState.Programmatic);

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

        const int width = 380;
        const int height = 220;
        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        int x = area.X + (area.Width - width) / 2;
        int y = area.Y + area.Height - height - 12;
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => UpdateUi();

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
