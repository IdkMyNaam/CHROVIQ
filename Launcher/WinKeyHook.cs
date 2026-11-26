using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// Notices the Windows key pressed and released on its own, keeps the Start menu from opening, and
/// raises <see cref="Pressed"/> instead. Windows-key shortcuts (Win+E, Win+D, Win+Shift+S, ...) are
/// left alone. Uses a low-level keyboard hook on its own thread (<see cref="KeyboardHookThread"/>), which
/// Windows removes automatically when the app ends, so the Windows key can never stay "taken" after a crash.
/// Ctrl+Esc still opens Start.
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint LLKHF_INJECTED = 0x10;

    /// <summary>An unassigned key code: pressing it does nothing, but tells Windows "a shortcut was used".</summary>
    private const ushort VK_DUMMY = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; // 1 = keyboard
        public KEYBDINPUT ki;
        private readonly long padding; // INPUT is a union; mouse input is the largest member
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private readonly KeyboardHookThread _hook;
    private volatile bool _winDown;
    private volatile bool _usedAsShortcut;

    /// <summary>
    /// Another hook used a Windows-key shortcut (Win+A, Win+N as the shell) and kept the key to itself, so this hook
    /// never saw it: releasing Win now isn't "Win on its own".
    /// </summary>
    public void ShortcutUsed()
    {
        if (_winDown) _usedAsShortcut = true;
    }

    /// <summary>The Windows key was pressed and released on its own. Raised on the hook's thread: hand it to the UI thread.</summary>
    public event Action? Pressed;

    public WinKeyHook() => _hook = new KeyboardHookThread("Windows key hook", OnKey);

    private bool OnKey(int msg, in KeyboardHookThread.KeyInfo key)
    {
        if ((key.flags & LLKHF_INJECTED) != 0) return false; // our own sent keys pass through untouched
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = msg is WM_KEYUP or WM_SYSKEYUP;
        bool isWin = key.vkCode is VK_LWIN or VK_RWIN;

        if (isWin && down)
        {
            if (!_winDown) { _winDown = true; _usedAsShortcut = false; }
        }
        else if (!isWin && down && _winDown)
        {
            _usedAsShortcut = true; // Win + something: a normal shortcut
        }
        else if (isWin && up && _winDown)
        {
            _winDown = false;
            if (!_usedAsShortcut)
            {
                // Hold back this key release, send "dummy key, then Windows key up" instead.
                // To Windows that was a shortcut, so the Start menu stays closed.
                // Sent just after this hook returns, not from inside it (a hook that answers late is skipped).
                ushort winKey = (ushort)key.vkCode;
                ThreadPool.UnsafeQueueUserWorkItem(_ => SendKeys(VK_DUMMY, winKey), null);
                Pressed?.Invoke();
                return true;
            }
        }
        return false;
    }

    private static void SendKeys(ushort dummy, ushort winKey)
    {
        const uint KEYEVENTF_KEYUP = 0x0002;
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = dummy } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = dummy, dwFlags = KEYEVENTF_KEYUP } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = winKey, dwFlags = KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() => _hook.Dispose();
}
