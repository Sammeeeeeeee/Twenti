# Twenti — 20/20 Eye Break Reminder

A Windows 11 native eye-break reminder. Lives in the system tray, counts down your work interval, surfaces a calm break prompt, and gets out of the way.

Built with **WinUI 3** (Windows App SDK) — same stack as PowerToys and the Windows 11 Settings app, so it looks and feels truly native (Mica + Acrylic backdrops, official Fluent tokens, real WinUI menus).

## Surfaces

1. **Tray icon** — the only persistent UI. Shows minutes left in green, switches to yellow under 2 min, pulses yellow during the 5-second pre-ping, eye glyph during the break, grey while snoozed.
2. **Tray flyout** (left-click) — acrylic flyout with the live countdown, "Start break now" button, and quick snooze options. Auto-opens during the pre-ping.
3. **Break popup** (when the timer fires) — centered Mica card with two states: prompt (`[Start 20 sec]` / `[Snooze]`) and timer (big 56px countdown with progress bar).

The 3rd break in every cycle is a long 2-minute break instead of the usual 20 seconds.

## Keyboard (while the popup is focused)

- `Enter` — start the break timer
- `Esc` — snooze 5 minutes
- `1`–`9` — snooze N minutes

## Right-click on the tray icon

Snooze 5 / 15 / 30 minutes · Mute or Unmute sounds · Quit

## Sounds (synthesised, no audio files)

- Soft 1318 Hz pre-ping 5 seconds before
- Warm 3-note chime when the popup appears
- Brown-noise water ambient during the break
- Rising 3-note resolution when the break completes
- Descending 2-note acknowledgement when snoozed

All produced live by NAudio — no `.wav` files shipped.

## Build & run from source

Prerequisites: **.NET 8 SDK** or newer, **Windows 11** (or Windows 10 ≥ 19041 for Mica).

```pwsh
dotnet restore
dotnet build
dotnet run
```

The app starts straight into the system tray — no main window appears.

## Build a portable single-file exe

```pwsh
dotnet publish -c Release -r win-x64 -o publish
```

Output: `publish\Twenti.exe`. The Windows App SDK runtime is bundled — the exe runs on a clean Windows 11 box without any prerequisites.

## Build the installer

```pwsh
dotnet publish -c Release -r win-x64 -o publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Twenti.iss
```

Output: `installer\Output\Twenti-Setup.exe`. Installs into Program Files, adds a Start Menu entry, optionally runs at login.

## Pre-built downloads

Every push to `main` builds both the portable exe and the installer in CI.

- **Latest** — pull artifacts from the latest [Actions run](../../actions).
- **Release** — push a `vX.Y.Z` tag, GitHub Actions automatically attaches both files to the new release.

## Project layout

```
Twenti/
├── .github/workflows/build.yml    # CI: portable exe + Inno Setup installer
├── installer/Twenti.iss           # Inno Setup script
├── Twenti.csproj
├── app.manifest                   # PerMonitorV2 DPI awareness
├── Program.cs                     # [STAThread] Main, single-instance mutex
├── App.xaml(.cs)                  # Tray icon + service wiring
├── MainWindow.xaml(.cs)           # Hidden owner window, off-screen
├── Views/
│   ├── BreakPopup.xaml(.cs)       # 380px Mica card popup
│   ├── TrayFlyout.xaml(.cs)       # 240px Acrylic flyout
│   └── ThemedResources.xaml       # Status colour brushes (light/dark)
└── Services/
    ├── BreakStateMachine.cs       # Phase + tick + 3-cycle rhythm
    ├── SoundEngine.cs             # NAudio synthesis (5 cues + ambient)
    ├── TrayIconRenderer.cs        # Runtime ICO generation per state
    └── ThemeListener.cs           # UISettings.ColorValuesChanged hook
```

## License

[MIT](LICENSE)
