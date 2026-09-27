# NEWS//WATCH

WPF (.NET 8) news feed monitor for Windows. Watches RSS, Atom, RSS 1.0 (RDF) and JSON Feed sources on a per-feed interval and slides a panel in at the screen edge whenever a new story appears — headline, summary, thumbnail, in the feed's own colour. Runs from the tray.

A sibling of [PULSE//WATCH](https://github.com/adamwoodland2/pulse-watch): same look, same tray and overlay behaviour. Panels appear on the **left** by default so they never collide with PULSE//WATCH's tiles on the right.

## Getting started

Windows 10/11. To build you need the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`); `build.ps1` finds it even if your terminal's PATH predates the install.

```
.\build.ps1 -SelfContained         # x64, portable single exe (~150 MB), no .NET needed on the target  -> dist\
.\build.ps1                        # x64, smaller exe; target needs the .NET 8 Desktop Runtime          -> dist\
.\build.ps1 -Arm64                 # Windows on ARM (add -SelfContained for a portable exe)             -> dist-arm64\
.\build.ps1 -DebugBuild            # Debug build only (or just `dotnet run` while developing)
```

Then run `dist\NewsWatch.exe`. A fresh install starts with one feed, BBC News (`https://feeds.bbci.co.uk/news/rss.xml`). Its first check is silent — the stories already in the feed are remembered, not announced — so the first panel appears when the BBC publishes something new. Tick **Start automatically when I log in** in Settings to have it launch minimised at login.

## Command line

- `--minimized` (or `/min`) — start hidden in the tray; feeds are checked and panels shown as normal.
- `--monitor N` (also `--monitor=N`, `/monitor:2`) — show panels on screen N (1-based, as listed by Windows). Invalid/missing N falls back to the primary screen.
- `--settings <file>` (also `--settings=<file>`) — use a different settings file instead of `%APPDATA%\NewsWatch\settings.json`, e.g. for a separate profile or a portable copy. `seen.json` lives next to it.

Only one instance runs per user; a second launch shows a notice and exits. NEWS//WATCH and PULSE//WATCH run side by side without interfering.

## Feeds

**+ ADD FEED** (or double-click empty space). Each feed has:

| Field | |
|---|---|
| **Feed URL** | The feed itself, or just a news site's address: if the URL returns a web page, the feed it advertises (`<link rel="alternate">`) is found and used — e.g. `bbc.co.uk/news` becomes `https://feeds.bbci.co.uk/news/rss.xml`. `feed://` links and addresses without `https://` are fine. |
| **TEST** | Fetches it now and shows the format, story count and newest date, fills in the display name from the feed's title, and puts the newest headline in the preview. SAVE runs the same check for a new or changed URL; if it fails you can **SAVE ANYWAY** and it keeps trying on schedule. |
| **Check every** | 1–1440 minutes (default 5). |
| **Only stories mentioning / Skip stories mentioning** | Comma-separated words or phrases, matched case-insensitively as whole words in the headline and summary (`art` won't match `start`). Blank = every story. The list shows `+` / `−` next to a filtered feed. |
| **Panel colour** | Hex, a quick-pick chip, or click the swatch for any colour. Blank = the default colour from Settings. A live preview shows the panel as it will appear. |
| **Chime** | A short, quiet three-note rising chime per batch of new stories (deliberately different from PULSE//WATCH's pings). |
| **Active** | Untick to pause the feed. |

Double-click a row (or EDIT) to change a feed; REMOVE is in the dialog too and needs a second click within 3 s. Drag rows to reorder. **↻ CHECK NOW** fetches every active feed immediately.

The list shows each feed's status (`LIVE` / `ERROR` / `PENDING` / `PAUSED`), format and story count (`RSS · 32`) or failure code, interval and last check time.

**Some feeds to try:** Guardian `https://www.theguardian.com/uk/rss`, Ars Technica `https://feeds.arstechnica.com/arstechnica/index`, Hacker News `https://hnrss.org/frontpage`, a subreddit `https://www.reddit.com/r/worldnews/.rss`, Google News `https://news.google.com/rss?hl=en-GB&gl=GB&ceid=GB:en` (or a search: `https://news.google.com/rss/search?q=your+topic`).

## What counts as a new story

- **Seen stories are remembered** per feed in `seen.json`. A story is "seen" if either its id (`guid` / `id`) or its link has been seen before. Links are compared without tracking parameters (`utm_*`, `at_*`, `fbclid`, …) and fragments. This matters for feeds like the BBC's, which bump a story's guid (`…#1` → `…#2`) every time it's edited: an edit doesn't pop the same story again.
- **First check of a new feed is silent.** Everything already in it is remembered. Changing a feed's URL counts as a new feed.
- **Catch-up after launch.** On the first check after NEWS//WATCH starts, stories published while it was closed get at most **3 panels per feed** (Settings; 0 = none). The rest go straight to History as `CATCH-UP`.
- **Steady state.** At most **4 panels per feed per check** (Settings); any more become a single "+N more new stories" panel, and all of them are in History.
- **Old stories are skipped.** Anything dated more than **24 hours** ago (Settings; 0 = no limit) never pops up, so a feed that re-surfaces old items stays quiet.
- **Duplicates across feeds pop once.** If two feeds carry the same link within 12 hours (say BBC top stories and BBC World), only the first gets a panel.

## Panels and the tray

- Panels slide in at the left edge of the chosen screen (or the right; see Settings), newest on top, in a small always-on-top overlay that exists only while panels are showing and is sized to them — nothing sits over your desktop or games the rest of the time. They appear even when the app is minimised and never steal focus.
- Each panel shows the feed name in its colour, the story's time and age (`12:25 · 42 min ago`), the headline (up to three lines), the summary (two lines, optional) and the story's thumbnail when the feed has one (optional).
- **Click a panel to dismiss it.** Hovering holds it on screen; moving away gives it 5 more seconds. Otherwise it leaves after the panel time (default 15 s). Beyond the on-screen limit (default 5) the oldest slide away to make room. To read a story, double-click it in History.
- Tray icon: a cyan page when all active feeds are fine, red while any is failing; the tooltip shows the version and the failing count. Right-click for Open, History, Check all feeds now, Mute sounds, Hold panels (off / until re-enabled / 15 m / 30 m / 1 h / 2 h / 12 h — feeds are still checked and stories still go to History, only panels are held), Exit.
- Minimising hides to the tray; with "X minimises to tray" on, closing does too.

## History

**HISTORY** (top right, or in the tray menu) lists every new story since the app started, newest first, updating live: when it was seen, the feed (in its colour), the headline, when it was published, and how it was shown — `PANEL`, `OVERFLOW` (over the per-check limit), `CATCH-UP` (over the catch-up limit) or `HELD` (panels were on hold).

- **Search** filters by headline, feed or summary.
- **Double-click** a story (or **OPEN STORY**) to open it in your browser. Only `http`/`https` links are ever opened.
- **EXPORT CSV** saves `Date,Time,Feed,Published,Title,Link,Shown`, oldest first (UTF-8, opens cleanly in Excel; headlines that start with `=`, `+`, `-` or `@` are neutralised so they can't run as formulas).
- Memory only: the list starts fresh each launch. The most recent 5,000 stories are kept.

## How fetching works

- One loop per active feed, each starting at a random 0.3–4 s offset so feeds don't all fetch in one burst.
- **Polite:** conditional GET (`If-None-Match` / `If-Modified-Since`), so an unchanged feed costs a `304 Not Modified` and no download. gzip/deflate/brotli accepted. Connections are reused across feeds on the same host. Uses the system proxy.
- **Dual-stack like a browser:** IPv6 and IPv4 addresses are raced (Happy Eyeballs, 250 ms head start each), so a network where one family is silently broken — phone tethering, flaky ISP IPv6 — doesn't turn every check into a `TIMEOUT`.
- **Recovers quickly:** after a network failure (`TIMEOUT`, `DNS`, `UNREACH` …) a feed retries at 15 s, 30 s, 60 s … instead of waiting out its interval, and retries straight away when Windows reports a network change (new Wi-Fi, cable plugged in, waking from sleep). Server errors (`HTTP 404`, `HTTP 503` …) wait for the normal interval.
- **Bounded:** 20 s timeout per fetch, 8 MB cap per feed, 3 MB / 10 s per thumbnail. Thumbnails are downloaded only when a panel shows one.
- **Tolerant parsing:** elements are matched by local name, so feeds with missing or wrong namespaces still parse. HTML entities that XML doesn't define (`&nbsp;`, `&mdash;` …) are repaired, legacy encodings (windows-1252, ISO-8859-x) work, and RFC 822 dates with zone names (`EDT`, `BST` …) are understood. Headlines and summaries are reduced to plain text; a summary that only repeats the headline (Google News) is dropped.
- **Safe parsing:** DTDs and external entities are never processed (no XXE), and only absolute `http`/`https` links and images are used.

**Failure codes** (shown in the list while a feed is failing):

| Code | Meaning |
|---|---|
| `TIMEOUT` | no complete answer within 20 s |
| `DNS` | the server name doesn't resolve |
| `REFUSED` / `UNREACH` / `CONN FAIL` / `RESET` / `CONN LOST` | couldn't connect, or the connection dropped |
| `CERT` / `TLS` | HTTPS certificate rejected / secure connection failed |
| `HTTP 404` etc. | the server answered with an error status |
| `NOT A FEED` | the address returned something that isn't RSS, Atom, RDF or JSON Feed |
| `NO FEED ON PAGE` | (dialog only) a web page that doesn't advertise a feed |
| `BAD XML` / `BAD JSON` | too broken to read, even after repairs |
| `TOO BIG` | response over 8 MB |
| `APP ERROR` | an unexpected error handling the feed; details in `errors.log` in the config folder |

## Settings

⚙ SETTINGS: X-minimises-to-tray, confirm-before-exit, auto-start at login (minimised); panel side (left by default), panel time, panels on screen, panels per feed per check, catch-up per feed, maximum story age, show summaries, show thumbnails; the default panel colour (with picker); version and licence.

Everything — settings and feeds — lives in one JSON file, `%APPDATA%\NewsWatch\settings.json` by default (Config Folder link on the main screen) or whatever `--settings` points at, for easy backup or moving between machines. `seen.json` beside it remembers which stories each feed has produced (delete it and every feed's next check is silent again). Auto-start is the one setting kept in the registry instead, since it embeds the exe path.

Loading is defensive: out-of-range numbers are clamped, invalid colours reset, duplicate feed IDs regenerated, a feed with an invalid URL paused with a warning, and an unreadable file preserved as `settings.json.corrupt`. Saves are atomic.

## Licence

MIT — see `LICENSE`.
