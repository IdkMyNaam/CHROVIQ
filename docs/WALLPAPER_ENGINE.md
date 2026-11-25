# Wallpaper Engine integration

CHROVIQ can use [Wallpaper Engine](https://store.steampowered.com/app/431960/Wallpaper_Engine/) as a profile's wallpaper
and pause it automatically while you play games. This document describes how that works.

## Overview

CHROVIQ does not use an API to talk to Wallpaper Engine. It runs Wallpaper Engine's own executable
(`wallpaper64.exe` or `wallpaper32.exe`) with command-line arguments. Wallpaper Engine has to be running to receive
them, so CHROVIQ starts it first when needed.

| Purpose | Arguments |
|---|---|
| Open a wallpaper | `-control openWallpaper -file "<path>\project.json"` |
| Load a Wallpaper Engine profile | `-control openProfile -profile "<name>"` |
| Stop, pause, resume | `-control stop`, `-control pause`, `-control play` |

## Components

- **`Themes/Profile.cs`**: `WallpaperKind` (`Image`, `WallpaperEngineFile`, `WallpaperEngineProfile`) and `WallpaperValue`
  (an image path, a `project.json` path, or a Wallpaper Engine profile name, depending on the kind).
- **`WallpaperEngine/WallpaperEngine.cs`**:
  - Finds the executable through Steam (`SteamPath` / `InstallPath` in the registry, plus the extra libraries in
    `steamapps\libraryfolders.vdf`) in `steamapps\common\wallpaper_engine`. A path chosen in the settings takes precedence.
  - `IsRunning` checks for the `wallpaper64` / `wallpaper32` processes.
  - `EnsureRunningAsync()` starts Wallpaper Engine and waits until it can receive commands.
  - `FindWallpapers()` lists Workshop wallpapers (`steamapps\workshop\content\431960`) and local projects
    (`projects\myprojects`, `projects\defaultprojects`), reading each title from `project.json`.
- **`Themes/ThemeService.cs`**: `ApplyAsync(Profile)` applies dark/light mode, accent and cursors, then the wallpaper.
  For a normal image it stops Wallpaper Engine first, since Wallpaper Engine draws over the Windows wallpaper.
- **`WallpaperEngine/GameWatcher.cs`**: pauses Wallpaper Engine while a game from the list is running and resumes it afterwards.
  It checks when the active window changes, with a 30-second fallback, and only resumes a pause it made itself.
- **`WallpaperEngine/WorkshopPicker.xaml`**: a dialog to choose a wallpaper from `FindWallpapers()`, with a title filter.
- **Settings** (`%AppData%\WindowsCustomizer\settings.json`): the game list, the pause option and an optional executable path.

## Interaction with other features

- **Daily wallpaper** doesn't change the wallpaper while Wallpaper Engine is running, because it would be hidden behind it.
- **Glass materials** show the Windows wallpaper picture behind a surface; with **live glass** they show what's really
  on screen, including a running Wallpaper Engine wallpaper.
- **Desktop widgets** sit just above the desktop, so they appear on top of a Wallpaper Engine wallpaper.

## Known limitations

- The commands are based on Wallpaper Engine's command-line interface; a future Wallpaper Engine version could change them.
- Game detection is by process name. Wallpaper Engine's own "pause when an application is fullscreen" setting may overlap with it.
