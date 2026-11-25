using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace WindowsCustomizer;

/// <summary>Wi-Fi and Bluetooth on/off through Windows' radio API (the same switches as Windows' Quick Settings).</summary>
internal static class SystemRadios
{
    /// <summary>The radio of that kind, or null (no adapter, or Windows doesn't allow it).</summary>
    public static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex) { AppTrace.Log($"Quick Settings: radios: {ex.Message}"); return null; }
    }

    public static async Task<bool> SetAsync(Radio radio, bool on)
    {
        try { return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed; }
        catch (Exception ex) { AppTrace.Log($"Quick Settings: radio {radio.Kind}: {ex.Message}"); return false; }
    }

    /// <summary>The network in use: the Wi-Fi name, "Ethernet" or similar, or null when offline.</summary>
    public static string? ConnectedNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return null;
            if (profile.IsWlanConnectionProfile && profile.WlanConnectionProfileDetails?.GetConnectedSsid() is { Length: > 0 } ssid) return ssid;
            return profile.ProfileName;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Night light on/off. Windows has no API for it; it keeps the state in a small binary value in the registry
/// (CloudStore). Known layout: byte 18 is 0x15 when on (with two extra bytes "10 00" at 23) and 0x13 when off.
/// The value is only changed when it has exactly one of those shapes; anything else (another Windows version)
/// leaves it alone, and the tile opens Windows' Night light settings instead.
/// </summary>
internal static class NightLight
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>On, off, or null when the state can't be read safely.</summary>
    public static bool? IsOn()
    {
        var data = Read();
        if (data == null) return null;
        if (IsOnShape(data)) return true;
        if (IsOffShape(data)) return false;
        return null;
    }

    private static bool IsOnShape(byte[] d) => d.Length > 25 && d[18] == 0x15 && d[23] == 0x10 && d[24] == 0x00;
    private static bool IsOffShape(byte[] d) => d.Length > 23 && d[18] == 0x13;

    private static byte[]? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("Data") as byte[];
        }
        catch (Exception) { return null; }
    }

    /// <summary>Turns night light on or off. False when the state isn't in a known shape (nothing is changed then).</summary>
    public static bool Set(bool on)
    {
        var data = Read();
        if (data == null) return false;
        bool isOn = IsOnShape(data);
        if (!isOn && !IsOffShape(data)) return false;
        if (isOn == on) return true;

        byte[] changed;
        if (on)
        {
            changed = data[..23].Concat(new byte[] { 0x10, 0x00 }).Concat(data[23..]).ToArray();
            changed[18] = 0x15;
        }
        else
        {
            changed = data[..23].Concat(data[25..]).ToArray();
            changed[18] = 0x13;
        }
        // A changed timestamp, so Windows notices: count up the first byte of it that isn't full yet.
        for (int i = 10; i < 15; i++)
        {
            if (changed[i] == 0xFF) continue;
            changed[i]++;
            break;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;
            key.SetValue("Data", changed, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex) { AppTrace.Log($"Quick Settings: night light: {ex.Message}"); return false; }
    }
}
/// <summary>
/// Screen brightness of external monitors over DDC/CI (the monitor's own brightness setting), one value per
/// monitor. Not every monitor supports it; those are left out. The calls are slow (the monitor answers over the
/// cable), so they run in the background and each monitor only gets its latest value.
/// The level each monitor had when this was opened is kept (and written to trace.log), so it can be put back.
/// </summary>
internal sealed class MonitorBrightness : IDisposable
{
    public sealed class Screen
    {
        public required IntPtr Handle { get; init; }
        public required uint Min { get; init; }
        public required uint Max { get; init; }
        /// <summary>The model the monitor reports (like "27G2AE"), or "Monitor 2".</summary>
        public required string Name { get; init; }
        /// <summary>Its brightness (0-100) when the panel opened.</summary>
        public required int StartPercent { get; init; }
        internal int Wanted = -1;
        internal Task? Sending;
    }

    public IReadOnlyList<Screen> Screens => _screens;
    private readonly List<Screen> _screens = new();
    private PHYSICAL_MONITOR[][] _all = Array.Empty<PHYSICAL_MONITOR[]>();
    private readonly object _lock = new();

    /// <summary>The monitors that answer, or null when none does. Slow (asks each monitor): call in the background.</summary>
    public static Task<MonitorBrightness?> OpenAsync() => Task.Run(() =>
    {
        var control = new MonitorBrightness();
        var lists = new List<PHYSICAL_MONITOR[]>();
        int number = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) return true;
            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return true;
            lists.Add(physical);
            foreach (var p in physical)
            {
                number++;
                if (!GetMonitorBrightness(p.Handle, out uint min, out uint current, out uint max) || max <= min) continue;
                var screen = new Screen
                {
                    Handle = p.Handle, Min = min, Max = max, Name = ModelOf(p.Handle) ?? $"Monitor {number}",
                    StartPercent = (int)Math.Round(100.0 * (current - min) / (max - min)),
                };
                control._screens.Add(screen);
                AppTrace.Log($"Quick Settings: {screen.Name} brightness is {current} (range {min}-{max})");
            }
            return true;
        }, IntPtr.Zero);
        control._all = lists.ToArray();
        if (control._screens.Count == 0) { control.Dispose(); return null; }
        return control;
    });

    /// <summary>The model from the monitor's capabilities ("...model(27G2AE)..."), or null.</summary>
    private static string? ModelOf(IntPtr handle)
    {
        try
        {
            if (!GetCapabilitiesStringLength(handle, out uint length) || length == 0 || length > 10000) return null;
            var text = new System.Text.StringBuilder((int)length + 1);
            if (!CapabilitiesRequestAndCapabilitiesReply(handle, text, length)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(text.ToString(), @"model\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Sets one monitor to this percentage (the latest value wins; earlier ones still waiting are skipped).</summary>
    public void Set(Screen screen, int percent)
    {
        lock (_lock)
        {
            screen.Wanted = Math.Clamp(percent, 0, 100);
            if (screen.Sending is { IsCompleted: false }) return;
            screen.Sending = Task.Run(() => Send(screen));
        }
    }

    private void Send(Screen screen)
    {
        while (true)
        {
            int value;
            lock (_lock)
            {
                if (screen.Wanted < 0) return;
                value = screen.Wanted;
                screen.Wanted = -1;
            }
            SetMonitorBrightness(screen.Handle, (uint)Math.Round(screen.Min + (screen.Max - screen.Min) * value / 100.0));
        }
    }

    public void Dispose()
    {
        // Let the last values reach the monitors before their handles go.
        foreach (var screen in _screens) screen.Sending?.Wait(2000);
        foreach (var list in _all) DestroyPhysicalMonitors((uint)list.Length, list);
        _all = Array.Empty<PHYSICAL_MONITOR[]>();
        _screens.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
    [DllImport("dxva2.dll")] private static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, System.Text.StringBuilder text, uint length);
}
