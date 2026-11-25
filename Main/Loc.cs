using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WindowsCustomizer;

/// <summary>
/// Translations. A language is a .json file: {"language": "Nederlands", "texts": {"Profiles": "Profielen", ...}},
/// the English text as the key. Files come with the app (Languages\ next to it) or are your own
/// (%AppData%\WindowsCustomizer\Languages); the file name is the language code (nl.json, de.json).
///
/// Windows are translated as they open (<see cref="TranslateTree"/>: every text, button, header and tooltip in
/// them), and code asks with <see cref="T"/>. Anything without a translation stays English. Every text the app
/// shows is remembered, so "Make a translation file" can list them for a translator.
/// </summary>
internal static class Loc
{
    private static Dictionary<string, string> _texts = new();
    private static readonly HashSet<string> Seen = new();

    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "Languages");
    public static string UserFolder => Path.Combine(AppSettings.Folder, "Languages");

    /// <summary>The language in use (a code like "nl"), or null for English.</summary>
    public static string? Current { get; private set; }

    /// <summary>Loads a language (at start; a change needs a restart, since open windows keep their texts).</summary>
    public static void Load(string? code)
    {
        Current = null;
        _texts = new();
        if (string.IsNullOrWhiteSpace(code) || code == "en") return;
        if (FileOf(code) is not { } file) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("texts", out var texts))
                foreach (var pair in texts.EnumerateObject())
                    if (pair.Value.GetString() is { Length: > 0 } translated) _texts[pair.Name] = translated;
            Current = code;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { AppTrace.Log($"Language {code}: {ex.Message}"); }
    }

    /// <summary>Your file wins over the one that comes with the app.</summary>
    private static string? FileOf(string code) =>
        new[] { Path.Combine(UserFolder, code + ".json"), Path.Combine(BuiltInFolder, code + ".json") }.FirstOrDefault(File.Exists);

    /// <summary>The languages there are files for: code and name ("nl", "Nederlands"), English first.</summary>
    public static List<(string Code, string Name)> Available()
    {
        var list = new List<(string, string)> { ("en", "English") };
        foreach (var file in new[] { BuiltInFolder, UserFolder }.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.json")))
        {
            string code = Path.GetFileNameWithoutExtension(file);
            if (code == "en" || list.Any(l => l.Item1 == code)) continue;
            list.Add((code, NameOf(code, file)));
        }
        return list;
    }

    private static string NameOf(string code, string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("language", out var name) && name.GetString() is { Length: > 0 } n) return n;
        }
        catch (Exception) { /* use the code's name */ }
        try { return CultureInfo.GetCultureInfo(code).NativeName; } catch (CultureNotFoundException) { return code; }
    }

    /// <summary>The translation of an English text (or the text itself).</summary>
    public static string T(string english)
    {
        if (string.IsNullOrWhiteSpace(english) || english == "-") return english;
        lock (Seen) Seen.Add(english);
        return _texts.TryGetValue(english, out var translated) ? translated : english;
    }

    /// <summary>Translates every fixed text in a window or element: text blocks, button and menu texts, headers, tooltips.</summary>
    public static void TranslateTree(DependencyObject root)
    {
        void Visit(object node)
        {
            switch (node)
            {
                case TextBlock text when !BindingOperations.IsDataBound(text, TextBlock.TextProperty) && text.Inlines.Count <= 1:
                    if (text.Text.Length > 0 && !IsSymbol(text.Text)) text.Text = T(text.Text);
                    break;
                case HeaderedContentControl headered when headered.Header is string h:
                    headered.Header = T(h);
                    break;
                case HeaderedItemsControl menu when menu.Header is string h:
                    menu.Header = T(h);
                    break;
                case ContentControl content when content.Content is string s && !BindingOperations.IsDataBound(content, ContentControl.ContentProperty):
                    if (!IsSymbol(s)) content.Content = T(s);
                    break;
                case Wpf.Ui.Controls.CardControl card when card.Header is string h:
                    card.Header = T(h);
                    break;
            }
            // A setting card's title and description are in its header, which isn't a logical child.
            if (node is Wpf.Ui.Controls.CardControl { Header: DependencyObject header }) Visit(header);
            if (node is FrameworkElement element && element.ToolTip is string tip) element.ToolTip = T(tip);
            if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Visit(child);
            if (node is FrameworkElement { ContextMenu: { } menuOf }) Visit(menuOf);
        }
        Visit(root);
    }

    /// <summary>Icon characters (Segoe Fluent Icons, in the private-use area) aren't words.</summary>
    private static bool IsSymbol(string text) => text.Length <= 2 && text.All(c => c >= '' && c <= '' || char.IsDigit(c));

    /// <summary>
    /// Writes a translation file for <paramref name="code"/> in your Languages folder: every text the app has shown
    /// so far (open the windows you want translated first), with the translations you already have kept.
    /// </summary>
    public static string WriteTemplate(string code, string languageName)
    {
        Directory.CreateDirectory(UserFolder);
        string file = Path.Combine(UserFolder, code + ".json");
        var existing = new Dictionary<string, string>();
        if (File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("texts", out var texts))
                    foreach (var pair in texts.EnumerateObject()) existing[pair.Name] = pair.Value.GetString() ?? "";
            }
            catch (JsonException) { /* start over; the old one is kept below */ }
            File.Copy(file, file + ".bak", overwrite: true);
        }
        List<string> all;
        lock (Seen) all = Seen.Union(existing.Keys).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var texts2 = all.ToDictionary(t => t, t => existing.TryGetValue(t, out var v) ? v : "");
        var json = JsonSerializer.Serialize(new { language = languageName, texts = texts2 },
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(file, json);
        return file;
    }
}
