using System.Windows;
using System.Windows.Input;

namespace WindowsCustomizer;

/// <summary>Everything a keyboard shortcut can do.</summary>
public enum HotkeyAction
{
    NextProfile, OpenStartMenu, OpenSearch, OpenQuickSettings, OpenCustomizer,
    ToggleTaskbar, ToggleWidgets, NextWallpaper,
    SnapPrevious, SnapNext, SnapPicker,
}

/// <summary>A key with modifiers, like Ctrl+Alt+P. <see cref="None"/> is "no shortcut".</summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    public static readonly Shortcut None = new(ModifierKeys.None, Key.None);
    public bool IsNone => Key == Key.None;

    /// <summary>A shortcut needs a modifier (a plain letter would take that key from every program).</summary>
    public bool IsUsable => !IsNone && Modifiers != ModifierKeys.None && Modifiers != ModifierKeys.Shift;

    public override string ToString()
    {
        if (IsNone) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=",
        _ => key.ToString(),
    };

    /// <summary>Reads "Ctrl+Alt+P" (as written by <see cref="ToString"/>); empty or bad text is <see cref="None"/>.</summary>
    public static Shortcut Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) key = Key.D0 + (part[0] - '0');
                    else if (part == ",") key = Key.OemComma;
                    else if (part == ".") key = Key.OemPeriod;
                    else if (part == "-") key = Key.OemMinus;
                    else if (part == "=") key = Key.OemPlus;
                    else if (Enum.TryParse<Key>(part, true, out var k)) key = k;
                    else return None;
                    break;
            }
        }
        return new Shortcut(modifiers, key);
    }
}

/// <summary>
/// All of Windows Customizer's own keyboard shortcuts: their names, defaults, your changes (AppSettings.Hotkeys),
/// and registering them with Windows. The hotkey editor changes them; menus show them.
/// </summary>
internal sealed class Hotkeys : IDisposable
{
    public sealed record Info(HotkeyAction Action, string Group, string Name, Shortcut Default);

    public static readonly IReadOnlyList<Info> All = new Info[]
    {
        new(HotkeyAction.NextProfile, "Profiles", "Switch to the next profile", new(ModifierKeys.Control | ModifierKeys.Alt, Key.P)),
        new(HotkeyAction.OpenStartMenu, "Open", "Open the start menu", Shortcut.None),
        new(HotkeyAction.OpenSearch, "Open", "Open search", Shortcut.None),
        new(HotkeyAction.OpenQuickSettings, "Open", "Open Quick Settings", Shortcut.None),
        new(HotkeyAction.OpenCustomizer, "Open", "Open Windows Customizer", Shortcut.None),
        new(HotkeyAction.ToggleTaskbar, "Show and hide", "Show or hide the custom taskbar", Shortcut.None),
        new(HotkeyAction.ToggleWidgets, "Show and hide", "Show or hide the desktop widgets", Shortcut.None),
        new(HotkeyAction.NextWallpaper, "Show and hide", "Next daily wallpaper", Shortcut.None),
        new(HotkeyAction.SnapPrevious, "Snap (when snap layouts are on)", "Window to the previous zone", new(ModifierKeys.Windows | ModifierKeys.Alt, Key.Left)),
        new(HotkeyAction.SnapNext, "Snap (when snap layouts are on)", "Window to the next zone", new(ModifierKeys.Windows | ModifierKeys.Alt, Key.Right)),
        new(HotkeyAction.SnapPicker, "Snap (when snap layouts are on)", "Layout picker for the active window", new(ModifierKeys.Windows | ModifierKeys.Alt, Key.Z)),
    };

    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly Dictionary<HotkeyAction, Action> _handlers;
    private readonly Func<HotkeyAction, bool> _active;
    private readonly List<GlobalHotKey> _registered = new();

    /// <summary>Shortcuts Windows refused (another program has them), after the last <see cref="Apply"/>.</summary>
    public HashSet<HotkeyAction> Taken { get; } = new();

    /// <param name="active">Whether an action's shortcut should be registered now (snap ones only while snap is on).</param>
    public Hotkeys(Window window, AppSettings settings, Dictionary<HotkeyAction, Action> handlers, Func<HotkeyAction, bool> active)
    {
        _window = window;
        _settings = settings;
        _handlers = handlers;
        _active = active;
    }

    /// <summary>The shortcut in use for an action: your choice, or the default.</summary>
    public Shortcut Get(HotkeyAction action) =>
        _settings.Hotkeys.TryGetValue(action.ToString(), out var text) ? Shortcut.Parse(text)
        : All.First(i => i.Action == action).Default;

    public void Set(HotkeyAction action, Shortcut shortcut)
    {
        var standard = All.First(i => i.Action == action).Default;
        if (shortcut == standard) _settings.Hotkeys.Remove(action.ToString());
        else _settings.Hotkeys[action.ToString()] = shortcut.ToString(); // "" = turned off
    }

    /// <summary>(Re)registers every shortcut. Returns a status line when some couldn't be registered, else null.</summary>
    public string? Apply()
    {
        Release();
        Taken.Clear();
        foreach (var info in All)
        {
            var shortcut = Get(info.Action);
            if (!shortcut.IsUsable || !_active(info.Action) || !_handlers.TryGetValue(info.Action, out var handler)) continue;
            var hotkey = new GlobalHotKey(_window, shortcut.Modifiers, shortcut.Key);
            if (!hotkey.IsRegistered) { Taken.Add(info.Action); hotkey.Dispose(); continue; }
            hotkey.Pressed += handler;
            _registered.Add(hotkey);
        }
        return Taken.Count == 0 ? null
            : $"Already used by another program, so they do nothing here: {string.Join(", ", Taken.Select(a => Get(a).ToString()))}. Change them in Keyboard shortcuts.";
    }

    /// <summary>Lets go of all shortcuts (while one is being typed in the editor, so it isn't run).</summary>
    public void Release()
    {
        foreach (var hotkey in _registered) hotkey.Dispose();
        _registered.Clear();
    }

    public void Dispose() => Release();
}
