# Windows Customizer

A C# / WPF app that switches complete Windows "looks" (wallpaper, Wallpaper Engine, colors, cursors,
dark/light) and replaces parts of the Windows shell: a customizable taskbar, a start menu, search and,
optionally, the whole shell (instead of Explorer). See [PLAN.md](PLAN.md) for the phases and progress.

## Where things are

| Folder | What's in it |
|---|---|
| (top) | `App.xaml` (app start, crash handlers), project file, `app.manifest` (per-monitor DPI), PLAN.md |
| `Main/` | Main window, settings (`settings.json`), tray icon, global hotkey, the app menu, shared settings form |
| `Themes/` | Profiles (`profiles.json`), applying a profile, accent color, cursor schemes and custom cursors |
| `WallpaperEngine/` | Controlling Wallpaper Engine, game mode, the Workshop wallpaper picker |
| `Taskbar/` | The custom taskbar: bar window, AppBar (reserving screen space), `TaskbarView` (draws a design), modules, designs + designer, monitors, hiding the Windows taskbar, watchdog |
| `Taskbar/WindowList/` | Tracking open windows, grouping by app, thumbnails, icon colors, pinned-app matching |
| `Taskbar/SystemInfo/` | Tray icons (ManagedShell), media sessions, volume, CPU/memory |
| `Launcher/` | Start menu, search window, app index, Windows-key hook, shell icons |
| `Shell/` | Phase 4: running as the shell (instead of Explorer), desktop, startup programs, work area |
| `Native/` | Win32 functions used in several places (`NativeMethods`) |
| `Scripts/` | Recovery and shell scripts (copied next to the .exe when building) |
| `docs/` | Extra notes (Wallpaper Engine integration) |

Everything uses one namespace, `WindowsCustomizer`; the folders are only for finding things.

## Settings and data

`%AppData%\WindowsCustomizer\`: `settings.json`, `profiles.json`, `Taskbars\` (designs), `Cursors\`
(custom cursor copies), `Backups\` (registry backups), logs.

## If something goes wrong

- **Windows taskbar gone:** run `Scripts\Restore-Taskbar.ps1` (the app also restores it itself, even
  when killed, through its watchdog).
- **Using Windows Customizer as the shell and something is wrong:** Ctrl+Alt+Del > Task Manager >
  File > Run new task > `explorer.exe`; then run `Restore-Shell.ps1` (next to the .exe) to make
  Explorer the shell again from the next sign-in.
- **Can't use your own account at all:** sign in to the backup admin account, open PowerShell as
  administrator and run `Restore-Shell.ps1 -ForUser <your user name>`.
