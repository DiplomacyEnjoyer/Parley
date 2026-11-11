# Parley — plan

*Parley* is the working title for an open-source, VR-first negotiation board game for
VRChat: simultaneous secret orders, private side rooms for deal-making, any map, any
number of players. The rules are compatible with the classic Diplomacy rules (verified
against the DATC test suite); the name, art and shipped maps are original.

## Shape of the project

| Folder | What it is | Runs where |
|---|---|---|
| `engine/Parley.Engine` | The rules: maps, board state, adjudicator, legal options, game codes, game flow. Written in the restricted C# that UdonSharp can compile. | Unity/VRChat **and** normal .NET |
| `tools/Parley.Tools` | Command-line tool: map importer, DATC runner, bot simulations, balance stats. Normal modern C#. | .NET only |
| `tests/Parley.Tests` | Automated tests, including the full DATC and a check that the engine stays inside the UdonSharp subset. | .NET only |
| `web/` | Simple flat web client so a real game can be played over Discord before VR exists. | .NET server + browser |
| `maps/` | Original maps in the `.pmap` text format (CC BY-SA 4.0). | data |
| `third_party/` | Downloaded reference material (git-ignored, never committed). | local only |

## Order of work (this phase: everything that needs no Unity or headset)

1. **Rules engine on a generic map format, passing the DATC.**
2. **Game codes**: compact text saves with full phase history; resume from any code.
3. **Legal-option generation** (what can be tapped next) and the **game flow state machine**
   (phases, timer, lock-in, drop/rejoin, resume).
4. **Everything else**: more importers, bot simulations and balance statistics, web client.

Later phases (not started): the Unity/VRChat world, hand-tracking order panels, private
audio rooms, network sync, Quest performance work, the scripted intro.

See `STATUS.md` for where things stand and `DECISIONS.md` for every judgement call.
