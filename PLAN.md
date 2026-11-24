# Windows Customizer: Project Plan

## Vision
A C# / WPF app that switches between full Windows "themes" (wallpaper, colors, cursor, dark/light) and eventually replaces parts of the Windows shell (taskbar, start menu, maybe Explorer itself).

## Decisions log
- **Framework: WPF (.NET)**. More mature than WinUI 3 for custom taskbar/overlay windows; the AppBar API is easier to use from it.
- **Language: C#** (matches school NATIVE track).
- **Approach:** build in phases. Each phase must work on its own before the next starts.
- **Source control:** git from day one.

## Safety rules (read before Phase 4)
- Always keep a "Restore Explorer" script ready before touching the shell.
- Keep a second admin account as a fallback.
- Test shell replacement in a VM first, never on the main PC first.
- Back up registry keys before changing them (export to .reg).

## Phase 1: Theme engine
- [x] Create WPF project + git repo
- [x] Profile model (name, wallpaper, accent color, dark/light, cursor)
- [x] Apply wallpaper via `SystemParametersInfo`
- [x] Apply accent color + dark/light mode via registry
- [x] Apply cursor scheme
- [x] Settings UI to create/edit/delete profiles
- [x] Tray icon + global hotkey to cycle profiles
- [x] Save/load profiles as JSON

## Phase 2: Custom taskbar / dock
- [x] Window that reserves screen space via `SHAppBarMessage` (AppBar API)
- [x] List open windows (EnumWindows) with icons
- [x] Click to focus / minimize / restore
- [x] Clock + basic system tray area
- [x] Native taskbar stays running underneath (low risk)
- [x] Option to hide the native taskbar (auto-hide + hidden, restored on exit/crash; `Restore-Taskbar.ps1` as fallback)
- [x] A bar on every monitor (window buttons on all bars, or only on the window's own monitor)

## Phase 2b: Fully customizable taskbar
Goal: design your own taskbar instead of using a fixed layout.
- [x] Split the bar into modules: menu/start button, window list, pinned apps, clock, battery, Quick Settings, spacer, custom button/text
- [x] Choose which modules show, their order, and zone (left / center / right), in the designer or the JSON files
- [x] Save a taskbar design as JSON (`%AppData%\WindowsCustomizer\Taskbars`), reloaded when the file changes
- [x] Look: height, colors, transparency/blur, corner radius, font, icon-only or icon + title (blur is experimental, not yet with the floating dock)
- [x] "Floating dock" style (margins from the screen edge, rounded, centered; reserves space or floats over windows)
- [x] Link a design to a profile (applying the profile switches the taskbar design)
- [x] Taskbar designer window with a live preview (live updates can be switched off; "Apply to taskbar")
- [x] Import/export designs as .json files (ties in with "Import/export themes" below)
- [x] Extras: auto-hide, screen edge per design, a design per monitor
- [x] Extras: window buttons grouped by app, live thumbnails, middle-click close, dock-style pinned apps, tinted icons, icon-color glow, hover fade
- [x] Extras: modules for show desktop, Windows search, search box, volume, CPU/memory, media controls, tray icons (ManagedShell)
- [x] Extras: search window (also on the Windows key) with its own look settings; watchdog restores the taskbar if the app is killed
- [ ] Later: own widgets as plugins (see open questions)

## Phase 3: Custom start menu / launcher
- [x] Read installed apps (Start Menu shortcuts + the shell Apps folder, so Store apps too, with their real icons)
- [x] Popup window with search (start menu, plus a separate search window)
- [x] Launch apps, pinned favorites (pin/unpin/reorder; pin to taskbar; open file location; run as administrator)
- [x] Power options (lock, sleep, sign out, restart, shut down; the last three ask first)
- [x] Open it from the Start module, the Windows key or the app menu; look settings in "Start menu settings..."
- [ ] Later: recent files, folders of pinned apps

## Phase 4: True shell replacement
- [x] Recovery first: `Restore-Shell.ps1` (with -DryRun), .reg backups, per-user change only (HKCU), `ShellLauncher.ps1` starts Explorer whenever the app ends
- [ ] VM: not used; testing on the main PC with the safety net above + a second admin account (ask before every risky step)
- [ ] Replace `explorer.exe` via the `Shell` value in the Winlogon registry key (built: "Use as shell" / "Back to Explorer"; real test pending)
- [ ] Own desktop (icons, right-click menu) (built and rendered; real test as shell pending)
- [ ] Startup programs (Run keys, RunOnce, Startup folders; respects Task Manager's on/off) (built, listed; real start pending)
- [ ] Work area without Explorer (our bars set it directly) (built; real test pending)
- [ ] Tray icons, notifications (tray via ManagedShell in shell mode; Windows notifications/Win+A/Win+N need Explorer)
- [ ] File browsing (our desktop registers as the shell window, so explorer.exe only opens file windows) (real test pending)
- [ ] Note: Windows 11 makes this harder; expect issues

## Optional extras
- [ ] Auto-switch profiles by time of day or active app
- [ ] Rainmeter-style desktop widgets (clock, CPU, weather)
- [ ] Import/export themes as JSON to share
- [ ] Profile examples: gaming, school, night

## Open questions
- Which Windows version is the target (10 or 11)?
- Which settings should a profile control beyond the list above?
- Use a plugin system for widgets later?
