using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WindowsCustomizer;

/// <summary>A system-wide keyboard shortcut (works while the app is hidden), using RegisterHotKey.</summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000; // holding the keys down fires once, not repeatedly

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Each hotkey has its own id: Windows tells which one was pressed by it (several can share a window).
    private static int _nextId = 0x5743;
    private readonly int Id = Interlocked.Increment(ref _nextId);

    private readonly HwndSource _source;
    private readonly bool _registered;

    public event Action? Pressed;

    /// <summary>True if Windows accepted the hotkey. False usually means another program already uses it.</summary>
    public bool IsRegistered => _registered;

    public ModifierKeys Modifiers { get; }
    public Key Key { get; }

    /// <param name="window">Receives the hotkey messages. Hiding the window is fine; closing it ends the hotkey.</param>
    public GlobalHotKey(Window window, ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);

        // WPF's ModifierKeys values (Alt=1, Control=2, Shift=4, Windows=8) match the MOD_* flags.
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(hwnd, Id, (uint)modifiers | MOD_NOREPEAT, vk);
    }

    /// <summary>For example "Ctrl+Alt+P".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(WndProc);
    }
}
