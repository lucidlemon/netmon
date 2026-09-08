# NetMon Stream Deck plugin

Shows live ping or jitter for one of NetMonGui's monitored connections on a
Stream Deck key — color-coded using the same shooters/MOBA/casual (ping) and
excellent/good/noticeable (jitter) tiers as the app's own charts.

It talks to NetMonGui over a tiny local JSON API
(`http://127.0.0.1:47115/api/status`, see
[`NetMonGui/Services/StatsApiService.cs`](../NetMonGui/Services/StatsApiService.cs))
that the app starts automatically whenever it's running — nothing to
configure on that side, and the API only ever listens on loopback.

## Build

```bash
npm install
npm run build
```

This bundles `src/` into `com.danielwinter.netmon.sdPlugin/bin/plugin.js`.
Run `npm run watch` instead while iterating — it rebuilds on save (the Stream
Deck app picks up a changed `bin/plugin.js` the next time the action appears,
or after `streamdeck restart com.danielwinter.netmon` if you have the
[Stream Deck CLI](https://docs.elgato.com/streamdeck/sdk/guides/cli/) installed).

## Install

NetMonGui must be running for the key to show real data (it'll show an
"offline" tile otherwise).

**Manual (no CLI needed):** copy or symlink the whole
`com.danielwinter.netmon.sdPlugin` folder into your Stream Deck plugins
directory, then restart the Stream Deck app:

```powershell
Copy-Item -Recurse -Force .\com.danielwinter.netmon.sdPlugin "$env:APPDATA\Elgato\StreamDeck\Plugins\"
```

**With the Stream Deck CLI:** `npm i -g @elgato/cli`, then from this folder:

```bash
streamdeck link com.danielwinter.netmon.sdPlugin
streamdeck restart com.danielwinter.netmon
```

Then in the Stream Deck app, drag the **NetMon → Connection Stat** action
onto a key, and in its property inspector pick a connection (populated from
NetMon's live adapter list) and Ping or Jitter. Add another instance of the
action for each connection/metric you want on its own key.

## How it's verified

I don't have physical Stream Deck hardware to test against here, so this was
verified by scripting a minimal fake Stream Deck WebSocket server that speaks
the same registration/`willAppear`/`setImage` protocol the real app does, and
confirming the built plugin registers, polls NetMon's live API, and pushes
back correctly color-coded tiles. It has *not* been through an actual Stream
Deck app — if a key misbehaves in practice, check `streamdeck dev` logs
(`~/Library/Logs/ElgatoStreamDeck` on macOS or
`%APPDATA%\Elgato\StreamDeck\logs` on Windows) first.

## Project layout

```
src/
  plugin.ts              entry point — registers the action, connects
  actions/connection-stat.ts   polling + event handling for the key
  lib/netmon-api.ts       fetches NetMonGui's /api/status
  lib/tile.ts              renders the key's SVG tile
com.danielwinter.netmon.sdPlugin/
  manifest.json
  bin/plugin.js           built output (gitignored)
  ui/connection-stat.html property inspector (connection + metric picker)
  imgs/                    icons (gen-icons.py regenerates these)
```
