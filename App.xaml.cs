using System;
using System.Diagnostics;
using System.Threading.Tasks;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Twenti.Services;
using Twenti.Views;

namespace Twenti;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public BreakStateMachine StateMachine { get; private set; } = null!;
    public SoundEngine Sound { get; private set; } = null!;
    public ThemeListener Theme { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
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

        Settings = AppSettings.Load();
        Sound = new SoundEngine { Muted = Settings.Muted };
        Theme = new ThemeListener();
        StateMachine = new BreakStateMachine(UIQueue);
        _iconRenderer = new TrayIconRenderer();

        // Get the tray icon visible as fast as possible — that's the user's
        // signal that the app has started. Anything that isn't strictly
        // required for the icon to render or respond to clicks is deferred
        // below.
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "",
            // SecondWindow hosts a real WinUI MenuFlyout — we need this so
            // Click handlers and ToggleMenuFlyoutItem.IsChecked behave
            // normally. PopupMenu mode strips the Click events.
            ContextMenuMode = ContextMenuMode.SecondWindow,
            // Without this, H.NotifyIcon waits ~500ms for a possible
            // double-click before firing the single-click command. That
            // delay is what was making the click-to-close race with the
            // foreground-change auto-hide.
            NoLeftClickDelay = true,
            ContextFlyout = BuildContextMenu(),
            LeftClickCommand = new RelayCommand(OnTrayLeftClick),
        };
        _trayIcon.ForceCreate();

        StateMachine.PropertyChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);
        StateMachine.PhaseChanged += OnPhaseChanged;
        StateMachine.BreakCompleted += (_, _) => UIQueue.TryEnqueue(Sound.PlayBreakComplete);
        Theme.ThemeChanged += (_, _) => UIQueue.TryEnqueue(() =>
        {
            RefreshTray();
            // SecondWindow ContextMenuMode hosts the menu in a separate window
            // that doesn't inherit the app's theme — rebuild so the new
            // RequestedTheme on each item picks up the right brushes.
            if (_trayIcon is not null) _trayIcon.ContextFlyout = BuildContextMenu();
        });

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        StateMachine.Start();
        RefreshTray();

        // Defer the heavy stuff (flyout window, acrylic backdrop init, update
        // probe) so the tray icon doesn't have to wait for them.
        UIQueue.TryEnqueue(DispatcherQueuePriority.Low, OnIdleStartup);
    }

    private void OnIdleStartup()
    {
        _flyout = new TrayFlyout();
        _flyout.WarmUp();

        if (!Settings.HasShownTrayHint)
        {
            Settings.HasShownTrayHint = true;
            Settings.Save();
            _trayIcon?.ShowNotification(
                title: "Twenti is running",
                message: "Drag the icon to the always-shown area of the taskbar so you can see your timer at a glance.",
                timeout: TimeSpan.FromSeconds(7));
        }

        if (Settings.CheckForUpdates)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => CheckForUpdatesAsync(silentIfNone: true));
        }
    }

    /// <summary>
    /// Resolves the target display area for the popup/flyout, honouring the
    /// user's "Main monitor" / "Follow cursor" preference.
    /// </summary>
    public DisplayArea GetTargetDisplayArea()
    {
        return Settings.Monitor == MonitorPreference.MainMonitor
            ? DisplayArea.Primary
            : Win32Helper.GetCursorDisplayArea();
    }

    private async Task CheckForUpdatesAsync(bool silentIfNone)
    {
        var info = await new UpdateChecker().CheckAsync().ConfigureAwait(false);
        UIQueue.TryEnqueue(() =>
        {
            if (info is null)
            {
                if (!silentIfNone) ShowToast("You're on the latest version.");
                return;
            }

            if (_trayIcon is null) return;
            _trayIcon.ShowNotification(
                title: $"Twenti {info.LatestVersion} is available",
                message: "Click to open the release page.",
                timeout: TimeSpan.FromSeconds(10));

            _trayIcon.LeftClickCommand = new RelayCommand(() =>
            {
                _trayIcon.LeftClickCommand = new RelayCommand(OnTrayLeftClick);
                OpenUrl(info.ReleaseUrl);
            });
        });
    }

    private void ShowToast(string message)
    {
        _trayIcon?.ShowNotification("Twenti", message, timeout: TimeSpan.FromSeconds(4));
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        var theme = Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        // SecondWindow mode hosts the menu in a separate window that doesn't
        // inherit the app theme, and its auto-width measurement was clipping
        // the longer items by ~1 character. Setting RequestedTheme on each
        // item fixes the brushes; setting MinWidth gives the host enough
        // headroom to render the longest label ("Check for updates now").
        const double MenuMinWidth = 240;
        void Style(MenuFlyoutItemBase item)
        {
            item.RequestedTheme = theme;
            item.MinWidth = MenuMinWidth;
        }

        foreach (var mins in new[] { 5, 15, 30 })
        {
            var item = new MenuFlyoutItem { Text = $"Snooze {mins} minutes" };
            item.Click += (_, _) => StateMachine.Snooze(mins);
            Style(item);
            menu.Items.Add(item);
        }
        var sep1 = new MenuFlyoutSeparator(); Style(sep1); menu.Items.Add(sep1);

        var monitorMenu = new MenuFlyoutSubItem { Text = "Show popup on" };
        Style(monitorMenu);
        var followCursor = new ToggleMenuFlyoutItem
        {
            Text = "The monitor with my cursor",
            IsChecked = Settings.Monitor == MonitorPreference.FollowCursor,
        };
        Style(followCursor);
        var mainMonitor = new ToggleMenuFlyoutItem
        {
            Text = "Main monitor only",
            IsChecked = Settings.Monitor == MonitorPreference.MainMonitor,
        };
        Style(mainMonitor);
        followCursor.Click += (_, _) =>
        {
            Settings.Monitor = MonitorPreference.FollowCursor;
            Settings.Save();
            followCursor.IsChecked = true;
            mainMonitor.IsChecked = false;
        };
        mainMonitor.Click += (_, _) =>
        {
            Settings.Monitor = MonitorPreference.MainMonitor;
            Settings.Save();
            mainMonitor.IsChecked = true;
            followCursor.IsChecked = false;
        };
        monitorMenu.Items.Add(followCursor);
        monitorMenu.Items.Add(mainMonitor);
        menu.Items.Add(monitorMenu);

        var muteItem = new ToggleMenuFlyoutItem { Text = "Mute sounds", IsChecked = Sound.Muted };
        muteItem.Click += (_, _) =>
        {
            Sound.Muted = muteItem.IsChecked;
            Settings.Muted = muteItem.IsChecked;
            Settings.Save();
        };
        Style(muteItem);
        menu.Items.Add(muteItem);

        var autoStart = new ToggleMenuFlyoutItem
        {
            Text = "Start with Windows",
            IsChecked = AutoStart.IsEnabled,
        };
        autoStart.Click += (_, _) => AutoStart.SetEnabled(autoStart.IsChecked);
        Style(autoStart);
        menu.Items.Add(autoStart);

        var sep2 = new MenuFlyoutSeparator(); Style(sep2); menu.Items.Add(sep2);

        var updateToggle = new ToggleMenuFlyoutItem
        {
            Text = "Check for updates",
            IsChecked = Settings.CheckForUpdates,
        };
        updateToggle.Click += (_, _) =>
        {
            Settings.CheckForUpdates = updateToggle.IsChecked;
            Settings.Save();
        };
        Style(updateToggle);
        menu.Items.Add(updateToggle);

        var checkNow = new MenuFlyoutItem { Text = "Check for updates now" };
        checkNow.Click += async (_, _) => await CheckForUpdatesAsync(silentIfNone: false);
        Style(checkNow);
        menu.Items.Add(checkNow);

        var sep3 = new MenuFlyoutSeparator(); Style(sep3); menu.Items.Add(sep3);

        var quit = new MenuFlyoutItem
        {
            Text = "Quit Twenti",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        };
        quit.Click += (_, _) => Quit();
        Style(quit);
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
            ToggleFlyout();
        });
    }

    private void ToggleFlyout()
    {
        _flyout ??= new TrayFlyout();
        _flyout.ToggleAt();
    }

    private void ShowFlyout()
    {
        _flyout ??= new TrayFlyout();
        _flyout.ShowAt();
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

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Lock/unlock and remote-disconnect/connect should freeze the timer
        // — the user is away from the screen so the countdown is meaningless.
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                UIQueue.TryEnqueue(() =>
                {
                    StateMachine.Pause();
                    _flyout?.HideQuiet();
                    HidePopup();
                });
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                UIQueue.TryEnqueue(StateMachine.Resume);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                UIQueue.TryEnqueue(() =>
                {
                    StateMachine.Pause();
                    _flyout?.HideQuiet();
                    HidePopup();
                });
                break;
            case PowerModes.Resume:
                UIQueue.TryEnqueue(StateMachine.Resume);
                break;
        }
    }

    private void ShowOrFocusPopup()
    {
        if (_popup is null)
        {
            _popup = new BreakPopup();
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.BringToFrontAndFocus();
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
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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
