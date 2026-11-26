# CHROVIQ Roadmap

CHROVIQ is a C# / WPF application for Windows that lets you reshape the desktop: switch complete looks in one go,
replace the taskbar, start menu, search, Alt+Tab and Quick Settings with fully customizable versions, decorate
every surface with GPU shader materials, and optionally run as the Windows shell instead of Explorer.

This document records the decisions behind the project and tracks progress. Checked items are finished;
unchecked items are planned or open.

CHROVIQ is developed with the help of AI (Anthropic's Claude, through Claude Code).

## Principles

- **WPF on .NET 8.** More mature than WinUI 3 for custom taskbar and overlay windows, and the AppBar API is
  straightforward to use from it.
- **Built in phases.** Each phase works on its own before the next one starts, and is tested on real hardware.
- **Safety first.** Anything that touches the shell or system settings can always be undone.
- **Measure before optimizing.** Performance work starts from numbers, not guesses.

## Safety rules

- A recovery script (`Restore-Shell.ps1`, `Restore-Taskbar.ps1`) is always available before the shell is touched.
- Shell replacement is tested in a separate standard account, with a second administrator account as a fallback.
- Registry keys are backed up (exported to .reg) before they are changed; changes are per user (HKCU) only.
- If the app stops responding or is closed, a watchdog brings back the Windows taskbar and tray icons.

## Phase 1: Theme engine
- [x] Profiles: name, wallpaper, accent color, dark/light mode, cursor scheme, saved as JSON
- [x] Applying wallpapers (`SystemParametersInfo`), accent color and dark/light mode (registry), cursor schemes
- [x] Settings UI to create, edit and delete profiles
- [x] Tray icon and a global hotkey to cycle profiles

## Phase 2: Custom taskbar and dock
- [x] A bar that reserves screen space through the AppBar API (`SHAppBarMessage`)
- [x] Open windows with icons; click to focus, minimize or restore
- [x] Clock and system tray area
- [x] Optionally hide the Windows taskbar (restored on exit or crash; `Restore-Taskbar.ps1` as a fallback)
- [x] A bar on every monitor (window buttons on all bars, or only on the window's own monitor)

## Phase 2b: Fully customizable taskbar
- [x] Modules: menu and start buttons, window list, pinned apps, clock, battery, Quick Settings, spacer, text, custom buttons,
      show desktop, Windows search, search box, volume, CPU/memory, media controls, tray icons, virtual desktops
- [x] Left, center and right zones; designs saved as JSON (`%AppData%\WindowsCustomizer\Taskbars`) and reloaded on change
- [x] Look: height, colors, transparency and blur, corners, fonts, icon-only or icon and title, window icon size,
      active-window marker, border, shadow and zone dividers
- [x] Floating dock style, auto-hide, screen edge per design, a different design per monitor
- [x] Window grouping by app, live thumbnails, middle-click to close, dock-style pinned apps, tinted icons, icon-color glow, hover fade
- [x] Taskbar designer with a live preview; designs can be linked to profiles and imported/exported
- [x] Widgets as plugins: web widgets (HTML, CSS, JavaScript) with live data, allowed addresses, chosen files and
      approved commands only; import and export as .zip; three examples and a guide (Widgets/README.md)
- [x] Live glass for widgets; previews of minimized windows in the taskbar and Alt+Tab
- [ ] Web widgets: right-click and dragging inside the page (so far only on the widget's edge)

## Phase 3: Start menu and search
- [x] Installed apps from Start Menu shortcuts and the shell Apps folder (including Store apps, with their own icons)
- [x] Start menu with pinned apps (pin, unpin, reorder, pin to taskbar, open file location, run as administrator)
      and an alphabetical list; a separate search window for apps and the web
- [x] Power options (lock, sleep, sign out, restart, shut down; the last three ask first)
- [x] Opened from the Start module, the Windows key or the app menu
- [x] Recent files (from Windows' own list) and folder buttons next to power (Windows' folders or your own)

## Phase 4: Shell replacement
- [x] Recovery first: `Restore-Shell.ps1` (with a dry run), .reg backups, a launcher that starts Explorer whenever the app ends
- [x] Replacing Explorer through the per-user Winlogon `Shell` value ("Use as shell" / "Back to Explorer")
- [x] Own desktop: icons, right-click menus, wallpaper
- [x] Startup programs (Run keys, RunOnce, Startup folders; respects Task Manager's on/off setting)
- [x] Work area handling without Explorer
- [x] File browsing: a hidden shell window is registered, so folders open in File Explorer without Explorer taking over the shell
- [x] Diagnostics: `shell.log` with timed steps and every Explorer start
- [x] Win+A (CHROVIQ's Quick Settings) and Win+N (a calendar panel, also from the clock) as the shell
- [ ] A list of other apps' notifications as the shell (needs MSIX packaging; the pop-ups themselves do appear)
- [ ] Use as the shell on a main account (tested so far in a dedicated test account)

## Phase 5: Ready for everyday use

### 5a. Performance
- [x] Measured first: about 1.3-2 s to the first window, about 0.5 % of one core when idle, about 100 MB of private memory
- [x] Fewer timers and less polling; event-based wherever Windows allows it
- [x] Release build with ReadyToRun
- [x] Startup measured step by step (logged in trace.log) and halved, from about 3.4 s to 1.75 s: four places that waited
      on Explorer or on Windows (hiding the Windows taskbar, the watchdog helper, the keyboard hooks, repeated bar
      position updates) no longer hold up the start
- [x] Memory investigated: the app's own data is about 12.5 MB with no leaks; the rest (about 110 MB in total) is .NET,
      WPF and the graphics driver. The app list, search index and icons already load only when first used
- [x] Package size: about 59 MB with .NET included. Trimming isn't supported for WPF apps; ReadyToRun adds size but keeps startup fast

### 5b. Desktop on par with Windows
- [x] Desktop preview: the shell desktop in a normal window, to try everything without switching shells
- [x] Windows 11-style right-click menus (View, Sort by, Refresh, New, Display settings, Personalize, Open in Terminal),
      plus "Show more options" with the full classic menu and other apps' entries
- [x] Multi-select, drag and drop, Cut/Copy/Paste and Undo (including restoring from the Recycle Bin)
- [x] Windows' own desktop icons (Recycle Bin, This PC, ...)
- [x] Own Alt+Tab switcher with live previews
- [x] "Text Document" always in New
- [ ] Later: the classic menu for items from both desktop folders at once

### 5c. Modern user interface
- [x] Main window redesigned with WPF-UI: sidebar navigation, Mica, rounded corners, Segoe Fluent icons, light/dark following Windows
- [x] All settings windows in the same style, with short explanations and clearly marked risky options
- [x] App themes (Windows, Dark glass, Midnight, Light clean, High contrast) and custom themes (mode, accent, background,
      corners, card transparency, font, material) with a live editor and import/export
- [x] Six more built-in themes (Retro, Ocean, Forest, Sunset, Nord, Paper) and theme fields for text and border colors

### 5c+. Shader materials
Every surface (taskbar, start menu, search, Alt+Tab, Quick Settings, menus, widgets) can use a material: a GPU pixel shader.
- [x] Built-in materials: Tinted glass, Clear glass, Iridescent, Aurora, Painted lines, Scribbles, Watercolor, Mouse glow
- [x] Custom materials: `material.hlsl` (compiled at runtime to ps_3_0) and `material.json` (up to 8 parameters and a texture),
      with settings generated automatically; import/export as .zip
- [x] Shader inputs: time, size, mouse, hover, accent color, parameters, texture, helper functions, and the wallpaper behind the surface
- [x] Live glass: what's really on screen behind a surface (that window is then left out of screen capture)
- [x] Safety and speed: compile errors shown with line numbers; animations pause for fullscreen apps and hidden windows; 15/30/60 fps limit
- [ ] Later: a Direct3D surface for heavier effects (multi-pass blur, refraction, newer shader models)

### 5d. Customization everywhere
- [x] Desktop icons: size, spacing, label font, color and shadow, hidden labels, choice of monitor
- [x] Menus, Alt+Tab, start menu, search and tray: colors, sizes, fonts, layout, scroll bars and materials
- [x] Opening animations for popups: presets or fully custom (fade, slide, zoom, easing, duration)
- [x] Settings windows organized in sections, in the style of the taskbar designer
- [x] Icons on every monitor at once (drag them between monitors)
- [x] Animations for menus, the tray flyout, window previews and auto-hide

### 5e. Installer and releases
- [x] Per-user installer (Inno Setup, no administrator rights), with Start menu shortcut, optional start at sign-in and desktop shortcut;
      .NET is included (about 59 MB)
- [x] The uninstaller restores Explorer and the Windows taskbar first, then asks whether to keep the settings
- [x] Versioning, and `Scripts\Build-Installer.ps1` to publish and package a release
- [x] Update check against GitHub Releases (daily, installs only when the user agrees)
- [ ] Code signing (until then, Windows SmartScreen asks for confirmation on first run)

## Extras
- [x] Automatic profile switching by time of day or running app
- [x] Desktop widgets: clock, CPU/memory/GPU with graphs, weather (Open-Meteo), media, notes
- [x] Example profiles (Gaming, School, Night) and a first-start setup wizard
- [x] Back up and restore all settings in one file
- [x] Log viewer and "Report a problem" (logs and system details in a .zip on the desktop; nothing is sent)
- [x] Quick Settings panel: Wi-Fi, Bluetooth, night light, mute, volume, per-monitor brightness (DDC/CI), battery
- [x] Keyboard shortcut editor for every shortcut in the app
- [x] Snap layouts (layout picker, Shift-drag zones, hotkeys, custom layouts) and a virtual desktops taskbar module
- [x] Daily wallpaper from Bing, Windows Spotlight or a folder
- [x] Accessibility: keyboard navigation, screen reader names, Windows contrast themes
- [x] Translation support: language picker and translation templates
- [x] Translations: Dutch, German, French, Spanish, Chinese, Japanese and Korean
- [x] Laptop screen brightness in Quick Settings
- [ ] A notification center (requires MSIX packaging)
- [x] Problem reports sent to the developer through a relay (the webhook is never in the app), a Help and support page,
      and a support button (Ko-fi)
- [x] A welcome screen right after signing in as the shell, and options for Windows' lock and sign-in screen

## Open questions
- Which further settings should a profile control?
