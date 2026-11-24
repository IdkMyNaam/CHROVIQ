using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>CPU and memory use, read from Windows (no extra libraries).</summary>
internal sealed class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low, High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad; // percent of physical memory in use
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    private ulong _lastIdle, _lastTotal;

    /// <summary>CPU use since the previous call, in percent (call about once a second).</summary>
    public int CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong total = kernel.Value + user.Value; // kernel time includes idle time
        ulong idleDelta = idle.Value - _lastIdle, totalDelta = total - _lastTotal;
        bool first = _lastTotal == 0;
        _lastIdle = idle.Value;
        _lastTotal = total;
        if (first || totalDelta == 0) return 0;
        return (int)Math.Round(100.0 * (totalDelta - idleDelta) / totalDelta);
    }

    /// <summary>Physical memory in use: percent, and used/total in GB.</summary>
    public static (int percent, double usedGb, double totalGb) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0, 0);
        const double gb = 1024.0 * 1024 * 1024;
        return ((int)status.dwMemoryLoad, (status.ullTotalPhys - status.ullAvailPhys) / gb, status.ullTotalPhys / gb);
    }
}
