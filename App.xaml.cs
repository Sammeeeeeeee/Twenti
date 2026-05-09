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
    private ContextMenuHost? _contextMenuHost;
    private bool _ready;

    // Cached so RefreshTray can run before ThemeListener is initialized
    // (Phase 2). Seeded inline in Phase 1, updated by Theme.ThemeChanged.
    private bool _isDarkCache;

    // Set when an update is found. Surfaces as a top-of-context-menu
    // "Download update vX.Y.Z" item so the click path is the menu, not the
    // icon — no transient "click reopens release page on every tray click"
    // hijack like the previous LeftClickCommand swap had.
    private string? _pendingUpdateUrl;
    private string? _pendingUpdateVersion;

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
            _isDarkCache = ThemeListener.ComputeIsDarkStatic();

            var initialIcon = _iconRenderer.Render(StateMachine, _isDarkCache);
            _trayIcon = new TaskbarIcon
            {
                Icon = initialIcon,
                ToolTipText = StateMachine.TooltipText,
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(OnTrayLeftClick),
                // We deliberately do NOT set ContextFlyout. H.NotifyIcon's
                // SecondWindow ContextMenuMode (the only one that gives the
                // Fluent look) installs a Win32 SUBCLASSPROC delegate that
                // gets GC'd, leading to COR_E_EXECUTIONENGINE FailFast on
                // unrelated window messages. PopupMenu mode avoids the
                // crash but renders a native Win32 menu.
                //
                // Instead we handle right-click ourselves via
                // RightClickCommand and show the same MenuFlyout inside our
                // own ContextMenuHost window — same Fluent visuals, no
                // library subclass involvement.
                RightClickCommand = new RelayCommand(OnTrayRightClick),
            };
            _trayIcon.ForceCreate();

            StateMachine.PropertyChanged += (_, _) => UIQueue.TryEnqueue(RefreshTray);

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
            Sound.SetOutputDeviceByName(Settings.OutputDeviceName);
            Theme = new ThemeListener();
            _isDarkCache = Theme.IsDark;

            // PhaseChanged must be wired before Start() so we never miss a
            // transition (e.g. if a future config drops WorkMinutes very low).
            StateMachine.PhaseChanged += OnPhaseChanged;
            StateMachine.BreakCompleted += (_, _) => UIQueue.TryEnqueue(Sound.PlayBreakComplete);
            StateMachine.Start();

            Theme.ThemeChanged += (_, _) => UIQueue.TryEnqueue(() =>
            {
                _isDarkCache = Theme.IsDark;
                RefreshTray();
                // The right-click menu rebuilds on each open, so theme
                // changes get picked up automatically — no rebuild here.
            });

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // Wake-on-second-instance: the running app shows a toast
            // explaining where the icon went so users don't think the
            // app is broken.
            SingleInstance.StartListener(() => UIQueue.TryEnqueue(ShowAlreadyRunningToast));

            _ready = true;
            RefreshTray();
            Logger.Info("Twenti ready.");

            // === Phase 3: lowest priority — flyout warmup, update probe.
            UIQueue.TryEnqueue(DispatcherQueuePriority.Low, OnIdleStartup);
        }
        catch (Exception ex)
        {
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
                _ = DelayedFirstUpdateCheckAsync();
            }
        }
        catch (Exception ex)
        {
            ReportFatal("Twenti failed initialising the flyout / update check", ex);
        }
    }

    private async Task DelayedFirstUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await CheckForUpdatesAsync(silentIfNone: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Initial update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the target display area for the popup/flyout, honouring the
    /// user's "Main monitor" / "Follow cursor" preference.
    /// </summary>
    public DisplayArea GetTargetDisplayArea()
    {
        var pref = Settings?.Monitor ?? MonitorPreference.FollowCursor;
        return pref == MonitorPreference.MainMonitor
            ? DisplayArea.Primary
            : Win32Helper.GetCursorDisplayArea();
    }

    private async Task CheckForUpdatesAsync(bool silentIfNone)
    {
        UpdateInfo? info;
        try
        {
            info = await new UpdateChecker().CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            return;
        }

        UIQueue.TryEnqueue(() =>
        {
            if (info is null)
            {
                if (!silentIfNone) ShowToast("You're on the latest version.");
                _pendingUpdateUrl = null;
                _pendingUpdateVersion = null;
                return;
            }

            if (_trayIcon is null) return;

            _pendingUpdateUrl = info.ReleaseUrl;
            _pendingUpdateVersion = info.LatestVersion;
            _trayIcon.ShowNotification(
                title: $"Twenti {info.LatestVersion} is available",
                message: "Right-click the tray icon → \"Download update\" to open the release page.",
                timeout: TimeSpan.FromSeconds(10));
        });
    }

    private void ShowAlreadyRunningToast()
    {
        try
        {
            _trayIcon?.ShowNotification(
                title: "Twenti is already running",
                message: "The tray icon may be hidden. Open the system tray (^ in the taskbar) and drag Twenti onto the visible tray.",
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Logger.Warn($"ShowAlreadyRunningToast failed: {ex.Message}");
        }
    }

    private void ShowToast(string message)
    {
        _trayIcon?.ShowNotification("Twenti", message, timeout: TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Open a URL in the default browser. Restricted to http/https — the
    /// release URL ultimately comes from the GitHub API JSON, and we don't
    /// want a future API quirk (or compromise) to be able to launch
    /// arbitrary protocol handlers via the shell.
    /// </summary>
    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            Logger.Warn($"OpenUrl rejected non-http(s) URL: {url}");
            return;
        }
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"OpenUrl failed: {ex.Message}"); }
    }

    private MenuFlyout BuildContextMenu()
    {
        Logger.Breadcrumb("menu.build");
        var menu = new MenuFlyout();
        var theme = Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        const double MenuMinWidth = 240;
        void Style(MenuFlyoutItemBase item)
        {
            item.RequestedTheme = theme;
            item.MinWidth = MenuMinWidth;
        }

        if (_pendingUpdateUrl is not null && _pendingUpdateVersion is not null)
        {
            var url = _pendingUpdateUrl;
            var update = new MenuFlyoutItem
            {
                Text = $"Download update {_pendingUpdateVersion}",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SteelBlue),
            };
            update.Click += (_, _) => OpenUrl(url);
            Style(update);
            menu.Items.Add(update);
            var sepUpdate = new MenuFlyoutSeparator(); Style(sepUpdate); menu.Items.Add(sepUpdate);
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

        var outputMenu = new MenuFlyoutSubItem { Text = "Sound output" };
        Style(outputMenu);
        var devices = SoundEngine.EnumerateDevices();
        string? current = Sound.CurrentDeviceName ?? Settings.OutputDeviceName;
        foreach (var dev in devices)
        {
            var label = dev.IsDefault ? "System default" : dev.Name;
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = dev.IsDefault
                    ? string.IsNullOrEmpty(current)
                    : string.Equals(current, dev.Name, StringComparison.Ordinal),
            };
            var captured = dev;
            item.Click += (_, _) =>
            {
                string? name = captured.IsDefault ? null : captured.Name;
                Logger.Breadcrumb($"menu.click outputDevice={name ?? "<default>"}");
                Sound.SetOutputDeviceByName(name);
                Settings.OutputDeviceName = name;
                Settings.Save();
            };
            Style(item);
            outputMenu.Items.Add(item);
        }
        menu.Items.Add(outputMenu);

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

        var openLog = new MenuFlyoutItem { Text = "Open log file" };
        openLog.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Warn($"Open log failed: {ex.Message}"); }
        };
        Style(openLog);
        menu.Items.Add(openLog);

        var sep3 = new MenuFlyoutSeparator(); Style(sep3); menu.Items.Add(sep3);

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

    private void OnTrayRightClick()
    {
        Logger.Breadcrumb("tray.rightclick");
        try
        {
            if (UIQueue.HasThreadAccess) ShowContextMenuAtCursor();
            else UIQueue.TryEnqueue(ShowContextMenuAtCursor);
        }
        catch (Exception ex)
        {
            Logger.Error("OnTrayRightClick threw", ex);
        }
    }

    private void ShowContextMenuAtCursor()
    {
        if (!_ready) return;
        try
        {
            _contextMenuHost ??= new ContextMenuHost();
            var pt = Win32Helper.GetCursorPos();
            _contextMenuHost.ShowMenuAt(pt.X, pt.Y, BuildContextMenu());
        }
        catch (Exception ex)
        {
            Logger.Error("ShowContextMenuAtCursor threw", ex);
        }
    }

    // Window in which a click that finds the flyout already-hidden is treated
    // as "the user just clicked to close" rather than "open it again". 400 ms
    // is comfortably longer than the OS deactivation-then-click race (~10 ms)
    // but short enough that an intentional reopen tap still works.
    private const int ToggleGuardMs = 400;

    private void OnTrayLeftClick()
    {
        // Snapshot click-time state synchronously, BEFORE any dispatch hop —
        // this is the ground truth the user is reacting to. By the time the
        // queued handler runs, the OS may have already deactivated the
        // flyout (which sets _isVisible=false), so reading IsVisible inside
        // the handler would lose the user's intent.
        bool wasVisibleAtClick = _flyout?.WasVisibleWithin(ToggleGuardMs) ?? false;
        Logger.Breadcrumb($"tray.leftclick (wasVisible={wasVisibleAtClick})");
        try
        {
            if (UIQueue.HasThreadAccess)
            {
                HandleTrayLeftClick(wasVisibleAtClick);
            }
            else
            {
                UIQueue.TryEnqueue(() => HandleTrayLeftClick(wasVisibleAtClick));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("OnTrayLeftClick threw", ex);
        }
    }

    private void HandleTrayLeftClick(bool wasVisibleAtClick)
    {
        if (!_ready) return;
        if (StateMachine.Phase is Phase.Alert or Phase.Break)
        {
            ShowOrFocusPopup();
            return;
        }
        if (wasVisibleAtClick)
        {
            // Either currently visible OR closed within the last
            // ToggleGuardMs ms. In both cases the user's intent is "close",
            // so HideQuiet (idempotent if already hidden — just consumes the
            // click and avoids the auto-hide-then-reopen race).
            _flyout?.HideQuiet();
        }
        else
        {
            _flyout ??= new TrayFlyout();
            _flyout.ShowAt();
        }
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
                Logger.Error("OnPhaseChanged failed", ex);
            }
        });
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
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
        catch (Exception ex)
        {
            // GDI hiccups (rare) shouldn't take the whole app down; the
            // next tick will paint a fresh icon.
            Logger.Warn($"RefreshTray failed: {ex.Message}");
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
        _contextMenuHost?.Close();
        _ownerWindow?.Close();
        SingleInstance.Shutdown();
        Exit();
    }

    // === Unhandled exception plumbing ====================================

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
        Logger.Error(title, ex);
        try
        {
            const uint MB_ICONERROR = 0x00000010;
            string body = $"{title}\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}\n\nLog file:\n{Logger.LogPath}";
            MessageBoxW(IntPtr.Zero, body, "Twenti", MB_ICONERROR);
        }
        catch
        {
            // Last-ditch — nothing else to do.
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
