using System.IO;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>Lists and applies the cursor schemes shown in Windows' Mouse settings.</summary>
public static class CursorSchemes
{
    private const string UserSchemesKey = @"Control Panel\Cursors\Schemes";
    private const string SystemSchemesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes";
    private const string ActiveCursorsKey = @"Control Panel\Cursors";

    /// <summary>The cursor value names, in the order a scheme lists its files.</summary>
    private static readonly string[] CursorNames = CustomCursors.Roles.Select(r => r.Name).ToArray();

    /// <summary>All installed scheme names: the user's own schemes and the system ones.</summary>
    public static List<string> List()
    {
        var names = new List<string>();
        using (var user = Registry.CurrentUser.OpenSubKey(UserSchemesKey))
            if (user != null) names.AddRange(user.GetValueNames());
        using (var system = Registry.LocalMachine.OpenSubKey(SystemSchemesKey))
            if (system != null) names.AddRange(system.GetValueNames());

        return names
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static void Apply(string schemeName)
    {
        // Same order as Windows: a user scheme wins over a system scheme with the same name.
        var (files, source) = Read(Registry.CurrentUser, UserSchemesKey, schemeName, 1)
                              ?? Read(Registry.LocalMachine, SystemSchemesKey, schemeName, 2)
                              ?? throw new KeyNotFoundException($"Cursor scheme \"{schemeName}\" is not installed.");

        var parts = files.Split(',');
        WriteActive(i => i < parts.Length ? parts[i].Trim() : "", schemeName, source);
    }

    /// <summary>Applies a custom set: role name to cursor file. Roles not in the set use the Windows default.</summary>
    public static void ApplyCustom(IReadOnlyDictionary<string, string> roleFiles)
    {
        // Check every file first, so a missing one doesn't leave a half-applied set.
        var missing = roleFiles.Where(kv => !File.Exists(kv.Value)).Select(kv => kv.Key).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException(
                $"Custom cursor file missing for: {string.Join(", ", missing)}. Import the cursor folder again.");

        // An unnamed set, like choosing "(None)" and browsing cursors in Mouse settings.
        WriteActive(i => roleFiles.GetValueOrDefault(CursorNames[i], ""), schemeName: "", source: 0);
    }

    /// <param name="fileForIndex">Cursor file for each index of <see cref="CursorNames"/>; "" means Windows default.</param>
    /// <param name="source">"Scheme Source": 0 = none/custom, 1 = user scheme, 2 = system scheme.</param>
    private static void WriteActive(Func<int, string> fileForIndex, string schemeName, int source)
    {
        using (var active = Registry.CurrentUser.CreateSubKey(ActiveCursorsKey))
        {
            for (int i = 0; i < CursorNames.Length; i++)
                active.SetValue(CursorNames[i], fileForIndex(i), RegistryValueKind.ExpandString);

            active.SetValue("", schemeName); // shown as the selected scheme
            active.SetValue("Scheme Source", source, RegistryValueKind.DWord);
        }

        ThemeService.ReloadCursors();
    }

    private static (string files, int source)? Read(RegistryKey root, string path, string name, int source)
    {
        using var key = root.OpenSubKey(path);
        // DoNotExpand keeps %SystemRoot% in the paths, like Windows itself stores them.
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string files
            ? (files, source)
            : null;
    }
}
