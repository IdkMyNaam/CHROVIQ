# Making a CHROVIQ widget

A CHROVIQ widget is a small web page on your desktop: HTML, CSS and JavaScript in a folder, with a `widget.json`
that says what it is and what it may use. CHROVIQ shows it in a see-through widget window (move it, resize it from
the bottom-right corner, right-click for its settings) and gives it live data.

## Where widgets live

- Your own: `%AppData%\WindowsCustomizer\Widgets\<name>\` (in CHROVIQ: Desktop widgets > Add widget > Open the
  widgets folder).
- The ones that come with CHROVIQ: the `Widgets` folder next to the app. They're good examples to copy.

Make a folder with `widget.json` and `index.html`, then choose it under **Add widget**. Turn on **Developer tools
(F12)** in the widget's settings while you work on it: F12 opens the browser's tools, F5 reloads the page.

To share a widget, use **Export (.zip)** in its settings; others import it with **Add widget > Import a widget**.

## widget.json

```json
{
  "name": "My widget",
  "author": "Your name",
  "version": "1.0",
  "description": "What it shows.",
  "entry": "index.html",
  "width": 300,
  "height": 160,
  "background": "#99202020",
  "data": ["stats", "media", "weather"],
  "network": ["http://localhost:4455", "https://api.example.com"],
  "files": [{ "id": "log", "label": "The log file to show" }],
  "commands": [{ "id": "temp", "label": "GPU temperature", "run": "nvidia-smi --query-gpu=temperature.gpu --format=csv,noheader", "everySeconds": 10 }]
}
```

Everything except `name` is optional. Ask only for what the widget needs: people see the list before they add it.

| Field | What it does |
|---|---|
| `entry` | The page to show, inside the folder (default `index.html`). |
| `width`, `height` | The size it starts with (people can resize it). |
| `background` | Its starting background color, `#AARRGGBB` (`#00000000` for none). |
| `data` | Live data from CHROVIQ: `stats` (CPU, memory, GPU), `media` (what's playing, with controls), `weather`. |
| `network` | Addresses it may connect to: `http`, `https`, `ws` or `wss`, with the port if there is one. Everything else is blocked. |
| `files` | Files it wants to read. People choose the file in the widget's settings; the widget can't read anything else. |
| `commands` | Commands whose output it shows. They only run once the person approves them (see below). |

## In the page: `chroviq`

CHROVIQ adds a `chroviq` object to the page before your scripts run.

```js
chroviq.on('stats', s => {
  // { cpu: 12, memory: 48, memoryUsedGb: 7.6, memoryTotalGb: 15.9, gpu: 3 }
});

chroviq.on('media', m => {
  // { active: true, playing: true, title: "…", artist: "…", cover: "data:image/png;base64,…" }
});
chroviq.media.playPause();  // also chroviq.media.next(), chroviq.media.previous()

chroviq.on('weather', w => {
  // { place, temperature, unit: "°C", description, wind, code, days: [{ date, max, min, description, code }] }
  // or { error: "…" } (the place is set in the widget's settings)
});

chroviq.on('file', f => {
  // { id: "log", text: "…", modified: "2026-…" }, again every time the file changes
  // or { id, missing: true, reason: "…" }
});

chroviq.on('command', c => {
  // { id: "temp", output: "…", error: "", exitCode: 0 }
  // or { id, blocked: true, reason: "…" } when it isn't allowed
});
chroviq.run('temp');  // runs an approved command now

chroviq.on('theme', t => {
  // { text: "#FFFFFF", accent: "#0078D4", font: "Segoe UI", fontSize: 14, locked: false }
});
```

`on` also gives you the latest message right away if one already came, so the order doesn't matter.

The widget's colors and font are also CSS variables: `var(--chroviq-text)`, `var(--chroviq-accent)`,
`var(--chroviq-font)` and `var(--chroviq-font-size)`. Keep the page background transparent: the widget's own
background (and its material) is drawn behind it.

Pressing the widget anywhere that isn't a button, link, field or an element with `data-no-drag` moves it. Set
`chroviq.noDrag = true` to turn that off. Right-click opens the widget's menu; `chroviq.menu()` opens it too.

## What a widget can't do

A widget runs in Microsoft Edge's sandbox (WebView2), and CHROVIQ adds its own limits:

- It only loads its own files (from its folder) and the addresses in `network`. Everything else is blocked,
  WebSockets included.
- It can't read files on the PC (only the ones the person chose for it), start programs, open windows, download,
  or use the camera, microphone, location or notifications.
- Links someone clicks open in their browser, not in the widget.

## Commands

Commands are powerful, and so they're guarded:

- They're **off for all widgets** until the person turns on "Let widgets run commands" in a widget's settings.
- Each widget's commands must also be **approved** by the person, who sees each command word for word, who made the
  widget, and where it came from (imported widgets get a warning).
- If the commands in `widget.json` change, the approval no longer counts: they stop until they're approved again.
- A command runs in the widget's folder through `cmd.exe`, may take 15 seconds at most, and its output is cut at
  64 KB. `everySeconds` is 2 to 3600 (0 = only when the page calls `chroviq.run`).

Prefer `network` or `files` when the other program can give its data that way: they don't need commands at all.

## Connecting to other programs

- **A local web address:** many programs share their data at an address on your own PC (OBS's WebSocket,
  Home Assistant, LibreHardwareMonitor's web server, a game's live stats). Put that address in `network` and use
  `fetch` or `WebSocket`.
- **A file:** if the program writes its data to a file (a log, a "now playing" text file, JSON), ask for it in
  `files` and read it in `chroviq.on('file', …)`.
- **A command:** for anything else, a command's output (with the person's approval).

## Examples

The widgets that come with CHROVIQ are small and readable:

- **Analog clock:** drawing with a canvas, using the widget's colors; no data and no network.
- **System monitor:** `stats`, shown as rings.
- **Now playing:** `media`, with the cover and controls.
