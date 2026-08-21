# BitCraft Overlay

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/Z6O024TDK7)

A floating, always-on-top overlay for [BitCraft Online](https://bitcraftonline.com) that shows everything you need in one place, without leaving the game and without stealing window focus:

- **[bitcraftsync.app](https://bitcraftsync.app)** — your team's share link
- **[bitjita.com](https://bitjita.com)** — market/listings
- **[brico.app](https://brico.app)** — recipe database
- **[bitcraftmap.com](https://bitcraftmap.com)** — map (remembers your last view)
- **Twitch** (`twitch.tv/bitcraftonline`) — separate window for watching the stream / drops
- **Calc** — native start/stop rate calculator (e.g. XP/hour), with named save/load history
- **Stats** — snapshot-compare a bitjita.com player's full state (skill XP + XP/h, all inventories, placeables, equipped tools) between two points in time, with named save/load
- **Claim** — look up a claim/settlement by name: sortable member tables for skill levels (tier-colored), armor loadouts (rarity-colored, all saved presets), and per-skill tools (rarity-colored)
- **Route** — live gathering route planner: pick your character and a resource or huntable animal, and it plots the shortest walkable order to visit nearby nodes on the game's own terrain map, routing around water automatically (optional, for fishing). Your position and nearby node positions update live as you move.
- **Custom** — an optional tab for your own URL (e.g. a claim's Google Sheet or website), configured in Settings

## Screenshots

Map overlay next to gameplay:

![Map overlay over the game](docs/minimap.png)

Bitjita market lookup:

![Bitjita market tab](docs/market.png)

Twitch popup window:

![Twitch popup window](docs/Twitch.png)

Settings — per-tab toggles and icon/text display mode:

![Settings window](docs/settings.png)

Route tab — live gathering route, auto-routed around water:

![Route tab](docs/Routes.png)

## Disclaimer

This project is **not affiliated with, endorsed by, or sponsored by Clockwork Labs** (the developer of BitCraft Online) in any way. It's an independent, fan-made community tool.

The source code is fully open and public — for transparency and honesty, so anyone can verify exactly what the app does for themselves. The overlay **does not interfere with the game client in any way**: it doesn't read or modify the game process's memory, doesn't inject any code, and doesn't modify game files. For most tabs, it's simply a separate, independent window displaying publicly available websites next to the game — exactly the same as a regular browser open on a second monitor.

The one exception is the **Route tab's live position tracking**: it reads live player and resource-node positions from [relay.bitcraftsync.app](https://relay.bitcraftsync.app), a public, read-only community mirror of BitCraft's live game data. This app never connects to Clockwork Labs' own servers directly, never touches the game process or its files, and never uses the player's own game login/credentials for this - it's the same kind of public, read-only API call this app already makes to bitjita.com and the others.

## Goal

The main goal is to make the game easier to play for people on a **single monitor** — instead of alt-tabbing to a separate browser (which steals window focus and loads a whole second browser onto weaker hardware), all the community tools you need are available in one lightweight window on top of the game.

## Official BitCraft Online sources

- Game website: https://bitcraftonline.com

## Features

- Draggable bar (tabs + buttons)
- Collapse to just the bar, reset size, resize width/height via the corner grip
- Remembers position, size, and the last URL of each tab
- Option to hide individual tabs, and to show icons instead of text labels, in settings
- Calc tab: start/stop rate calculator with named history
- Stats tab: bitjita.com player snapshot compare (skill XP + XP/h, all items, placeables, equipped tools), named history
- Claim tab: settlement lookup with 3 sortable sub-tabs
  - Members: skill level per column, cell tinted by the game's own tier colors
  - Armor: every member's saved armor presets (tier + rarity colored)
  - Tools: each member's Toolbelt tool per skill, with instrument/charm shown as attached detail
- Route tab: live gathering route planner
  - Search for your character, live player position updates automatically as you move
  - Pick a gathering resource or a huntable animal - nearby nodes appear live on the map
  - Nearest-neighbor + 2-opt route ordering, with an option to prefer dense clusters of nodes over solo ones
  - Pathfinding routes around water by default (toggle off for fishing, where you need to walk into it)
  - Zoom controls, auto-fit framing that matches the overlay window's own shape/size
- Custom tab: your own URL, configured in Settings, with a header button to reload it back to that URL (undoes an in-page link click)

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and Windows (WPF + WebView2 Runtime, usually preinstalled on Windows 11).

```bash
cd BitCraftOverlay
dotnet build
dotnet run
```

Or skip building it yourself and grab a ready-to-run build from the [Releases page](https://github.com/NowakAdmin/bitcraft-overlay/releases) - two options are published with each release:

- **BitCraftOverlay.exe** - self-contained, includes the .NET 8 runtime. Just download and run.
- **BitCraftOverlay-requires-dotnet8.zip** - much smaller, but needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed separately.

## Status

Hobby project, built for the BitCraft community. Bug reports / ideas welcome via Issues.
