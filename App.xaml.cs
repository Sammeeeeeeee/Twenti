using System;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Twenti.Services;
using Twenti.Views;

namespace Twenti;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public BreakStateMachine StateMachine { get; private set; } = null!;
    public SoundEngine Sound { get; private set; } = null!;
    public ThemeListener Theme { get; private set; } = null!;
    public DispatcherQueue UIQueue { get; private set; } = null!;

    private TaskbarIcon? _trayIcon;
    private TrayIconRenderer? _iconRenderer;
    private MainWindow? _ownerWindow;
    private BreakPopup? _popup;
    private TrayFlyout? _flyout;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UIQueue = DispatcherQueue.GetForCurrentThread();

        _ownerWindow = new MainWindow();

        Sound = new SoundEngine();
        Theme = new ThemeListener();
        StateMachine = new BreakStateMachine(UIQueue);

        _iconRenderer = new TrayIconRenderer();

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "20/20 — Eye Break Reminder · click for details, right-click for options",
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = BuildContextMenu(),
            LeftClickCommand = new RelayCommand(OnTrayLeftClick),
        };
        _trayIcon.ForceCreate();

        StateMachine.PropertyChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);
        StateMachine.PhaseChanged += OnPhaseChanged;
        StateMachine.BreakCompleted += (_, _) => UIQueue.TryEnqueue(Sound.PlayBreakComplete);
        Theme.ThemeChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);

        StateMachine.Start();
        RefreshTray();
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();

        foreach (var mins in new[] { 5, 15, 30 })
        {
            var item = new MenuFlyoutItem { Text = $"Snooze {mins} minutes" };
            item.Click += (_, _) => StateMachine.Snooze(mins);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());

        var muteItem = new ToggleMenuFlyoutItem { Text = "Mute sounds", IsChecked = Sound.Muted };
        muteItem.Click += (_, _) => Sound.Muted = muteItem.IsChecked;
        menu.Items.Add(muteItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var quit = new MenuFlyoutItem
        {
            Text = "Quit 20/20",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.IndianRed),
        };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        return menu;
    }

    private void OnTrayLeftClick()
    {
        UIQueue.TryEnqueue(() =>
        {
            if (StateMachine.Phase is Phase.Alert or Phase.Break)
            {
                ShowOrFocusPopup();
                return;
            }
            ShowFlyout();
        });
    }

    private void ShowFlyout()
    {
        if (_flyout is not null)
        {
            _flyout.Activate();
            return;
        }
        _flyout = new TrayFlyout();
        _flyout.Closed += (_, _) => _flyout = null;
        _flyout.Activate();
    }

    private void OnPhaseChanged(object? sender, Phase phase)
    {
        UIQueue.TryEnqueue(() =>
        {
            switch (phase)
            {
                case Phase.PrePing:
                    Sound.PlayPrePing();
                    ShowFlyout();
                    break;
                case Phase.Alert:
                    Sound.PlayPopupChime();
                    ShowOrFocusPopup();
                    break;
                case Phase.Break:
                    Sound.StartAmbient();
                    break;
                case Phase.Working:
                    Sound.StopAmbient();
                    HidePopup();
                    break;
                case Phase.Snoozed:
                    Sound.StopAmbient();
                    Sound.PlaySnooze();
                    HidePopup();
                    break;
            }
            RefreshTray();
        });
    }

    private void ShowOrFocusPopup()
    {
        if (_popup is null)
        {
            _popup = new BreakPopup();
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.Activate();
    }

    private void HidePopup()
    {
        _popup?.Close();
        _popup = null;
    }

    private void RefreshTray()
    {
        if (_trayIcon is null || _iconRenderer is null) return;
        _trayIcon.Icon = _iconRenderer.Render(StateMachine, Theme.IsDark);
        _trayIcon.ToolTipText = StateMachine.TooltipText;
    }

    public void Quit()
    {
        StateMachine.Stop();
        Sound.Dispose();
        _trayIcon?.Dispose();
        _popup?.Close();
        _flyout?.Close();
        _ownerWindow?.Close();
        Exit();
    }
}

internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
}
