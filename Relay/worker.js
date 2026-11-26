// CHROVIQ report relay (Cloudflare Worker).
//
// The app sends a report here as plain JSON; this Worker checks it, builds the Discord message itself and posts it to
// the webhook, which is a Cloudflare secret (DISCORD_WEBHOOK) and is never in the app. So someone who finds this
// address can only send a report-shaped message, a few times a minute: they can't post their own names, pictures or
// pings, and they can't rename or delete the webhook.

const MAX_BYTES = 512 * 1024;      // a whole request
const MAX_TEXT = 3500;             // what the person wrote
const MAX_REPLY = 200;             // their email (optional)
const MAX_REPORT = 450 * 1024;     // report.txt with the logs

function answer(status, message) {
  return new Response(JSON.stringify({ ok: status === 200, message }), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function text(value, max) {
  if (value === undefined || value === null) return "";
  if (typeof value !== "string") return null;
  return value.length > max ? value.slice(0, max) + "..." : value;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/report") return answer(404, "Not found.");
    if (request.method !== "POST") return answer(405, "Only POST.");
    if (!env.DISCORD_WEBHOOK) return answer(503, "Reports aren't set up on the server.");

    // How often: per sender, and for everyone together (so a crowd can't flood the channel either).
    const ip = request.headers.get("CF-Connecting-IP") || "unknown";
    if (env.PER_SENDER && !(await env.PER_SENDER.limit({ key: ip })).success)
      return answer(429, "Too many reports from you; try again in a few minutes.");
    if (env.EVERYONE && !(await env.EVERYONE.limit({ key: "all" })).success)
      return answer(429, "Too many reports right now; try again in a few minutes.");

    // How big, and what shape.
    if (Number(request.headers.get("Content-Length") || 0) > MAX_BYTES) return answer(413, "The report is too big.");
    const raw = await request.text();
    if (raw.length > MAX_BYTES) return answer(413, "The report is too big.");
    let data;
    try { data = JSON.parse(raw); } catch { return answer(400, "That isn't a report."); }
    if (!data || typeof data !== "object") return answer(400, "That isn't a report.");

    const kind = data.kind === "question" ? "question" : data.kind === "problem" ? "problem" : null;
    const body = text(data.text, MAX_TEXT);
    const replyTo = text(data.replyTo, MAX_REPLY);
    const report = text(data.report, MAX_REPORT);
    const version = typeof data.version === "string" && /^\d{1,3}(\.\d{1,5}){1,3}$/.test(data.version) ? data.version : "?";
    const windows = typeof data.windows === "string" && /^[\d.]{1,30}$/.test(data.windows) ? data.windows : "?";
    if (!kind || body === null || replyTo === null || report === null) return answer(400, "That isn't a report.");

    // The Discord message, built here: no mentions can ping anyone, the name is always the same.
    const payload = {
      username: "CHROVIQ reports",
      allowed_mentions: { parse: [] },
      embeds: [{
        title: kind === "problem" ? `Problem report (CHROVIQ ${version})` : `Question (CHROVIQ ${version})`,
        description: body.trim().length > 0 ? body : "(no text)",
        color: kind === "problem" ? 0xE5484D : 0x6D4BFF,
        fields: [
          { name: "Reply to", value: replyTo.trim() || "(no email given)", inline: true },
          { name: "Windows", value: windows, inline: true },
        ],
        timestamp: new Date().toISOString(),
      }],
    };
    const form = new FormData();
    form.append("payload_json", JSON.stringify(payload));
    if (report.length > 0) form.append("files[0]", new Blob([report], { type: "text/plain" }), "report.txt");

    const sent = await fetch(env.DISCORD_WEBHOOK, { method: "POST", body: form });
    if (sent.status === 429) return answer(429, "Too many reports right now; try again in a few minutes.");
    if (!sent.ok) return answer(502, `It wasn't delivered (Discord said ${sent.status}).`);
    return answer(200, "Sent.");
  },
};
