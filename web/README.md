# Parley web client

A flat, 2D way to play a real game with friends before the VR world exists: a map, tap
(click) order entry that mirrors the planned VR panel (unit, then order type, then
target), secret orders, simultaneous resolution, history, roll-back, and the same game
codes the VR world will use. It runs the exact same rules engine.

It is a test bench, not a polished product. Voice and negotiation happen in Discord.

## Running it on your own computer

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. In the project folder run:

```bash
dotnet run --project web/Parley.Web
```

3. Open http://localhost:5080 in a browser. Create a game, join it with your name,
   take a power, and send your friends the game's address.

Friends outside your home network cannot reach `localhost`. The simplest fix is a
tunnel, which gives you a temporary public address without touching your router.
With [cloudflared](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/)
installed:

```bash
cloudflared tunnel --url http://localhost:5080
```

It prints an address ending in `trycloudflare.com`; send friends that address plus
`/?g=...` for your game. The game lives on your computer, so leave it running.

## Running it on a server

A `Dockerfile` is in the project root (built and tested in the second session). Run
`./scripts/fetch-reference-maps.sh` first if you want the classic board in the image;
the image then contains third-party maps, so keep it to yourself (THIRD_PARTY.md).

```bash
docker build -t parley .
```

```bash
docker run -p 5080:5080 -v parley-data:/data parley
```

## How it behaves

- **Seats.** The first person to join a new game is its host. Everyone picks a power in
  the lobby; the host starts the game. Powers nobody takes simply hold every turn.
- **Identity.** After joining, your browser remembers you for that game. "Invite and
  save" has a personal link if you want to continue on another device. Do not share it:
  whoever has it can see and change your orders.
- **A turn.** Everyone presses **Ready**; the clock starts (by default 30 minutes for the
  first two movement phases, then 15; 2 minutes for retreats, 3 for builds). Click one of
  your units, choose Hold / Move / Support / Convoy, click the highlighted target. Only
  legal orders are offered. Flip **Submitted** on when you are done (off again to keep
  editing). If everyone is submitted the phase ends early; at zero, whatever is on each
  sheet is used and units with nothing entered hold. A chime plays, the orders are read
  out one at a time, country by country, then the result is shown.
- **Secrecy.** Your sheet stays in your browser. Submitting sends only a sealed
  fingerprint; the orders reach the server when every sheet is sealed, and are checked
  against the seals. Not even whoever runs the server can see orders early.
- **Host.** The host can pause the clock (for real-life interruptions), end a waiting
  stage early, change the clock, free seats and roll back. If the host's browser goes away,
  the host role passes to another player.
- **Saving.** Every game is written to `web/data/` after each change and reloaded when
  the server restarts. Every player's browser also keeps a copy of the whole game: if the
  server loses it, opening the game's address offers "Resume it here". "Copy code" gives the code for the current position; "Copy full
  archive" gives the whole game. Either can be pasted into "Resume from a game code or
  archive" on any Parley server.
- **History.** The numbered buttons under the board show any past phase with every
  order and its result. The host can roll the game back to any of them.
- **Maps.** `scripts/fetch-reference-maps.sh` installs the classic board (the default) and
  two variants with their pictures into `maps-local/`. Every `.pmap` in `maps/` and
  `maps-local/` is offered; a `name.art.json` next to `name.pmap` gives it a picture.
  Maps without one are drawn as a node-and-link diagram.

## Settings

| Environment variable | Meaning | Default |
|---|---|---|
| `PARLEY_URL` | Address to listen on | `http://0.0.0.0:5080` |
| `PARLEY_DATA` | Folder for saved games | `web/data` |

## Testing

`web/e2e/playtest.js` plays whole multi-player games through the page with Playwright
(see the comment at its top for how to run it).

| Environment variable | Meaning | Default |
|---|---|---|
| `PARLEY_AWAY_SECONDS` | A browser silent this long counts as away | 45 |
