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
using Windows.UI.ViewManagement;

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
    private bool _ready;

    // Cached so RefreshTray can run before ThemeListener is initialized
    // (Phase 2). Seeded inline in Phase 1, updated by Theme.ThemeChanged.
    private bool _isDarkCache;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            UIQueue = DispatcherQueue.GetForCurrentThread();

            // === Phase 1: synchronous, fast === paint the real timer icon
            // immediately. Previously this phase only set a static placeholder
            // icon and the user saw an awkward "app icon → blank → timer icon"
            // flicker as Phase 2 caught up. By creating the state machine and
            // renderer here, the very first paint already shows "20" on green.
            _ownerWindow = new MainWindow();
            StateMachine = new BreakStateMachine(UIQueue);
            _iconRenderer = new TrayIconRenderer();
            _isDarkCache = ComputeIsDarkInline();

            var initialIcon = _iconRenderer.Render(StateMachine, _isDarkCache);
            _trayIcon = new TaskbarIcon
            {
                Icon = initialIcon,
                ToolTipText = StateMachine.TooltipText,
                // SecondWindow hosts a real WinUI MenuFlyout — we need this so
                // Click handlers and ToggleMenuFlyoutItem.IsChecked behave
                // normally. PopupMenu mode strips the Click events.
                ContextMenuMode = ContextMenuMode.SecondWindow,
                // Without this, H.NotifyIcon waits ~500ms for a possible
                // double-click before firing the single-click command.
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(OnTrayLeftClick),
            };
            _trayIcon.ForceCreate();

            // Wire icon refresh on every tick BEFORE Phase 2 — the timer keeps
            // updating even if the menu / theme listener / sound aren't ready.
            StateMachine.PropertyChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);
            StateMachine.Start();

            // === Phase 2: deferred at high priority — settings, theme,
            // sound, menu, popup wiring. The icon is already showing the real
            // state by this point.
            UIQueue.TryEnqueue(DispatcherQueuePriority.High, FinishStartup);
        }
        catch (Exception ex)
        {
            // OnLaunched runs on the WinAppSDK loop thread; an exception
            // here would otherwise be swallowed by the message loop and
            // leave the process in a zombie state. Surface and exit.
            ReportFatal("Twenti failed during OnLaunched", ex);
            Environment.Exit(1);
        }
    }

    private void FinishStartup()
    {
        try
        {
            Settings = AppSettings.Load();
            Sound = new SoundEngine { Muted = Settings.Muted };
            Theme = new ThemeListener();
            _isDarkCache = Theme.IsDark;

            if (_trayIcon is not null)
            {
                _trayIcon.ContextFlyout = BuildContextMenu();
            }

            StateMachine.PhaseChanged += OnPhaseChanged;
            StateMachine.BreakCompleted += (_, _) => UIQueue.TryEnqueue(Sound.PlayBreakComplete);
            Theme.ThemeChanged += (_, _) => UIQueue.TryEnqueue(() =>
            {
                _isDarkCache = Theme.IsDark;
                RefreshTray();
                // SecondWindow ContextMenuMode hosts the menu in a separate
                // window that doesn't inherit the app theme — rebuild so the
                // new RequestedTheme on each item picks up the right brushes.
                if (_trayIcon is not null) _trayIcon.ContextFlyout = BuildContextMenu();
            });

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _ready = true;
            RefreshTray();

            // === Phase 3: lowest priority — flyout warmup, update probe.
            UIQueue.TryEnqueue(DispatcherQueuePriority.Low, OnIdleStartup);
        }
        catch (Exception ex)
        {
            // FinishStartup running on the dispatcher queue means an
            // exception here would bypass the Main-thread try/catch in
            // Program.cs entirely. Surface it instead of silently dying.
            ReportFatal("Twenti failed during startup", ex);
        }
    }

    private void OnIdleStartup()
    {
        try
        {
            _flyout = new TrayFlyout();
            _flyout.WarmUp();

            if (Settings.CheckForUpdates)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => CheckForUpdatesAsync(silentIfNone: true));
            }
        }
        catch (Exception ex)
        {
            ReportFatal("Twenti failed initialising the flyout / update check", ex);
        }
    }

    /// <summary>
    /// Quick inline dark-mode probe for Phase 1 — same logic as
    /// ThemeListener.ComputeIsDark, duplicated so the listener doesn't have
    /// to be constructed before the first icon render.
    /// </summary>
    private static bool ComputeIsDarkInline()
    {
        try
        {
            var bg = new UISettings().GetColorValue(UIColorType.Background);
            return bg.R + bg.G + bg.B < 384;
        }
        catch
        {
            return true; // dark is the more common Windows 11 default
        }
    }

    /// <summary>
    /// Resolves the target display area for the popup/flyout, honouring the
    /// user's "Main monitor" / "Follow cursor" preference.
    /// </summary>
    public DisplayArea GetTargetDisplayArea()
    {
        // Settings is null until Phase 2 finishes — fall back to following
        // the cursor (the more useful default) if a popup somehow needs to
        // show before then.
        var pref = Settings?.Monitor ?? MonitorPreference.FollowCursor;
        return pref == MonitorPreference.MainMonitor
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

        // Read-only version readout. Disabled so it can't be clicked, but
        // visible in the menu — gives the user a single place to see what
        // version of Twenti they're running.
        var versionItem = new MenuFlyoutItem
        {
            Text = $"Version {UpdateChecker.CurrentVersion}",
            IsEnabled = false,
        };
        Style(versionItem);
        menu.Items.Add(versionItem);

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
        // H.NotifyIcon's LeftClickCommand fires on the dispatcher thread; run
        // synchronously when we can so ToggleAt() executes BEFORE any hide
        // that the deactivation handler queued in response to the same
        // click. Going through TryEnqueue here put toggles BEHIND that hide
        // and made the click look like it did nothing. Wrap in try/catch
        // because H.NotifyIcon's command invoker doesn't gracefully swallow
        // exceptions, and a throw here would fail-fast the whole app.
        try
        {
            if (UIQueue.HasThreadAccess)
            {
                HandleTrayLeftClick();
            }
            else
            {
                UIQueue.TryEnqueue(HandleTrayLeftClick);
            }
        }
        catch
        {
            // Swallow — clicking the tray icon must never crash the app.
        }
    }

    private void HandleTrayLeftClick()
    {
        // Phase 2 hasn't finished — swallow rather than NRE through
        // StateMachine / Settings / _flyout. The icon is already painting
        // the timer; the user's click will work in a beat.
        if (!_ready) return;
        if (StateMachine.Phase is Phase.Alert or Phase.Break)
        {
            ShowOrFocusPopup();
            return;
        }
        ToggleFlyout();
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
            try
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
            }
            catch (Exception ex)
            {
                // Phase transitions are timer-driven — let one bad transition
                // try to throw to the unhandled-exception handler instead of
                // killing the app, since the timer will tick again next second.
                Debug.WriteLine($"OnPhaseChanged failed: {ex}");
            }
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
        if (_trayIcon is null || _iconRenderer is null || StateMachine is null) return;
        try
        {
            _trayIcon.Icon = _iconRenderer.Render(StateMachine, _isDarkCache);
            _trayIcon.ToolTipText = StateMachine.TooltipText;
        }
        catch
        {
            // GDI hiccups (rare) shouldn't take the whole app down; the
            // next tick will paint a fresh icon.
        }
    }

    public void Quit()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StateMachine?.Stop();
        Sound?.Dispose();
        _trayIcon?.Dispose();
        _popup?.Close();
        _flyout?.Close();
        _ownerWindow?.Close();
        Exit();
    }

    // === Unhandled exception plumbing ====================================
    // Windows App SDK doesn't surface async exceptions by default — they
    // travel through finalizers / the dispatcher and the process just
    // disappears with no UI feedback. Wire each path explicitly so the user
    // gets a message box ("Twenti crashed because…") rather than the silent
    // "thinks for a second and closes" the user reported.

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatal("Twenti hit a XAML exception", e.Exception ?? new Exception(e.Message));
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportFatal("Twenti hit an unhandled exception", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        ReportFatal("Twenti hit an unobserved task exception", e.Exception);
    }

    private static void ReportFatal(string title, Exception ex)
    {
        try
        {
            const uint MB_ICONERROR = 0x00000010;
            string body = $"{title}\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            MessageBoxW(IntPtr.Zero, body, "Twenti", MB_ICONERROR);
        }
        catch
        {
            // If even the message box fails, there's nothing useful left.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
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
