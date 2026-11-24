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
- [ ] Create WPF project + git repo
- [ ] Profile model (name, wallpaper, accent color, dark/light, cursor)
- [ ] Apply wallpaper via `SystemParametersInfo`
- [ ] Apply accent color + dark/light mode via registry
- [ ] Apply cursor scheme
- [ ] Settings UI to create/edit/delete profiles
- [ ] Tray icon + global hotkey to cycle profiles
- [ ] Save/load profiles as JSON

## Phase 2: Custom taskbar / dock
- [ ] Window that reserves screen space via `SHAppBarMessage` (AppBar API)
- [ ] List open windows (EnumWindows) with icons
- [ ] Click to focus / minimize / restore
- [ ] Clock + basic system tray area
- [ ] Native taskbar stays running underneath (low risk)

## Phase 3: Custom start menu / launcher
- [ ] Read installed apps (Start Menu shortcuts)
- [ ] Popup window with search
- [ ] Launch apps, pinned favorites
- [ ] Power options (sleep, restart, shut down)

## Phase 4: True shell replacement
- [ ] Set up VM + recovery script first
- [ ] Replace `explorer.exe` via the `Shell` value in the Winlogon registry key
- [ ] Own desktop (icons, right-click menu)
- [ ] Tray icons, notifications
- [ ] File browsing (fallback: launch Explorer windows on demand)
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
