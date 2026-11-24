# Wallpaper Engine integration: notes for Claude Code

Context for the `WindowsCustomizer` project (C#, WPF, .NET 8, Windows only). Read `PLAN.md` first for the overall vision. This file explains only the Wallpaper Engine (WE) part.

## Goal
Each theme profile can set the desktop wallpaper through Wallpaper Engine instead of (or in addition to) a normal image, and WE can be paused while games run.

## How it works
The app does not talk to WE through an API. It starts WE's own executable (`wallpaper64.exe` or `wallpaper32.exe`) with command-line arguments. WE has to be running to receive them, so the app starts it first if needed.

Commands used (written from memory, **verify them against the current Wallpaper Engine documentation and fix `WallpaperEngine.cs` if a command or argument differs**):

| Purpose | Arguments |
|---|---|
| Open a wallpaper | `-control openWallpaper -file "<path>\project.json"` |
| Load a WE profile | `-control openProfile -profile "<name>"` |
| Stop / pause / resume | `-control stop`, `-control pause`, `-control play` |

## Files (all in the project root)
- `Profile.cs`: `WallpaperKind` enum (`Image`, `WallpaperEngineFile`, `WallpaperEngineProfile`) and `WallpaperValue` (image path, project.json path, or WE profile name depending on the kind).
- `WallpaperEngine.cs`: static helper.
  - Finds the exe via Steam: registry `SteamPath` / `InstallPath`, plus extra libraries parsed from `steamapps\libraryfolders.vdf`, looking in `steamapps\common\wallpaper_engine`. A saved path in settings wins if it exists.
  - `IsRunning` checks processes `wallpaper64` / `wallpaper32`.
  - `EnsureRunningAsync()` starts the exe and waits a fixed 4 seconds (crude, could be improved).
  - `FindWallpapers()` lists wallpapers from `steamapps\workshop\content\431960\*` (431960 is WE's Steam app id) and from `wallpaper_engine\projects\myprojects` and `defaultprojects`, reading the `title` from each `project.json`.
- `ThemeService.cs`: `ApplyAsync(Profile)` sets dark/light mode, then handles the wallpaper by kind. For `Image` it stops WE first (WE draws over the normal wallpaper), waits 700 ms, then calls `SystemParametersInfo`.
- `GameWatcher.cs`: a `DispatcherTimer` every 5 seconds. If any process name from the settings is running, it pauses WE; when none are, it resumes.
- `AppSettings.cs`: JSON settings in `%AppData%\WindowsCustomizer\settings.json` (game process list, pause on/off, saved WE exe path).
- `WorkshopPicker.xaml(.cs)`: dialog to pick a wallpaper from `FindWallpapers()` with a title filter.
- `MainWindow.xaml(.cs)`: wallpaper type dropdown, value box, browse button, apply button, game mode section.

## Known weak points and ideas
- The code has not been compiled or tested on a real WE install yet. Build first and fix errors.
- The 4 second wait after starting WE is a guess. Better: poll until WE responds or the process is up.
- The 700 ms wait after `stop` before setting an image is also a guess.
- Profiles are only in memory (lost on close). Next planned step: save and load profiles as JSON.
- Game detection is by process name only. WE also has its own pause-on-fullscreen setting, which may overlap.
- Error handling is minimal: failures from starting WE are mostly silent. Consider checking exit codes and logging.

## Working agreement with the user
- Always ask for confirmation before changing any file, even when asked to.
- Keep answers short and direct.
- Suggest extra ideas, but clearly label them as optional.
- Phases from `PLAN.md` are built one at a time. Do not touch shell replacement (Phase 4) without the safety rules in `PLAN.md`.
