using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// Notices the Windows key pressed and released on its own, keeps the Start menu from opening, and
/// raises <see cref="Pressed"/> instead. Windows-key shortcuts (Win+E, Win+D, Win+Shift+S, ...) are
/// left alone. Uses a low-level keyboard hook, which Windows removes automatically when the app ends,
/// so the Windows key can never stay "taken" after a crash. Ctrl+Esc still opens Start.
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint LLKHF_INJECTED = 0x10;

    /// <summary>An unassigned key code: pressing it does nothing, but tells Windows "a shortcut was used".</summary>
    private const ushort VK_DUMMY = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr extraInfo;
    }

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

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc; // kept in a field: Windows calls it, so it must not be garbage collected
    private IntPtr _hook;
    private bool _winDown;
    private bool _usedAsShortcut;

    /// <summary>The Windows key was pressed and released on its own. Raised on the UI thread (the hook's thread).</summary>
    public event Action? Pressed;

    public WinKeyHook()
    {
        _proc = OnKey;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Windows did not allow the keyboard hook for the Windows key.");
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((key.flags & LLKHF_INJECTED) == 0) // our own sent keys pass through untouched
            {
                int msg = wParam.ToInt32();
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
                        SendKeys(VK_DUMMY, (ushort)key.vkCode);
                        Pressed?.Invoke();
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
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

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
