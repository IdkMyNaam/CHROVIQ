using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// A low-level keyboard hook on its own thread. Windows waits for a low-level hook on EVERY key press in every
/// program, so the hook must never sit behind the taskbar's work (drawing, previews, menus): on the UI thread,
/// a busy moment there made the whole keyboard wait. This thread does nothing but answer the hook.
/// The handler runs on this thread: it must be quick and hand any real work to the UI thread.
/// Windows removes the hook by itself when the app ends, so a key can never stay "taken" after a crash.
/// </summary>
internal sealed class KeyboardHookThread : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyInfo { public uint vkCode, scanCode, flags, time; public IntPtr extraInfo; }

    /// <summary>Gets each key event (message = WM_KEYDOWN etc.); returns true to keep it from Windows and other apps.</summary>
    public delegate bool KeyHandler(int message, in KeyInfo key);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x0012;

    private readonly KeyHandler _handler;
    private readonly HookProc _proc; // kept in a field: Windows calls it, so it must not be garbage collected
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    public KeyboardHookThread(string name, KeyHandler handler)
    {
        _handler = handler;
        _proc = OnKey;
        using var ready = new ManualResetEventSlim();
        int error = 0;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            // This app's own module (null = the .exe). Not Process.MainModule: that lists every loaded DLL first,
            // which took about a second at startup.
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
            ready.Set();
            if (_hook == IntPtr.Zero) return;

            // The hook is called through this thread's messages; run until Dispose posts WM_QUIT.
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != 0) throw new InvalidOperationException($"Windows did not allow the keyboard hook ({name}, error {error}).");
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var key = Marshal.PtrToStructure<KeyInfo>(lParam);
                if (_handler(wParam.ToInt32(), in key)) return (IntPtr)1;
            }
            catch (Exception) { /* never let a bug here take keys away: pass the key on */ }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
