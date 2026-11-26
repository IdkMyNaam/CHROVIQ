# CHROVIQ report relay

A small Cloudflare Worker between CHROVIQ's "Report a problem" / "Ask a question" and the Discord channel the
reports go to. The Discord webhook is a secret in Cloudflare, so it is never inside the app, where anyone could pull
it out.

What the relay does:

- Accepts only `POST /report` with a report-shaped JSON body (kind, text, reply address, version, Windows version,
  and report.txt with the logs).
- Builds the Discord message itself: the name is always "CHROVIQ reports", and nothing in a report can ping anyone.
- Limits sizes (the whole request 512 KB, the text 3,500 characters).
- Limits how often: 2 reports a minute per sender, 20 a minute for everyone together.

Someone who finds the relay's address can at most send a few report-shaped messages a minute. They can't post their
own names, pictures or pings, and they can't rename or delete the webhook.

## Setting it up

Needs Node.js (for `npx`) and a Cloudflare account (the free plan is enough).

1. In Discord, make a **new** webhook for the reports channel (Channel settings > Integrations > Webhooks), and copy
   its URL. Use a new one: the old webhook is inside CHROVIQ 1.1.0 and 1.2.0, so it should be deleted once the
   relay works.
2. In this folder:

   ```powershell
   npx wrangler login
   npx wrangler secret put DISCORD_WEBHOOK
   npx wrangler deploy
   ```

   `secret put` asks for the value: paste the webhook URL there (it isn't stored in any file).
   The relay runs at `https://reports.chroviq.com/report` (set in `wrangler.toml`; Cloudflare makes the DNS record
   and the certificate itself, since chroviq.com is on Cloudflare). That address is `ReportSender.RelayUrl` in
   Main/ReportSender.cs.
3. Once a report through the relay arrives, delete the old webhook in Discord.

## If it's ever abused

Lower the limits in `wrangler.toml` and run `npx wrangler deploy` again. To stop reports completely for a while,
delete the secret (`npx wrangler secret delete DISCORD_WEBHOOK`): the relay then answers that reports aren't set up.
