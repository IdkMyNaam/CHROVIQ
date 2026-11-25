using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// Takes over Alt+Tab (and Alt+Shift+Tab, Ctrl+Alt+Tab) so our switcher shows instead of Windows' own.
/// While the switcher is open it also gets the arrow keys, Enter, Delete and Esc. Uses a low-level keyboard
/// hook on its own thread (<see cref="KeyboardHookThread"/>), which Windows removes automatically when the app
/// ends, so Alt+Tab can never stay "taken" after a crash.
/// All events are raised on the hook's thread: handlers must return at once (hand the work to the UI thread).
/// </summary>
internal sealed class AltTabHook : IDisposable
{
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_DELETE = 0x2E, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const uint LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;
    private const ushort VK_DUMMY = 0xE8; // unassigned: tells Windows "Alt was used with a key", so apps don't open their menu bar

    public enum SwitcherKey { Left, Right, Up, Down, Delete }

    /// <summary>Alt+Tab pressed: open (backwards = Shift held; sticky = Ctrl+Alt+Tab, stays open after Alt is released).</summary>
    public event Action<bool, bool>? Opened;
    /// <summary>Tab again while open (backwards = Shift held).</summary>
    public event Action<bool>? Stepped;
    /// <summary>Alt released (or Enter): switch to the selected window.</summary>
    public event Action? Committed;
    /// <summary>Esc: close without switching.</summary>
    public event Action? Cancelled;
    public event Action<SwitcherKey>? KeyPressed;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; private readonly long padding; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private readonly KeyboardHookThread _hook;
    // Read and written by the hook's thread and (Close) the UI thread.
    private volatile bool _open, _sticky;

    /// <summary>Whether the switcher is open (the hook takes the navigation keys).</summary>
    public bool IsOpen => _open;

    public AltTabHook() => _hook = new KeyboardHookThread("Alt+Tab hook", OnKey);

    /// <summary>The switcher was closed by other means (a click): stop taking the navigation keys.</summary>
    public void Close() { _open = false; _sticky = false; }

    private static bool Held(uint key) => GetAsyncKeyState((int)key) < 0;

    private bool OnKey(int msg, in KeyboardHookThread.KeyInfo key)
    {
        if ((key.flags & LLKHF_INJECTED) != 0) return false; // our own dummy key
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = msg is WM_KEYUP or WM_SYSKEYUP;
        bool alt = (key.flags & LLKHF_ALTDOWN) != 0 || Held(VK_MENU);

        if (down && key.vkCode == VK_TAB && (alt || _open))
        {
            bool backwards = Held(VK_SHIFT);
            if (!_open)
            {
                _open = true;
                _sticky = Held(VK_CONTROL);
                // Not from inside the hook: Windows runs the sent key through the hooks too (this one included,
                // which is busy right now), and a hook that answers late is skipped, so Tab would reach
                // Windows' own Alt+Tab. Sent a moment later instead; Alt is released long after that.
                ThreadPool.UnsafeQueueUserWorkItem(_ => SendDummyKey(), null);
                Opened?.Invoke(backwards, _sticky);
            }
            else Stepped?.Invoke(backwards);
            return true; // Windows' own Alt+Tab doesn't see it
        }

        if (!_open) return false;
        bool altKey = key.vkCode is VK_MENU or VK_LMENU or VK_RMENU;
        if (up && altKey && !_sticky)
        {
            _open = false;
            Committed?.Invoke();
            return false; // let the Alt release through: apps must see Alt go up
        }
        if (down && key.vkCode == VK_ESCAPE) { Close(); Cancelled?.Invoke(); return true; }
        if (down && key.vkCode == VK_RETURN) { Close(); Committed?.Invoke(); return true; }
        if (down && key.vkCode is VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN or VK_DELETE)
        {
            KeyPressed?.Invoke(key.vkCode switch
            {
                VK_LEFT => SwitcherKey.Left, VK_RIGHT => SwitcherKey.Right, VK_UP => SwitcherKey.Up,
                VK_DOWN => SwitcherKey.Down, _ => SwitcherKey.Delete,
            });
            return true;
        }
        return false;
    }

    private static void SendDummyKey()
    {
        const uint KEYEVENTF_KEYUP = 0x0002;
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_DUMMY } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_DUMMY, dwFlags = KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() => _hook.Dispose();
}
