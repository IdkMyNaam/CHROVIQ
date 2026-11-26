using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WindowsCustomizer;

/// <summary>
/// Sends problem reports and questions to the developer, as a message in a private Discord channel (through a
/// webhook): the person's text and contact details as the message, everything (with the logs) as an attached
/// report.txt. A webhook can only post into that one channel, not read it. Nothing is sent without the person
/// pressing Send, after seeing exactly what goes.
/// </summary>
internal static partial class ReportSender
{
    /// <summary>
    /// The report relay (Relay\worker.js, a Cloudflare Worker): it holds the Discord webhook as a secret, checks each
    /// report and limits how often anyone can send. This address is fine to be public. Empty: the older direct webhook
    /// below is used instead (only in builds that have ReportSender.Secret.cs).
    /// </summary>
    public const string RelayUrl = "https://reports.chroviq.com/report";

    /// <summary>
    /// The Discord webhook address. It isn't in the public source: it comes from ReportSender.Secret.cs, a file git
    /// ignores, so only release builds made by the developer can send. Without that file this is empty, and sending
    /// is off (the report can still be saved as a .zip).
    /// </summary>
    public static string WebhookUrl
    {
        get
        {
            string url = "";
            LoadWebhook(ref url);
            return url;
        }
    }

    /// <summary>Filled in by ReportSender.Secret.cs, when that file is there.</summary>
    static partial void LoadWebhook(ref string url);

    public static bool IsSetUp => RelayUrl.Length > 0 || WebhookUrl.Length > 0;

    public enum Kind { Problem, Question }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>When the last report was sent (one a minute at most, so a double click doesn't send it twice).</summary>
    private static DateTime _lastSent = DateTime.MinValue;

    /// <summary>The whole report as it will be sent: the person's text, how to reply, and (optionally) the details.</summary>
    public static string Compose(Kind kind, string text, string? replyTo, bool details)
    {
        var message = new StringBuilder();
        message.AppendLine(kind == Kind.Problem ? "What happened:" : "Question:");
        message.AppendLine(text.Trim());
        message.AppendLine();
        message.AppendLine(string.IsNullOrWhiteSpace(replyTo) ? "Reply to: (no email given)" : $"Reply to: {replyTo.Trim()}");
        if (!details) return message.ToString();

        message.AppendLine();
        message.AppendLine("----- About this PC -----");
        message.Append(LogWindow.AboutThisPc());
        foreach (var (name, lines) in new[] { ("trace.log", 300), ("shell.log", 100) })
        {
            if (Tail(Path.Combine(AppSettings.Folder, name), lines) is not { } tail) continue;
            message.AppendLine();
            message.AppendLine($"----- {name} (last {lines} lines) -----");
            message.Append(tail);
        }
        return message.ToString();
    }

    /// <summary>Through the relay: plain fields only; the relay builds the Discord message itself.</summary>
    private static async Task<string?> SendThroughRelayAsync(Kind kind, string report, string body, string? replyTo, string version)
    {
        const int maxReport = 440 * 1024; // the relay takes up to 450 KB; the newest log lines are at the end, so keep those
        if (report.Length > maxReport) report = "(older lines left out)\n" + report[^maxReport..];
        var payload = new
        {
            kind = kind == Kind.Problem ? "problem" : "question",
            text = body,
            replyTo = replyTo?.Trim() ?? "",
            version,
            windows = Environment.OSVersion.Version.ToString(),
            report,
        };
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(RelayUrl, content);
            string answer = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                _lastSent = DateTime.Now;
                AppTrace.Log($"Report sent through the relay ({kind})");
                return null;
            }
            AppTrace.Log($"Report not sent: {(int)response.StatusCode} {answer}");
            // The relay explains why in plain words ("Too many reports...").
            try
            {
                using var json = JsonDocument.Parse(answer);
                if (json.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } why)
                    return Loc.T(why);
            }
            catch (JsonException) { /* not the relay's answer */ }
            return $"It wasn't sent (the server said {(int)response.StatusCode}). Try again later, or save the report as a .zip.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"It couldn't be sent ({ex.Message}). Check the internet connection, or save the report as a .zip.";
        }
    }

    private static string? Tail(string path, int lines)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join("\n", all.TakeLast(lines + 1)).TrimEnd() + "\n";
        }
        catch (IOException) { return null; }
    }

    /// <summary>Sends it. Returns null when it went, or why it didn't.</summary>
    /// <param name="report">The whole report (<see cref="Compose"/>), attached as report.txt.</param>
    /// <param name="text">What the person wrote, shown in the message itself.</param>
    public static async Task<string?> SendAsync(Kind kind, string report, string text, string? replyTo)
    {
        if (!IsSetUp) return "Sending isn't set up in this version; save the report as a .zip instead.";
        if (DateTime.Now - _lastSent < TimeSpan.FromMinutes(1)) return "A report was just sent; wait a minute before sending another.";

        string version = Updates.Current.ToString(3);
        string body = text.Trim();
        if (body.Length > 3500) body = body[..3500] + "...";
        if (RelayUrl.Length > 0) return await SendThroughRelayAsync(kind, report, body, replyTo, version);
        var payload = new
        {
            username = "CHROVIQ reports",
            // Nothing in a report can ping anyone (@everyone, @here, users or roles).
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = kind == Kind.Problem ? $"Problem report (CHROVIQ {version})" : $"Question (CHROVIQ {version})",
                    description = body.Length > 0 ? body : "(no text)",
                    color = kind == Kind.Problem ? 0xE5484D : 0x6D4BFF,
                    fields = new[]
                    {
                        new { name = "Reply to", value = string.IsNullOrWhiteSpace(replyTo) ? "(no email given)" : replyTo.Trim(), inline = true },
                        new { name = "Windows", value = Environment.OSVersion.Version.ToString(), inline = true },
                    },
                    timestamp = DateTime.UtcNow.ToString("o"),
                },
            },
        };

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "files[0]", "report.txt");
        try
        {
            using var response = await Http.PostAsync(WebhookUrl, form);
            if (response.IsSuccessStatusCode)
            {
                _lastSent = DateTime.Now;
                AppTrace.Log($"Report sent ({kind})");
                return null;
            }
            AppTrace.Log($"Report not sent: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return response.StatusCode == HttpStatusCode.TooManyRequests
                ? "Too many reports right now; try again in a few minutes."
                : $"It wasn't sent (the server said {(int)response.StatusCode}). Try again later, or save the report as a .zip.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"It couldn't be sent ({ex.Message}). Check the internet connection, or save the report as a .zip.";
        }
    }
}
