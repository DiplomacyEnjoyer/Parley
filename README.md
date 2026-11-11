# Parley

*Working title.* An open-source negotiation board game for VRChat: talk in private side
rooms, enter secret orders by tapping, and everything resolves at once. Any map, any
number of players. Quest (including hand tracking) and Windows desktop play together.

The rules are compatible with classic Diplomacy and are verified against the full DATC
test suite. The name, maps and art are original.

**Where things stand:** see [STATUS.md](STATUS.md). **Why things are the way they are:**
see [DECISIONS.md](DECISIONS.md). **Overall plan:** [PLAN.md](PLAN.md).

## What is here

| Folder | Contents |
|---|---|
| `engine/Parley.Engine` | The rules engine, in the restricted C# that VRChat's UdonSharp compiles. Copy these files (not the `Shim` folder) into the Unity project. |
| `tools/Parley.Tools` | The `parley` command-line tool: map importer, map generator, rule tests, bot simulations. |
| `tests/Parley.Tests` | Automated tests. `tests/datc` holds the DATC rule test cases. |
| `maps/` | Original maps (`.pmap` text files). |
| `web/` | Flat web client for playing over Discord before the VR world exists. |
| `scripts/` | Helper scripts. |

## Trying it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) and, for the rule tests,
Python 3 with pip (only to download reference maps).

Download the reference maps the rule tests need (once, needs internet):

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

## The engine in one paragraph

`MapData` loads a `.pmap` file. `BoardState` is the position. `Adjudicator` takes orders
and resolves a phase. `OptionGenerator` lists what a player may tap next. `GameCodec`
turns a position into a short game code and back. `GameHistory` keeps every phase and
saves the whole game as one line of text. `GameFlow` runs the game: seats, secret order
hand-in, timer, lock-in, resolution, drop/rejoin, resume and roll-back. None of them
knows any particular map or player count.

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

## Licence

Code: [MPL 2.0](LICENSE). Maps and art: [CC BY-SA 4.0](LICENSE-ART).
DATC test cases © Lucas B. Kruijswijk, used under the DATC's own terms.
"Diplomacy" is a trademark of its owner; this project is not affiliated with it.
