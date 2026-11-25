using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// The window Windows knows as "the shell window" (GetShellWindow) while Windows Customizer is the shell.
/// Like Explorer's own: a plain, hidden window of class "Progman" ("Program Manager"). With one registered,
/// a newly started explorer.exe only opens File Explorer windows instead of taking over as the shell.
///
/// A separate native window, not our WPF desktop: Windows refuses (error 87) a window that is or was made
/// "always on top", and a plain hidden window is never anything but a placeholder.
/// </summary>
internal sealed class ShellWindow : IDisposable
{
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetShellWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private const string ClassName = "Progman";
    private const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80;
    private static readonly WndProc Proc = DefWindowProc; // kept alive: Windows calls it for as long as the class exists
    private static bool _classRegistered;

    public IntPtr Handle { get; }

    /// <summary>Creates the (hidden) window. Call on the UI thread: its messages are handled there.</summary>
    public ShellWindow()
    {
        if (!_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Proc,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the shell window class");
            _classRegistered = true;
        }
        Handle = CreateWindowEx(WS_EX_TOOLWINDOW, ClassName, "Program Manager", WS_POPUP, 0, 0, 0, 0,
                                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the shell window");
    }

    /// <summary>
    /// One attempt to register. Never pushes aside another shell window that is still alive.
    /// Returns null when registered, otherwise why not.
    /// </summary>
    public string? TryRegister()
    {
        var current = GetShellWindow();
        if (current == Handle) return null;
        if (current != IntPtr.Zero && IsWindow(current)) return $"another shell window exists ({current})";
        if (SetShellWindow(Handle) && GetShellWindow() == Handle) return null;
        return $"Windows refused (error {Marshal.GetLastWin32Error()})";
    }

    public void Dispose() => DestroyWindow(Handle);
}
