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

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "",
            // PopupMenu uses Win32 TrackPopupMenuEx — auto-sizes to text and
            // repositions correctly near the screen edge (the SecondWindow
            // mode was clipping "Snooze 15 minutes" / "Start with Windows"
            // when the tray was on the right).
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = BuildContextMenu(),
            LeftClickCommand = new RelayCommand(OnTrayLeftClick),
        };
        _trayIcon.ForceCreate();

        // Pre-create the flyout so the first tray click is instant —
        // no XAML compilation or DWM init latency on demand.
        _flyout = new TrayFlyout();

        StateMachine.PropertyChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);
        StateMachine.PhaseChanged += OnPhaseChanged;
        StateMachine.BreakCompleted += (_, _) => UIQueue.TryEnqueue(Sound.PlayBreakComplete);
        Theme.ThemeChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        StateMachine.Start();
        RefreshTray();

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

        foreach (var mins in new[] { 5, 15, 30 })
        {
            var item = new MenuFlyoutItem { Text = $"Snooze {mins} minutes" };
            item.Click += (_, _) => StateMachine.Snooze(mins);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());

        var monitorMenu = new MenuFlyoutSubItem { Text = "Show popup on" };
        var followCursor = new ToggleMenuFlyoutItem
        {
            Text = "The monitor with my cursor",
            IsChecked = Settings.Monitor == MonitorPreference.FollowCursor,
        };
        var mainMonitor = new ToggleMenuFlyoutItem
        {
            Text = "Main monitor only",
            IsChecked = Settings.Monitor == MonitorPreference.MainMonitor,
        };
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
        menu.Items.Add(muteItem);

        var autoStart = new ToggleMenuFlyoutItem
        {
            Text = "Start with Windows",
            IsChecked = AutoStart.IsEnabled,
        };
        autoStart.Click += (_, _) => AutoStart.SetEnabled(autoStart.IsChecked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new MenuFlyoutSeparator());

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
        menu.Items.Add(updateToggle);

        var checkNow = new MenuFlyoutItem { Text = "Check for updates now" };
        checkNow.Click += async (_, _) => await CheckForUpdatesAsync(silentIfNone: false);
        menu.Items.Add(checkNow);

        menu.Items.Add(new MenuFlyoutSeparator());

        var quit = new MenuFlyoutItem { Text = "Quit Twenti" };
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
