# CHROVIQ

Make Windows look and work the way you want. CHROVIQ replaces and extends parts of the Windows shell, and
everything in it can be customized:

- **Taskbar and dock**: design your own bars (modules, zones, colors, materials), a floating dock, auto-hide, per monitor
- **Start menu, search, Alt+Tab and Quick Settings** of its own, with looks, animations and live glass
- **Shader materials**: glass, aurora, painted and animated surfaces, or write your own (HLSL)
- **Desktop**: widgets (clock, stats, weather, media, notes), snap layouts, a daily wallpaper, Windows 11-style menus
- **Profiles**: switch a whole look (dark/light, accent, wallpaper or Wallpaper Engine, cursors, taskbar) at once, by hand or automatically
- **Shell mode** (optional): run CHROVIQ instead of Explorer

## Made with AI

CHROVIQ is designed and directed by IdkMyNaam and built with the help of AI: the code, documentation and
installer were written together with Anthropic's Claude, through Claude Code. Every feature is tested by hand on
real hardware before it ships.

CHROVIQ is not made by or affiliated with Microsoft. Windows is a trademark of Microsoft.

## Install

Download `CHROVIQ-Setup-<version>.exe` from [Releases](../../releases) and run it. It installs for your
account only (no admin needed). Windows 10 2004 or newer, 64-bit.

The setup isn't signed yet, so the first time Windows SmartScreen may say "Windows protected your PC":
click **More info**, then **Run anyway**.

CHROVIQ looks for updates once a day (you choose when to install). Uninstalling puts Explorer and the
Windows taskbar back and asks whether to keep your settings.

## If something goes wrong

- **Windows taskbar gone:** run `Restore-Taskbar.ps1` (next to the app). The app also restores it itself, even
  when it's killed, through its watchdog.
- **Using CHROVIQ as the shell and something is wrong:** Ctrl+Alt+Del > Task Manager > File > Run new task >
  `explorer.exe`; then run `Restore-Shell.ps1` (next to the app) to make Explorer the shell again from the next sign-in.
- **Can't use your own account at all:** sign in to another admin account, open PowerShell as administrator and
  run `Restore-Shell.ps1 -ForUser <your user name>`.
- **Report a problem:** About page > "Report a problem..." puts the logs and details in a .zip on your desktop.

## Building

.NET 8 SDK, Windows. `dotnet build` builds it; `Scripts\Build-Installer.ps1` makes the setup (needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php)). See [PLAN.md](PLAN.md) for the plan and progress.

| Folder | What's in it |
|---|---|
| (top) | `App.xaml` (app start, crash handlers), project file, `app.manifest` (per-monitor DPI), PLAN.md |
| `Main/` | Main window, settings, tray icon, hotkeys, app themes, setup wizard, backup, logs, updates, translations |
| `Themes/` | Profiles, applying a profile, accent color, cursors, automatic profile switching |
| `WallpaperEngine/` | Controlling Wallpaper Engine, game mode, the Workshop wallpaper picker |
| `Taskbar/` | The custom taskbar: bar window, AppBar, `TaskbarView` (draws a design), modules, designs + designer, watchdog |
| `Taskbar/WindowList/` | Open windows, grouping by app, thumbnails, icon colors, pinned-app matching |
| `Taskbar/SystemInfo/` | Tray icons (ManagedShell), media, volume, CPU/memory, virtual desktops |
| `Launcher/` | Start menu, search, Quick Settings, Alt+Tab, Windows-key hook, opening animations |
| `Materials/` | Shader materials: compiler, effects, live glass, built-in materials |
| `Shell/` | Shell mode, the desktop, widgets, snap layouts, daily wallpaper, startup programs |
| `Native/` | Win32 functions used in several places |
| `Scripts/` | Recovery and shell scripts (copied next to the app), the installer build script |
| `Installer/` | The Inno Setup script |
| `docs/` | Extra notes (Wallpaper Engine integration) |

Settings and data are in `%AppData%\WindowsCustomizer\` (the folder kept its first name so settings carry over).

## License

CHROVIQ is released under the [MIT License](LICENSE).
