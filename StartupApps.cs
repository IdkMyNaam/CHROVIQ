using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>A program that starts at sign-in.</summary>
/// <param name="Source">Where it comes from, like "HKCU Run" or "Startup folder".</param>
/// <param name="Enabled">False when turned off in Task Manager's Startup apps.</param>
public sealed record StartupEntry(string Name, string Command, string Source, bool Enabled, bool IsFile);

/// <summary>
/// Starts the startup programs, which Explorer normally does at sign-in. Only used when
/// Windows Customizer is the shell (once per sign-in). Respects Task Manager's on/off switches.
/// </summary>
internal static class StartupApps
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Everything Explorer would start, with whether it's turned on.</summary>
    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        AddRunKey(list, Registry.CurrentUser, Run, "HKCU Run", "Run");
        AddRunKey(list, Registry.LocalMachine, Run, "HKLM Run", "Run");
        AddRunKey(list, Registry.LocalMachine, Run32, "HKLM Run (32-bit)", "Run32");
        AddRunKey(list, Registry.CurrentUser, RunOnce, "HKCU RunOnce", null);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)");
        return list;
    }

    private static void AddRunKey(List<StartupEntry> list, RegistryKey root, string path, string source, string? approvedKey)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) continue;
            bool enabled = approvedKey == null || IsApproved(approvedKey, name);
            list.Add(new StartupEntry(name, command, source, enabled, IsFile: false));
        }
    }

    private static void AddFolder(List<StartupEntry> list, string folder, string source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), file, source, IsApproved("StartupFolder", name), IsFile: true));
        }
    }

    /// <summary>
    /// Task Manager stores on/off per entry under ...\Explorer\StartupApproved\&lt;Run|Run32|StartupFolder&gt;:
    /// first byte even = on, odd = off. No entry means on.
    /// </summary>
    private static bool IsApproved(string approvedKey, string name)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"{Approved}\{approvedKey}");
            if (key?.GetValue(name) is byte[] { Length: > 0 } state)
                return (state[0] & 1) == 0;
        }
        return true;
    }

    /// <summary>Starts every turned-on entry, one after the other, in the background. RunOnce entries are removed after starting (as Windows does).</summary>
    public static Task RunAllAsync(Action<string>? log = null) => Task.Run(async () =>
    {
        foreach (var entry in List().Where(e => e.Enabled))
        {
            try
            {
                if (entry.IsFile)
                {
                    Process.Start(new ProcessStartInfo(entry.Command) { UseShellExecute = true })?.Dispose();
                }
                else
                {
                    // A command line (path + arguments): let the shell's "start" parse it like Explorer would.
                    var command = Environment.ExpandEnvironmentVariables(entry.Command);
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" {command}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })?.Dispose();
                }
                log?.Invoke($"Started {entry.Name} ({entry.Source})");

                if (entry.Source == "HKCU RunOnce")
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunOnce, writable: true);
                    key?.DeleteValue(entry.Name, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not start {entry.Name}: {ex.Message}");
            }
            await Task.Delay(200); // don't start everything in the same instant
        }
    });
}
