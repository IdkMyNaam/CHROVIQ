using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Back up / restore everything in one .zip: settings, profiles (with their cursors), desktop layout, app themes,
/// taskbar designs and your own materials. For moving to a new PC, or keeping a copy before trying things.
///
/// A restore never overwrites files while the app runs (it saves on exit, which would undo the restore):
/// the backup is unpacked into a "PendingRestore" folder and moved into place at the next start, before
/// anything is loaded. Your current settings are backed up first, so a restore can be undone.
/// </summary>
internal static class SettingsBackup
{
    private const string ManifestName = "windows-customizer-backup.json";
    private static string PendingFolder => Path.Combine(AppSettings.Folder, "PendingRestore");

    /// <summary>Automatic backups (before a restore) go here.</summary>
    public static string AutoBackupFolder => Path.Combine(AppSettings.Folder, "Backups", "Settings");

    private sealed class Manifest
    {
        public string App { get; set; } = "CHROVIQ";
        public string? Version { get; set; }
        public DateTime Created { get; set; }
        /// <summary>Where the files were (%AppData%\WindowsCustomizer on that PC), to fix up stored paths on another PC.</summary>
        public string? Folder { get; set; }
    }

    /// <summary>Not part of a backup: logs, broken or temporary files, the backups themselves.</summary>
    private static bool Skip(string relative)
    {
        string top = relative.Split(Path.DirectorySeparatorChar)[0];
        if (top.Equals("Backups", StringComparison.OrdinalIgnoreCase) || top.Equals("PendingRestore", StringComparison.OrdinalIgnoreCase)
            || top.Equals("Wallpapers", StringComparison.OrdinalIgnoreCase)) return true; // daily wallpapers: downloaded again
        string ext = Path.GetExtension(relative).ToLowerInvariant();
        return ext is ".log" or ".bad" or ".tmp";
    }

    // ---------- Back up ----------

    /// <summary>Asks where to save, then writes the backup. Returns a status line (or null when cancelled).</summary>
    public static string? BackUp(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up all CHROVIQ settings",
            Filter = "CHROVIQ backup (*.zip)|*.zip",
            FileName = $"CHROVIQ backup {DateTime.Now:yyyy-MM-dd}.zip",
        };
        if (dialog.ShowDialog(owner) != true) return null;
        try
        {
            int count = Write(dialog.FileName);
            return $"Backed up {count} files to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not write the backup: {ex.Message}";
        }
    }

    /// <summary>Zips the settings folder (minus <see cref="Skip"/>) with a manifest. Returns the number of files.</summary>
    private static int Write(string zipPath)
    {
        string temp = zipPath + ".tmp";
        int count = 0;
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var manifest = new Manifest
            {
                Version = typeof(SettingsBackup).Assembly.GetName().Version?.ToString(),
                Created = DateTime.Now,
                Folder = AppSettings.Folder,
            };
            using (var writer = new StreamWriter(zip.CreateEntry(ManifestName).Open()))
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            if (Directory.Exists(AppSettings.Folder))
            {
                foreach (var file in Directory.EnumerateFiles(AppSettings.Folder, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(AppSettings.Folder, file);
                    if (Skip(relative)) continue;
                    try
                    {
                        // Shared read: settings.json may be open for a moment while the app saves.
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var target = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal).Open();
                        source.CopyTo(target);
                        count++;
                    }
                    catch (IOException ex) { AppTrace.Log($"Backup: skipped {relative} ({ex.Message})"); }
                }
            }
        }
        File.Move(temp, zipPath, overwrite: true);
        return count;
    }

    // ---------- Restore ----------

    /// <summary>
    /// Asks for a backup, checks it, saves the current settings as an automatic backup, and stages the restore.
    /// Returns a status line (null when cancelled). <paramref name="restartNow"/> is true when the app should restart.
    /// </summary>
    public static string? Restore(Window owner, out bool restartNow)
    {
        restartNow = false;
        var dialog = new OpenFileDialog { Title = "Restore CHROVIQ settings", Filter = "CHROVIQ backup (*.zip)|*.zip" };
        if (dialog.ShowDialog(owner) != true) return null;

        Manifest? manifest;
        try
        {
            using var zip = ZipFile.OpenRead(dialog.FileName);
            var entry = zip.GetEntry(ManifestName);
            if (entry == null) return "That .zip isn't a CHROVIQ backup (it has no windows-customizer-backup.json).";
            using var reader = new StreamReader(entry.Open());
            manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return $"Could not read the backup: {ex.Message}";
        }

        string when = manifest?.Created is { } created && created != default ? $" from {created:d MMM yyyy, HH:mm}" : "";
        string how = ShellMode.IsShell
            ? "It's used the next time you sign in (CHROVIQ is your shell, so it doesn't restart now)."
            : "CHROVIQ restarts to use it.";
        if (MessageBox.Show(owner,
                $"Restore the backup{when}?\n\nThis replaces all your current settings, profiles, themes, taskbar designs and materials. " +
                $"Your current settings are backed up first (in Backups\\Settings), so you can go back.\n\n{how}",
                "Restore settings", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return null;

        try
        {
            // 1. The current settings, so this can be undone.
            Directory.CreateDirectory(AutoBackupFolder);
            string before = Path.Combine(AutoBackupFolder, $"before restore {DateTime.Now:yyyy-MM-dd HHmmss}.zip");
            Write(before);

            // 2. Unpack into the pending folder (only files inside it: a crafted .zip can't write elsewhere).
            if (Directory.Exists(PendingFolder)) Directory.Delete(PendingFolder, recursive: true);
            Directory.CreateDirectory(PendingFolder);
            string root = Path.GetFullPath(PendingFolder) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(dialog.FileName))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // a folder
                    string target = Path.GetFullPath(Path.Combine(PendingFolder, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { if (Directory.Exists(PendingFolder)) Directory.Delete(PendingFolder, recursive: true); } catch (IOException) { }
            return $"Could not restore: {ex.Message}. Nothing was changed.";
        }

        AppTrace.Log($"Restore staged from {dialog.FileName}");
        if (ShellMode.IsShell) return "The backup is ready; it's used the next time you sign in.";
        restartNow = true;
        return "Restoring: restarting CHROVIQ...";
    }

    /// <summary>Starts a new copy of the app that waits for this one to close, then applies the restore.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        Process.Start(new ProcessStartInfo(exe, $"--after-restore {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>
    /// At startup, before anything is loaded: moves a staged restore into place. With --after-restore, first waits
    /// for the copy that staged it to close (it saves on exit). Safe to call when there's nothing to restore.
    /// </summary>
    public static void ApplyPending(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--after-restore", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int pid))
        {
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(15000); }
            catch (ArgumentException) { /* already closed */ }
        }

        if (!File.Exists(Path.Combine(PendingFolder, ManifestName))) return;
        try
        {
            Manifest? manifest = null;
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(PendingFolder, ManifestName))); }
            catch (JsonException) { /* restore anyway; paths just aren't fixed up */ }

            // Clear what a backup holds (keeping logs and backups), then move the restored files in.
            foreach (var file in Directory.EnumerateFiles(AppSettings.Folder, "*", SearchOption.AllDirectories).ToList())
            {
                string relative = Path.GetRelativePath(AppSettings.Folder, file);
                if (!Skip(relative)) File.Delete(file);
            }
            foreach (var file in Directory.EnumerateFiles(PendingFolder, "*", SearchOption.AllDirectories).ToList())
            {
                string relative = Path.GetRelativePath(PendingFolder, file);
                if (relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(AppSettings.Folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
                // From another PC (or user name): stored paths into the old settings folder (custom cursors) now point here.
                if (manifest?.Folder is { } oldFolder && !oldFolder.Equals(AppSettings.Folder, StringComparison.OrdinalIgnoreCase)
                    && target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(target);
                    string oldJson = JsonSerializer.Serialize(oldFolder).Trim('"'), newJson = JsonSerializer.Serialize(AppSettings.Folder).Trim('"');
                    if (text.Contains(oldJson, StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(target, text.Replace(oldJson, newJson, StringComparison.OrdinalIgnoreCase));
                }
            }
            Directory.Delete(PendingFolder, recursive: true);
            AppTrace.Log("Restore applied");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppTrace.Log($"Restore failed: {ex.Message}");
            MessageBox.Show($"The settings backup could not be restored ({ex.Message}).\n\nYour settings from before are in " +
                            $"{AutoBackupFolder}.", "CHROVIQ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
