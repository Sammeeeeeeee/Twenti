<p align="center">
  <img src="icon.svg" width="96" alt="Twenti">
</p>

<h1 align="center">Twenti — 20/20 Eye Break Reminder</h1>

<p align="center">
  <a href="https://github.com/Sammeeeeeeee/Twenti/releases/latest/download/Twenti-Setup.exe"><img alt="Download installer" src="https://img.shields.io/badge/Download-Installer-0067c0?style=for-the-badge&logo=windows&logoColor=white"></a>
  <a href="https://github.com/Sammeeeeeeee/Twenti/releases/latest/download/Twenti.exe"><img alt="Download portable" src="https://img.shields.io/badge/Portable-.exe-3a3a3a?style=for-the-badge&logo=windows&logoColor=white"></a>
  <a href="https://sammeeeeeeee.github.io/Twenti/"><img alt="Website" src="https://img.shields.io/badge/Website-sammeeeeeeee.github.io-60cdff?style=for-the-badge"></a>
</p>

<p align="center">
  <a href="https://github.com/Sammeeeeeeee/Twenti/actions/workflows/build.yml"><img alt="Build" src="https://github.com/Sammeeeeeeee/Twenti/actions/workflows/build.yml/badge.svg"></a>
  <a href="https://github.com/Sammeeeeeeee/Twenti/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Sammeeeeeeee/Twenti?label=latest&color=0067c0"></a>
  <a href="https://github.com/Sammeeeeeeee/Twenti/issues"><img alt="Issues" src="https://img.shields.io/github/issues/Sammeeeeeeee/Twenti?color=3a3a3a"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/Sammeeeeeeee/Twenti?color=3a3a3a"></a>
</p>

A Windows 11 native eye-break reminder. 

## What is the 20/20/20 rule?

The 20/20/20 rule is a [proven technique](https://pubmed.ncbi.nlm.nih.gov/36473088/) to prevent eye strain, dryness and degradation. 

## Twenti

Built with **WinUI 3** (Windows App SDK), this aims to be native and light, to be as unobtrusive as possible, but easily accessible. 
It lives in the tray, with the minutes left as countdown. A flyout on click shows more information. Every 20 minutes, a pop up appears in the centre of your screen. You can choose to delay, or press enter to start the countdown (optional: accompanied by white noise). Every 3rd pop up is for 2 minutes. 

### Keyboard (while the popup is focused)

- `Enter` — start the break timer
- `Esc` — snooze 5 minutes
- `1`–`9` — snooze N minutes

### Right-click on the tray icon

Snooze 5 / 15 / 30 minutes · Mute or Unmute sounds 

### Sounds (mutable)

- Soft 1318 Hz pre-ping 5 seconds before
- Warm 3-note chime when the popup appears
- Brown-noise water ambient during the break
- Rising 3-note resolution when the break completes
- Descending 2-note acknowledgement when snoozed

## Pre-built downloads

- **Recommended → [Releases](../../releases)**.`Twenti.exe` (portable) and `Twenti-Setup.exe` (installer).
- **Latest dev build** — pull from the most recent [Actions run](../../actions). 

## Build & run from source

Prerequisites: **.NET 8 SDK** or newer, **Windows 11** (or Windows 10 ≥ 19041 for Mica).

```pwsh
dotnet restore
dotnet build
dotnet run
```

The app starts straight into the system tray.

## Build a portable single-file exe

```pwsh
dotnet publish -c Release -r win-x64 -o publish
```

Output: `publish\Twenti.exe`. The Windows App SDK runtime is bundled.

## Build the installer

```pwsh
dotnet publish -c Release -r win-x64 -o publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Twenti.iss
```

Output: `installer\Output\Twenti-Setup.exe`. Installs as user or system wide. Adds a Start Menu entry, optionally runs at login.
