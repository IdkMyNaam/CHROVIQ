using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Windows' own desktop icons (Recycle Bin, This PC, your user folder, Network, Control Panel): the ones that are
/// turned on in Settings > Personalization > Themes > Desktop icon settings, with their names in the user's language.
/// </summary>
internal static class SpecialIcons
{
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>(parsing name, shown unless the user turned it off?)</summary>
    private static readonly (string Id, bool ShownByDefault)[] Known =
    {
        (RecycleBin, true),
        ("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false), // This PC
        ("::{59031A47-3F72-44A7-89C5-5595FE6B30EE}", false), // the user's folder
        ("::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", false), // Network
        ("::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", false), // Control Panel
    };

    public static bool IsSpecial(string path) => path.StartsWith("::{", StringComparison.Ordinal);

    /// <summary>The shown ones, with their names.</summary>
    public static List<(string Id, string Name)> Shown()
    {
        var result = new List<(string, string)>();
        using var hide = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
        foreach (var (id, shownByDefault) in Known)
        {
            // The setting's value: 1 = hidden, 0 = shown; no value = Windows' default.
            bool shown = hide?.GetValue(id[2..]) is int hidden ? hidden == 0 : shownByDefault;
            if (shown && DisplayName(id) is { } name) result.Add((id, name));
        }
        return result;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

    private static string? DisplayName(string id)
    {
        if (SHParseDisplayName(id, IntPtr.Zero, out var pidl, 0, out _) != 0) return null;
        try
        {
            if (SHGetNameFromIDList(pidl, 0 /* SIGDN_NORMALDISPLAY */, out var name) != 0) return null;
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    /// <summary>
    /// Puts a deleted file or folder back from the Recycle Bin (the most recently deleted one with that
    /// path). False if it isn't there any more.
    /// </summary>
    public static bool Restore(string originalPath)
    {
        string folder = System.IO.Path.GetDirectoryName(originalPath) ?? "";
        string name = System.IO.Path.GetFileName(originalPath);
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic bin = shell.NameSpace(10); // the Recycle Bin
            dynamic? newest = null;
            DateTime newestDate = DateTime.MinValue;
            foreach (dynamic item in bin.Items())
            {
                if (!string.Equals((string?)item.ExtendedProperty("System.Recycle.DeletedFrom"), folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals((string?)item.ExtendedProperty("System.FileName"), name, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime deleted = item.ExtendedProperty("System.Recycle.DateDeleted") is DateTime d ? d : DateTime.MinValue;
                if (newest == null || deleted > newestDate) { newest = item; newestDate = deleted; }
            }
            if (newest == null) return false;
            newest.InvokeVerb("undelete");
            return true;
        }
        catch (Exception) { return false; }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
