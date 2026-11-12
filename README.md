# Parley

*Working title.* An open-source negotiation board game for VRChat: talk in private side
rooms, enter secret orders by tapping, and everything resolves at once. Any map, any
number of players. Quest (including hand tracking) and Windows desktop play together.

The rules are compatible with classic Diplomacy and are verified against the full DATC
test suite. The classic board is the default map: its data and picture are downloaded
at setup time from existing open-source fan projects (see below and
[THIRD_PARTY.md](THIRD_PARTY.md)); nothing of theirs is committed here.

**Where things stand:** see [STATUS.md](STATUS.md). **Why things are the way they are:**
see [DECISIONS.md](DECISIONS.md). **Overall plan:** [PLAN.md](PLAN.md).

## What is here

| Folder | Contents |
|---|---|
| `engine/Parley.Engine` | The rules engine, in the restricted C# that VRChat's UdonSharp compiles. Copy these files (not the `Shim` folder) into the Unity project. |
| `tools/Parley.Tools` | The `parley` command-line tool: map importer, map generator, rule tests, bot simulations. |
| `tests/Parley.Tests` | Automated tests. `tests/datc` holds the DATC rule test cases. |
| `maps/` | Generated "Ring" test maps (`.pmap` text files). |
| `docs/` | Design notes: the VR world, variant research, [secret orders and dice](docs/secret-orders-and-dice.md), [the Renaissance ruleset's rulings](docs/machiavelli-rulings.md), [fog of war](docs/fog-of-war.md), [VRChat sync and recovery](docs/vrchat-sync-design.md). |
| `web/` | Flat web client for playing over Discord before the VR world exists. |
| `scripts/` | Helper scripts. |

## Getting the classic map working (step by step)

You need: [.NET 8 SDK](https://dotnet.microsoft.com/download), Python 3 with `pip`, and
an internet connection for step 2.

1. Get the code:

   ```bash
   git clone https://github.com/DiplomacyEnjoyer/Parley.git
   ```

   ```bash
   cd Parley
   ```

2. Download the classic board (and a few variants) and install it for the web client.
   This fetches the open-source Python `diplomacy` package, copies its map files and board
   pictures into `third_party/`, converts them, and writes the result into `maps-local/`:

   ```bash
   ./scripts/fetch-reference-maps.sh
   ```

   On Windows, run it from Git Bash or WSL. It should end with
   `wrote maps-local/standard.art.json: 75 province shapes, 81 unit positions, 34 centre markers`.

3. Start the web client:

   ```bash
   dotnet run --project web/Parley.Web
   ```

4. Open http://localhost:5080. "Standard" is already selected as the map. Press
   **Create game**, enter your name, take a power, and send the page address to friends.
   (Playing with friends outside your home network: see [web/README.md](web/README.md).)

If the map list shows only the "Ring" maps, step 2 did not finish: run it again and
read its last lines. The Ring maps are generated test maps, not launch maps.

## Trying it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) and, for the rule tests,
Python 3 with pip (only to download reference maps).

Download the reference maps the rule tests need (once, needs internet; this is step 2 above):

```bash
./scripts/fetch-reference-maps.sh
```

Run all tests:

```bash
dotnet test
```

Run just the DATC rule cases and see a one-line summary:

```bash
dotnet run --project tools/Parley.Tools -- datc
```

Play 200 bot games on a map and print per-power results:

```bash
dotnet run --project tools/Parley.Tools -- sim maps/ring5.pmap 200
```

Make an original map for 9 players:

```bash
dotnet run --project tools/Parley.Tools -- gen-ring 9 maps/ring9.pmap
```

Convert a community map from DPjudge format (check its licence before sharing it):

```bash
dotnet run --project tools/Parley.Tools -- import-dpjudge third_party/maps/dpjudge/modern.map modern.pmap
```

Start the web client (then open http://localhost:5080; see [web/README.md](web/README.md)):

```bash
dotnet run --project web/Parley.Web
```

Play random games of the Renaissance ruleset ("Purse and Dagger") with consistency checks,
on the small test board or on the fetched Italy board:

```bash
dotnet run --project tools/Parley.Tools -- purse-sim tests/purse/valleys.pmap 50
```

```bash
dotnet run --project tools/Parley.Tools -- purse-sim maps-local/italy1454.pmap 20
```

To play it in the web client, start the server and choose "Italy 1454" as the map (the
fetch script installs it; it is drawn as dots and lines, because Parley has no picture
of that board).

Automated browser playtest (whole games through the page; needs Node and Playwright):

```bash
dotnet build web/Parley.Web
```

```bash
node web/e2e/playtest.js standard ring5 valleys
```

## The engine in one paragraph

`MapData` loads a `.pmap` file. `BoardState` is the position. `Adjudicator` takes orders
and resolves a phase. `OptionGenerator` lists what a player may tap next. `GameCodec`
turns a position into a short game code and back. `GameHistory` keeps every phase and
saves the whole game as one line of text. `OrderSeal` seals a secret order sheet
(SHA-256 commit and reveal) so nobody's machine sees anyone's orders early.
`Announcement` lists the orders country by country for the announcer, with where each
piece is heading and what became of it. `GameFlow` runs the game: seats, the timer
schedule, submit toggle, sealing and opening, announcement, ready-up, pause, drop/rejoin,
resume and roll-back. None of them knows any particular map or player count.

**Rulesets.** The game flow reaches the rules only through `Ruleset`. `StandardRules` is
the standard game; `PurseRules` is "Purse and Dagger", the Renaissance game with money,
bribes, garrisons, sieges, rebellions, assassins, famine and plague
([rulings](docs/machiavelli-rulings.md)). `Adjudicator` is the referee both share.
`Dice` rolls from a seed that every player's sealed sheet contributes to
([how](docs/secret-orders-and-dice.md)). `Visibility` says what a power sees under fog
of war ([what that can and cannot hide](docs/fog-of-war.md)).

## Map format (`.pmap`)

```
parleymap 1
id tiny
name Tiny Example
start 1
power red Red #c8553d Redland
power blue Blue #2f6690 Blueland
prov rc l red Red Capital        # l = inland, c = coastal, s = sea; then home power, * (neutral centre) or -
prov rp c red Red Port
prov mid c * Midway
prov bp c blue Blue Port
prov bc l blue Blue Capital
prov sea s - The Sound
army rc: rp mid                  # armies can move between these (both ways)
army bc: bp mid
army rp: mid
army bp: mid
fleet sea: rp mid bp             # fleets can move between these (both ways)
fleet rp: mid
fleet bp: mid
unit red A rc
unit red F rp
unit blue A bc
unit blue F bp
```

Provinces with two separate coasts add `coast spa nc` / `coast spa sc` lines and name
the coast in fleet links (`fleet mao: spa/nc spa/sc`). Optional `xy` and `shape` lines
give label positions and outlines for boards. `victory N` and `build any` override the
defaults (more than half the centres; build in home centres only).

Optional lines for other calendars and rulesets: `seasons Spring Summer Fall* Winter`
(movement seasons in order, a star after those at whose end centres change hands, then
the name of the adjustment time), `rules purse famine loans ...` (the ruleset and its
default options; no line means the standard game), `city <prov> walled|open <income>
[port] [single]`, `convoy coastal` (fleets in coastal provinces may convoy), and
`x ...` lines that only a particular ruleset reads (tables). `tests/purse/valleys.pmap`
is a small complete example.

## Licence

Code: [MPL 2.0](LICENSE). Parley's own maps and art: [CC BY-SA 4.0](LICENSE-ART).
Downloaded maps and pictures are other people's work: [THIRD_PARTY.md](THIRD_PARTY.md).
DATC test cases © Lucas B. Kruijswijk, used under the DATC's own terms.
"Diplomacy" is a trademark of its owner; this project is not affiliated with it.
