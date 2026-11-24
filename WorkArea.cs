using System.Runtime.InteropServices;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The usable screen area ("work area": where maximized windows go) when Windows Customizer is the
/// shell. Normally Explorer manages it through the app-bar messages; without Explorer nobody does,
/// so our bars report what they reserve here and the work area of each monitor is set directly.
/// </summary>
internal static class WorkArea
{
    private const int SPI_SETWORKAREA = 0x002F;
    private const int SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);

    // Pixels reserved per monitor and edge.
    private static readonly Dictionary<(string device, TaskbarEdge edge), int> Reserved = new();

    public static void Reserve(string device, TaskbarEdge edge, int pixels)
    {
        if (Reserved.TryGetValue((device, edge), out int old) && old == pixels) return;
        Reserved[(device, edge)] = pixels;
        Apply(device);
    }

    public static void Release(string device, TaskbarEdge edge)
    {
        if (Reserved.Remove((device, edge))) Apply(device);
    }

    /// <summary>Monitor minus what the bars on its top and bottom edges reserve.</summary>
    private static void Apply(string device)
    {
        if (Monitors.Find(device) is not { } found) return;
        var rect = found.info.Bounds;
        rect.Top += Reserved.GetValueOrDefault((device, TaskbarEdge.Top));
        rect.Bottom -= Reserved.GetValueOrDefault((device, TaskbarEdge.Bottom));
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref rect, SPIF_SENDCHANGE); // tells maximized windows to resize
    }
}
