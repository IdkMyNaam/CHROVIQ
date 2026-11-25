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
- [x] VM: not used; tested on the main PC in a separate standard account ("ShellTest"), with the safety net above + a second admin account (ask before every risky step)
- [x] Replace `explorer.exe` via the `Shell` value in the Winlogon registry key ("Use as shell" / "Back to Explorer"; the launcher runs through `conhost --headless`, so no window); tested in ShellTest
- [x] Own desktop (icons, right-click menu, draws the wallpaper itself; icons load in the background); tested in ShellTest
- [x] Startup programs (Run keys, RunOnce, Startup folders; respects Task Manager's on/off; started once the shell window is registered); tested in ShellTest
- [x] Work area without Explorer (our bars set it directly); tested in ShellTest
- [ ] Tray icons, notifications (tray via ManagedShell in shell mode works; Windows notifications/Win+A/Win+N need Explorer)
- [x] File browsing: a hidden "Progman" window is registered as the shell window, and explorer.exe is never started directly, so folders open as File Explorer windows without Explorer taking over; tested in ShellTest
- [x] Diagnostics: `%AppData%\WindowsCustomizer\shell.log` (timed steps, startup programs, every Explorer that starts and who started it)
- [ ] Later: use it on the main account
- [ ] Note: Windows 11 makes this harder; expect issues

## Phase 5: A real product people can use
Order: speed first (measure before changing), then the desktop, then the new UI and shader materials, then the installer last (it packages the finished app). The optional extras below are wanted too, after Phase 5.

### 5a. Lighter and faster
- [ ] Measure first: startup time, memory, idle CPU (also with Wallpaper Engine / games running)
- [ ] Fewer timers and polling (watchdogs, stats, window list); event-based where Windows allows it
- [ ] Load only what's used (modules, search index, tray) and load it later where possible
- [ ] Release build with ReadyToRun; check trimming and size

### 5b. Desktop like Windows' own
- [ ] Right-click on the background like Windows 11: View (icon size, auto arrange, align to grid, show icons), Sort by (name, size, type, date modified), Refresh, New (folder, shortcut, the file types Windows offers), Display settings, Personalise, Open in Terminal
- [ ] "Show more options": the full Windows menu, including other apps' entries (Open with Code, Zed, ...)
- [ ] Right-click on icons: the full Windows file menu (Open with, Send to, Cut/Copy, other apps' entries)
- [ ] Multi-select: drag a selection box, Ctrl+click, Shift+click, Ctrl+A; open/delete/copy several items at once
- [ ] Drag and drop: move icons freely (positions saved), drop on folders/Recycle Bin, drag files in and out of File Explorer
- [ ] Cut/Copy/Paste and Undo (Ctrl+X/C/V/Z), like Windows
- [ ] Alt tabing like windows -manualy writen

### 5c. New, professional UI
- [ ] Redesign the main window: sidebar navigation (Themes, Taskbar, Start menu, Search, Desktop, Shell, About), Windows 11 look (rounded, Mica/acrylic, Segoe Fluent icons), dark/light following Windows
- [ ] All settings windows in that same style (taskbar designer, start menu/search settings)
- [ ] Short explanations next to settings; risky options clearly marked

### 5c+. Shader materials (after the new UI, plugs into the redesigned designer)
Every surface (taskbar, tray, search, start menu, menus, desktop) can use a "material": a GPU shader instead of a plain color.
- [ ] Built-in materials: frosted glass (Windows' acrylic blur), tinted glass (blur + color), clear glass (bends the wallpaper behind it; in shell mode we draw the wallpaper ourselves), iridescent/animated glow, painted styles (lines, scribbles, watercolor, from a texture)
- [ ] Own shaders: an HLSL text file (compiled by the app with Windows' own shader compiler) + a small JSON with its settings (colors, sliders like tint, speed, roughness, and textures), shown in the designer automatically
- [ ] Choose a material per surface, with live preview; share materials like taskbar designs (import/export)
- [ ] Inputs for shaders: time (animation), mouse position, surface size, the wallpaper behind it, accent color
- [ ] Safety and speed: a shader that doesn't compile is refused with the error shown; animated shaders pause during games/fullscreen and when nothing changes; a frame-rate limit setting
- [ ] Note: Blender materials can't be imported directly (3D ray tracing vs. flat UI), but their looks can be rebuilt as shaders
- [ ] Note: WPF runs only simple pixel shaders (ps_3_0, one pass); bending OTHER windows behind a bar needs live screen capture (costly)
- [ ] Later: a Direct3D surface for heavier effects (multi-pass blur, real refraction, newer shader versions)

### 5d. More customization everywhere
- [ ] Desktop: icon size, spacing, label font/color/shadow, hide labels, icons on every monitor
- [ ] Taskbar/start menu/search/tray: more look and behavior options (to be listed per area while redesigning)
- [ ] Menus: their colors, corners and font follow the chosen look

### 5e. Installer
- [ ] Installer per user (no admin needed): Start menu shortcut, optional start with Windows, uninstaller
- [ ] The uninstaller turns shell mode off and restores the Windows taskbar first, and keeps or removes the settings (asks)
- [ ] Choose: MSIX (needed later for some Windows features, like reading notifications) or a classic installer (Inno Setup / WiX)
- [ ] Code signing (otherwise Windows SmartScreen warns people), version numbers, release notes
- [ ] Updates: check for a new version and update from the app

## Optional extras
- [ ] Auto-switch profiles by time of day or active app
- [ ] Rainmeter-style desktop widgets (clock, CPU, weather)
- [ ] Import/export themes as JSON to share
- [ ] Profile examples: gaming, school, night
- [ ] First-start wizard: pick a look, taskbar and Windows-key behavior in a few steps
- [ ] Back up / restore all settings in one file (moving to a new PC)
- [ ] Log viewer and "report a problem" (collects the logs) in the app
- [ ] Own Quick Settings panel (Wi-Fi, Bluetooth, volume, brightness, night light), useful in shell mode where Win+A doesn't work
- [ ] Own notification center (needs MSIX, see 5e)
- [ ] Hotkey editor for all shortcuts
- [ ] Window tiling/snap layouts and virtual desktop switching on the taskbar
- [ ] Spotlight-style daily wallpaper in shell mode
- [ ] Languages (translations) and accessibility (keyboard use, screen readers, high contrast)

## Open questions
- Which Windows version is the target (10 or 11)?
- Which settings should a profile control beyond the list above?
- Use a plugin system for widgets later?
