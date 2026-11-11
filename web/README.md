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

A `Dockerfile` is in the project root (written but **not yet tried**; there was no
Docker on the machine this was built on):

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
- **Orders.** Click one of your units, choose Hold / Move / Support / Convoy, click the
  highlighted target. Only legal orders are offered. Orders are saved as you go; press
  **Lock in** when done. The phase resolves when everyone has locked in (or the timer
  runs out, or the host presses "Resolve now").
- **Secrecy.** The server never sends a player anybody else's orders until the phase
  has resolved. The person running the server could of course read them from the saved
  files, so the host is trusted in this version.
- **Saving.** Every game is written to `web/data/` after each change and reloaded when
  the server restarts. "Copy code" gives the code for the current position; "Copy full
  archive" gives the whole game. Either can be pasted into "Resume from a game code or
  archive" on any Parley server.
- **History.** The numbered buttons under the board show any past phase with every
  order and its result. The host can roll the game back to any of them.
- **Maps.** Every `.pmap` file in `maps/` is offered. Put imported maps in a folder
  called `maps-local/` (git ignores it) and restart. Maps without layout data are drawn
  as a node-and-link diagram.

## Settings

| Environment variable | Meaning | Default |
|---|---|---|
| `PARLEY_URL` | Address to listen on | `http://0.0.0.0:5080` |
| `PARLEY_DATA` | Folder for saved games | `web/data` |

## Testing

`web/e2e/e2e.js` drives three browser sessions through a turn with Playwright
(`node web/e2e/e2e.js /tmp/shot` while the server is running on port 5080).
