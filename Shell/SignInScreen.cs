using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Windows' own lock and sign-in screen, through the settings Windows itself offers (the Settings app and group
/// policies). Signing in itself is never touched: no password, PIN or account setting is read or changed.
/// </summary>
internal static class SignInScreen
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string CspKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";

    /// <summary>Where the whole-PC sign-in picture is kept (readable by Windows before anyone signs in).</summary>
    private static string PictureFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CHROVIQ", "SignIn");

    /// <summary>The sign-in screen settings as Windows has them now (reading needs no administrator).</summary>
    public sealed record State(bool Blur, bool LogonPicture, string? PcPicture);

    public static State Current()
    {
        int Policy(string name)
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue(name) is int value ? value : 0;
        }
        string? picture = null;
        using (var csp = Registry.LocalMachine.OpenSubKey(CspKey))
            if (csp?.GetValue("LockScreenImageStatus") is int status && status == 1) picture = csp.GetValue("LockScreenImagePath") as string;
        return new State(Blur: Policy("DisableAcrylicBackgroundOnLogon") != 1, LogonPicture: Policy("DisableLogonBackgroundImage") != 1, picture);
    }

    // ---------- Your own lock screen picture (no administrator) ----------

    /// <summary>Sets this account's lock screen picture with Windows' own API; null when it worked, else why not.</summary>
    public static async Task<string?> SetLockScreenPictureAsync(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(path)) return Loc.T("That picture can't be found.");
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            try
            {
                await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
                return null;
            }
            catch (Exception)
            {
                // The other official way (it answers yes or no instead of failing).
                if (Windows.System.UserProfile.UserProfilePersonalizationSettings.IsSupported() &&
                    await Windows.System.UserProfile.UserProfilePersonalizationSettings.Current.TrySetLockScreenImageAsync(file))
                    return null;
                throw;
            }
        }
        catch (Exception ex)
        {
            AppTrace.Log($"Lock screen picture: {ex.Message}");
            return Loc.T("Windows didn't allow it for CHROVIQ. Use the whole-PC sign-in picture below, or Windows Settings > Personalization > Lock screen.");
        }
    }

    // ---------- The sign-in screen for the whole PC (administrator) ----------

    /// <summary>
    /// Applies the sign-in screen settings through a copy of CHROVIQ with administrator rights (one Windows prompt).
    /// Null when it worked, else why not.
    /// </summary>
    public static async Task<string?> ApplyAsAdminAsync(bool blur, bool logonPicture, string? pcPicture, bool reset)
    {
        string result = Path.Combine(Path.GetTempPath(), $"chroviq-signin-{Guid.NewGuid():N}.txt");
        var args = reset
            ? $"--signin-admin --reset --result \"{result}\""
            : $"--signin-admin --blur {(blur ? 1 : 0)} --logon-picture {(logonPicture ? 1 : 0)} --pc-picture \"{pcPicture ?? ""}\" --result \"{result}\"";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            if (process == null) return Loc.T("It could not be started.");
            await process.WaitForExitAsync();
            string answer = File.Exists(result) ? File.ReadAllText(result).Trim() : "";
            try { File.Delete(result); } catch (Exception) { /* a leftover temp file */ }
            return answer == "ok" ? null : answer.Length > 0 ? answer : Loc.T("It ended without an answer.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Loc.T("Not changed: the administrator question was answered with No.");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// The administrator copy (command line "--signin-admin ..."): writes only these sign-in screen settings, then
    /// ends. Runs before WPF starts, from Program.Main.
    /// </summary>
    public static int RunAsAdmin(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        string? resultFile = Arg("--result");
        string answer = "ok";
        try
        {
            using var policy = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
            using var csp = Registry.LocalMachine.CreateSubKey(CspKey, writable: true);
            void Flag(string name, bool on)
            {
                if (on) policy.SetValue(name, 1, RegistryValueKind.DWord);
                else policy.DeleteValue(name, throwOnMissingValue: false);
            }
            void ClearPicture()
            {
                foreach (var name in new[] { "LockScreenImagePath", "LockScreenImageUrl", "LockScreenImageStatus" })
                    csp.DeleteValue(name, throwOnMissingValue: false);
                if (Directory.Exists(PictureFolder))
                    foreach (var old in Directory.GetFiles(PictureFolder, "lockscreen.*")) File.Delete(old);
            }

            if (args.Any(a => a.Equals("--reset", StringComparison.OrdinalIgnoreCase)))
            {
                Flag("DisableAcrylicBackgroundOnLogon", false);
                Flag("DisableLogonBackgroundImage", false);
                ClearPicture();
            }
            else
            {
                Flag("DisableAcrylicBackgroundOnLogon", Arg("--blur") == "0");
                Flag("DisableLogonBackgroundImage", Arg("--logon-picture") == "0");
                string picture = Environment.ExpandEnvironmentVariables((Arg("--pc-picture") ?? "").Trim().Trim('"'));
                if (picture.Length == 0) ClearPicture();
                else if (!File.Exists(picture)) answer = "The sign-in picture can't be found: " + picture;
                else if (!Path.GetDirectoryName(Path.GetFullPath(picture))!.Equals(PictureFolder, StringComparison.OrdinalIgnoreCase))
                {
                    // A copy where Windows can read it before anyone signs in.
                    ClearPicture();
                    Directory.CreateDirectory(PictureFolder);
                    string copy = Path.Combine(PictureFolder, "lockscreen" + Path.GetExtension(picture).ToLowerInvariant());
                    File.Copy(picture, copy, overwrite: true);
                    csp.SetValue("LockScreenImagePath", copy, RegistryValueKind.String);
                    csp.SetValue("LockScreenImageUrl", copy, RegistryValueKind.String);
                    csp.SetValue("LockScreenImageStatus", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex) { answer = ex.Message; }
        try { if (resultFile != null) File.WriteAllText(resultFile, answer); } catch (Exception) { /* the window says it ended without an answer */ }
        return answer == "ok" ? 0 : 1;
    }

    [DllImport("user32.dll")] public static extern bool LockWorkStation();
}

/// <summary>"Sign-in screen...": Windows' lock screen picture and how its sign-in screen looks. "Try it" locks the PC.</summary>
internal sealed class SignInSettingsWindow : SettingsForm
{
    private readonly TextBlock _lockStatus = Status(), _signInStatus = Status();

    public SignInSettingsWindow()
        : base("Sign-in screen", () => { }, () => SignInScreen.LockWorkStation())
    {
        Header("Lock screen");
        Hint("Your account's lock screen picture (what you see after Win+L or when the PC wakes up). No administrator needed.");
        string lockPicture = "";
        Text("Picture (image file)", "", v => lockPicture = v, "A full path, like C:\\Users\\You\\Pictures\\lock.jpg.");
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(Button("Set as lock screen picture", async () =>
        {
            if (string.IsNullOrWhiteSpace(lockPicture)) { _lockStatus.Text = Loc.T("Type the picture's path first."); return; }
            _lockStatus.Text = await SignInScreen.SetLockScreenPictureAsync(lockPicture) ?? Loc.T("Done. Press \"Try it\" to see it.");
        }));
        row.Children.Add(Button("Use the wallpaper", async () =>
        {
            if (Wallpaper.Current().File is not { } wallpaper) { _lockStatus.Text = Loc.T("There's no wallpaper picture right now."); return; }
            _lockStatus.Text = await SignInScreen.SetLockScreenPictureAsync(wallpaper) ?? Loc.T("Done. Press \"Try it\" to see it.");
        }));
        Add(row);
        Add(_lockStatus);

        var state = SignInScreen.Current();
        bool blur = state.Blur, logonPicture = state.LogonPicture;
        string pcPicture = state.PcPicture ?? "";
        Header("Sign-in screen");
        Hint("Windows settings for the whole PC (every account), so applying them asks for administrator permission once. Signing in itself stays the same: your password, PIN and accounts aren't touched.");
        Check("Blur behind the sign-in box", blur, v => blur = v);
        Check("Lock screen picture behind the sign-in box", logonPicture, v => logonPicture = v);
        Text("One sign-in picture for the whole PC", pcPicture, v => pcPicture = v,
            "Empty: none (each account's own lock screen picture). Windows uses this on Education and Enterprise editions; others may ignore it.");
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Button("Apply (asks for administrator)", async () =>
        {
            _signInStatus.Text = Loc.T("Waiting for the administrator question...");
            _signInStatus.Text = await SignInScreen.ApplyAsAdminAsync(blur, logonPicture, pcPicture, reset: false)
                                 ?? Loc.T("Done. Press \"Try it\" to see it.");
        }, primary: true));
        buttons.Children.Add(Button("Reset to Windows default", async () =>
        {
            _signInStatus.Text = Loc.T("Waiting for the administrator question...");
            _signInStatus.Text = await SignInScreen.ApplyAsAdminAsync(true, true, null, reset: true)
                                 ?? Loc.T("Back to Windows' default. Reopen this window to see the settings.");
        }));
        Add(buttons);
        Add(_signInStatus);
        Hint("\"Try it\" locks the PC (like Win+L), so you can see the lock screen and the sign-in screen.");
    }

    private static TextBlock Status() => new() { Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };

    private static Wpf.Ui.Controls.Button Button(string text, Func<Task> click, bool primary = false)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = Loc.T(text), Margin = new Thickness(0, 0, 8, 8),
            Appearance = primary ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary,
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await click(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
}
