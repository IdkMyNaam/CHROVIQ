using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>When an automatic profile rule applies.</summary>
public enum AutoSwitchWhen
{
    /// <summary>From a time of day on (until the next time rule).</summary>
    Time,
    /// <summary>While a program runs (it wins over time rules; afterwards the profile from before comes back).</summary>
    AppRunning,
}

/// <summary>One rule: switch to <see cref="Profile"/> at a time, or while an app runs.</summary>
public sealed class AutoSwitchRule
{
    public AutoSwitchWhen When { get; set; } = AutoSwitchWhen.Time;

    /// <summary>The profile's name.</summary>
    public string Profile { get; set; } = "";

    /// <summary>Time rules: "HH:mm".</summary>
    public string? At { get; set; }

    /// <summary>App rules: the program's name without ".exe" (like "cs2" or "notepad").</summary>
    public string? App { get; set; }

    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
}

/// <summary>
/// Switches profiles by itself (Profiles page, "Switch profiles automatically"): time rules (Night from 21:00,
/// School from 08:00) and app rules (Gaming while cs2 runs). It only acts when the profile it wants changes, so
/// a profile you pick by hand stays until the next rule kicks in. App rules win over time rules; when the app
/// closes, the profile from before comes back (or the time rule's, if there is one).
///
/// Checks when the active window changes (a program that starts or closes almost always changes it) and every
/// 30 seconds (for the time rules, and as a safety net).
/// </summary>
public sealed class ProfileSwitcher
{
    private readonly AppSettings _settings;
    private readonly Func<string, Task> _apply;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly WinEventProc _foregroundCallback; // kept in a field so the GC doesn't collect it
    private IntPtr _foregroundHook;

    /// <summary>What the rules wanted last time (null: nothing yet); a change of this is what switches.</summary>
    private string? _wanted;
    /// <summary>The profile that was in use when an app rule took over, to go back to afterwards.</summary>
    private string? _beforeApp;
    private bool _appRuleActive, _busy;

    /// <param name="apply">Applies a profile by name (and remembers it as the last applied one).</param>
    public ProfileSwitcher(AppSettings settings, Func<string, Task> apply)
    {
        _settings = settings;
        _apply = apply;
        _timer.Tick += async (_, _) => await CheckAsync();
        _settle.Tick += async (_, _) => { _settle.Stop(); await CheckAsync(); };
        _foregroundCallback = (_, _, _, _, _, _, _) => { _settle.Stop(); _settle.Start(); };
    }

    /// <summary>Starts or stops to match the setting (call again after the rules or the switch changed).</summary>
    public void Update()
    {
        bool on = _settings.AutoSwitch && _settings.AutoSwitchRules.Count > 0;
        if (!on) { Stop(); return; }
        _timer.Start();
        if (_foregroundHook == IntPtr.Zero && _settings.AutoSwitchRules.Any(r => r.When == AutoSwitchWhen.AppRunning))
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                                              _foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _wanted = null; // rules changed: look again from scratch
        _ = CheckAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _settle.Stop();
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        _wanted = null;
        _appRuleActive = false;
    }

    private async Task CheckAsync()
    {
        if (_busy || !_settings.AutoSwitch) return;
        _busy = true;
        try
        {
            string? appProfile = RunningAppProfile();
            string? wanted = appProfile ?? TimeProfile();

            if (appProfile != null && !_appRuleActive)
            {
                _appRuleActive = true;
                _beforeApp = _settings.LastAppliedProfile; // to come back to when the app closes
            }
            else if (appProfile == null && _appRuleActive)
            {
                _appRuleActive = false;
                wanted ??= _beforeApp; // no time rule: back to what was in use before
                _wanted = null;        // force the switch back even if the time rule's profile didn't change
            }

            if (wanted == null || wanted == _wanted) return;
            _wanted = wanted;
            if (wanted.Equals(_settings.LastAppliedProfile, StringComparison.CurrentCultureIgnoreCase)) return; // already in use
            AppTrace.Log($"Auto-switch: applying \"{wanted}\"");
            await _apply(wanted);
        }
        catch (Exception ex) { AppTrace.Log($"Auto-switch: {ex.Message}"); }
        finally { _busy = false; }
    }

    /// <summary>The first app rule (in list order) whose program runs, or null.</summary>
    private string? RunningAppProfile()
    {
        var rules = _settings.AutoSwitchRules.Where(r => r.When == AutoSwitchWhen.AppRunning && !string.IsNullOrWhiteSpace(r.App)
                                                         && !string.IsNullOrWhiteSpace(r.Profile)).ToList();
        if (rules.Count == 0) return null;
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) using (p) running.Add(p.ProcessName);
        foreach (var rule in rules)
        {
            string name = rule.App!.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (running.Contains(name)) return rule.Profile;
        }
        return null;
    }

    /// <summary>The time rule in effect now: the latest one that started today, else the last one of yesterday.</summary>
    private string? TimeProfile()
    {
        var rules = _settings.AutoSwitchRules
            .Where(r => r.When == AutoSwitchWhen.Time && !string.IsNullOrWhiteSpace(r.Profile))
            .Select(r => (ok: AutoSwitchRule.TryParseTime(r.At, out var t), time: t, r.Profile))
            .Where(x => x.ok).OrderBy(x => x.time).ToList();
        if (rules.Count == 0) return null;
        var now = DateTime.Now.TimeOfDay;
        var started = rules.LastOrDefault(x => x.time <= now);
        return (started.ok ? started : rules[^1]).Profile;
    }
}
