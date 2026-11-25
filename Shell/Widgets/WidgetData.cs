using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowsCustomizer;

/// <summary>
/// GPU use in percent, the way Task Manager counts it: the busiest "3D" engine use, summed over all programs,
/// from Windows' performance counters (PDH). Call <see cref="Read"/> about once a second.
/// </summary>
internal sealed class GpuUsage : IDisposable
{
    private IntPtr _query, _counter;
    private bool _ok;

    public GpuUsage()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        _ok = PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter) == 0;
        if (_ok) PdhCollectQueryData(_query); // the first reading only sets the starting point
    }

    /// <summary>Percent since the previous call, or null when Windows doesn't have the counter.</summary>
    public int? Read()
    {
        lock (this) return ReadLocked();
    }

    private int? ReadLocked()
    {
        if (!_ok) return null;
        if (PdhCollectQueryData(_query) != 0) return null;
        uint size = 0, count = 0;
        int status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return 0;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, buffer) != 0) return null;
            double total = 0;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.CStatus == 0) total += item.Value;
            }
            return (int)Math.Round(Math.Clamp(total, 0, 100));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _ok = false;
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public uint CStatus;
        private uint _padding;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}

/// <summary>
/// Weather from Open-Meteo (open-meteo.com): free, no account or key. Only the place name (to look it up once)
/// and then its coordinates are sent.
/// </summary>
internal static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record Day(DateTime Date, int Code, double Max, double Min);
    public sealed record Report(double Temperature, int Code, double Wind, List<Day> Days);

    /// <summary>Finds a place by name: its coordinates and full name, or null.</summary>
    public static async Task<(double Lat, double Lon, string Name)?> FindAsync(string city)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name={Uri.EscapeDataString(city.Trim())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        string name = r.GetProperty("name").GetString() ?? city;
        if (r.TryGetProperty("country", out var country) && country.GetString() is { Length: > 0 } c) name += ", " + c;
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
    }

    public static async Task<Report> GetAsync(double lat, double lon, bool fahrenheit)
    {
        string unit = fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m" +
            $"&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=7{unit}");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var current = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        var days = new List<Day>();
        var dates = daily.GetProperty("time");
        for (int i = 0; i < dates.GetArrayLength(); i++)
        {
            days.Add(new Day(DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                daily.GetProperty("weather_code")[i].GetInt32(),
                daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                daily.GetProperty("temperature_2m_min")[i].GetDouble()));
        }
        return new Report(current.GetProperty("temperature_2m").GetDouble(), current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("wind_speed_10m").GetDouble(), days);
    }

    /// <summary>A symbol and a few words for a WMO weather code (what Open-Meteo returns).</summary>
    public static (string Symbol, string Text) Describe(int code) => code switch
    {
        0 => ("☀", "Clear"),
        1 or 2 => ("⛅", code == 1 ? "Mostly clear" : "Partly cloudy"),
        3 => ("☁", "Cloudy"),
        45 or 48 => ("☁", "Fog"),
        >= 51 and <= 57 => ("☂", "Drizzle"),
        >= 61 and <= 67 => ("☂", "Rain"),
        >= 71 and <= 77 => ("❄", "Snow"),
        >= 80 and <= 82 => ("☂", "Showers"),
        85 or 86 => ("❄", "Snow showers"),
        >= 95 => ("⚡", "Thunderstorm"),
        _ => ("☁", "Weather"),
    };
}
