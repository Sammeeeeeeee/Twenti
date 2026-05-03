using System;
using System.Diagnostics;
using System.Threading.Tasks;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
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
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = BuildContextMenu(),
            LeftClickCommand = new RelayCommand(OnTrayLeftClick),
        };
        _trayIcon.ForceCreate();

        // Pre-create the flyout so the first tray click is instant —
        // no XAML compilation or window-creation latency on demand.
        _flyout = new TrayFlyout();

        StateMachine.PropertyChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);
        StateMachine.PhaseChanged += OnPhaseChanged;
        StateMachine.BreakCompleted += (_, _) => UIQueue.TryEnqueue(Sound.PlayBreakComplete);
        Theme.ThemeChanged += (_, _) => UIQueue.TryEnqueue(() =>
        {
            RefreshTray();
            // Rebuild the context menu so its theme matches the new system theme.
            if (_trayIcon is not null) _trayIcon.ContextFlyout = BuildContextMenu();
        });

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
        // ContextMenuMode.SecondWindow hosts the flyout in a separate window which
        // doesn't inherit the app theme; set it on each item so the rendered
        // FrameworkElements pick up the right brushes.
        var theme = Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        void Themed(MenuFlyoutItemBase item) => item.RequestedTheme = theme;

        foreach (var mins in new[] { 5, 15, 30 })
        {
            var item = new MenuFlyoutItem { Text = $"Snooze {mins} minutes" };
            item.Click += (_, _) => StateMachine.Snooze(mins);
            Themed(item);
            menu.Items.Add(item);
        }
        var sep1 = new MenuFlyoutSeparator(); Themed(sep1); menu.Items.Add(sep1);

        // ── Monitor placement submenu ──
        var monitorMenu = new MenuFlyoutSubItem { Text = "Show popup on" };
        Themed(monitorMenu);
        var followCursor = new ToggleMenuFlyoutItem
        {
            Text = "The monitor with my cursor",
            IsChecked = Settings.Monitor == MonitorPreference.FollowCursor,
        };
        Themed(followCursor);
        var mainMonitor = new ToggleMenuFlyoutItem
        {
            Text = "Main monitor only",
            IsChecked = Settings.Monitor == MonitorPreference.MainMonitor,
        };
        Themed(mainMonitor);
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
        Themed(muteItem);
        menu.Items.Add(muteItem);

        var autoStart = new ToggleMenuFlyoutItem
        {
            Text = "Start with Windows",
            IsChecked = AutoStart.IsEnabled,
        };
        autoStart.Click += (_, _) => AutoStart.SetEnabled(autoStart.IsChecked);
        Themed(autoStart);
        menu.Items.Add(autoStart);

        var sep2 = new MenuFlyoutSeparator(); Themed(sep2); menu.Items.Add(sep2);

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
        Themed(updateToggle);
        menu.Items.Add(updateToggle);

        var checkNow = new MenuFlyoutItem { Text = "Check for updates now" };
        checkNow.Click += async (_, _) => await CheckForUpdatesAsync(silentIfNone: false);
        Themed(checkNow);
        menu.Items.Add(checkNow);

        var sep3 = new MenuFlyoutSeparator(); Themed(sep3); menu.Items.Add(sep3);

        var quit = new MenuFlyoutItem
        {
            Text = "Quit Twenti",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.IndianRed),
        };
        quit.Click += (_, _) => Quit();
        Themed(quit);
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
        // Window already exists from OnLaunched — just reposition + show.
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
