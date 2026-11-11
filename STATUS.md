# Status

*Last updated during the second session (turn flow, sealed orders, classic map).*

## Read this first

1. **The classic board is now the default map.** Run `./scripts/fetch-reference-maps.sh`
   once; it downloads the map and its picture from the open-source Python `diplomacy`
   package and installs them. Step by step: README, "Getting the classic map working".
   Nothing downloaded is committed; licences in THIRD_PARTY.md.
2. **The turn flow is your design**: timer schedule (30, 30, then 15 minutes), private
   order sheet, submit toggle, early finish, automatic hand-in at zero, host pause,
   end-of-round sound, announcement country by country, instant adjudication, ready-up.
   Built in the engine and playable in the web client.
3. **Secret orders are tamper-proof.** Sheets never leave a player's device until every
   sheet is sealed (SHA-256 commit and reveal). The web server cannot see orders early either.
4. **One thing for you to do by hand:** delete the old `initial-engine` branch on GitHub
   (Branches page, bin icon). It is fully merged into `main`; this session was not
   allowed to delete branches.
5. **One open question:** how pausing should work in the end (DECISIONS §21).

## Done and tested

| Piece | State | How it is tested |
|---|---|---|
| Map format, any map / any player count | Done | 11 generated maps and 14 imported community maps; 2 to 70 powers |
| Rules engine | Done | **DATC v3.0: all 164 cases pass.** Cross-checked against the Python engine on 20,000 random movement situations (DECISIONS §7) |
| Map importer (DPjudge `.map`) | Done | All 14 reference maps convert, load and play |
| Classic board picture in the web client | Done | `parley import-svg` matches the picture to the map; classic, Modern, Ancient Mediterranean all match 100%; test checks coverage and that no unsafe markup survives |
| Game codes and full-history archive | Done | Round-trip, damage detection; archive now stores the timer schedule |
| Legal-option generation | Done | "Options offered = orders the referee accepts", thousands of orders, 4 maps |
| Turn flow (schedule, submit toggle, early finish, auto hand-in, pause, announcement, ready-up) | Done | `TurnFlowTests`: 21 scenarios including every sealed-order edge case |
| Sealed orders (`OrderSeal`, SHA-256 in the UdonSharp subset) | Done | Test vectors; matches .NET SHA-256 on 200+ inputs; cheating attempts refused |
| Announcement data (orders by country, intended moves, results, battles) | Done | Movement, retreat and build phases |
| Web client on the new flow | Working | Three-browser smoke test of a full sealed turn. Full automated playtest suite: in progress (see Next steps) |
| UdonSharp-subset guard | Done | Covers the new engine files too |

Test count: 270 (`dotnet test`, after running the fetch script).

## Decisions you made this session (recorded in DECISIONS.md)

Classic map default, fetched at setup (§2); resume and roll-back host only, every client
keeps the save (§8); tamper-proof orders (§10, §18); newest DATC convoy ruling (§7);
name stays "Parley" (§1); contributor terms not needed yet (§14); Ring maps are test
maps only (§12).

## Next steps, in your priority order

- [x] Step 0: build and tests pass. Branch deletion blocked (see above).
- [x] Classic map data and art, default in the web client, README steps, THIRD_PARTY.md.
- [x] Turn flow and tamper-proof orders (engine and web client).
- [x] VR design notes: `docs/vr-world-design.md`.
- [ ] **Automated playtesting through the web client** (in progress): multi-player games
      covering timers expiring with partial orders, early finish, submit on/off, pause and
      resume, ready-up, players dropping and rejoining, host dropping, resuming from a
      code, roll-back, and the sealed exchange; on the classic map and imported variants.
      Planned file: `web/e2e/playtest.js`.
- [ ] Variant research (`docs/variants-research.md`), then importers for the shortlist.
- [ ] Cross-check against godip / jDip; extend cross-checks to retreat and build phases.
- [ ] Try building the Dockerfile.

Unity/VRChat phase (unchanged; see docs/vr-world-design.md for the owner's design):

- [ ] Import the engine into Unity with the VRChat SDK; get it compiling under UdonSharp;
      confirm that hand-written SHA-256 runs (DECISIONS §18); measure speed on Quest.
- [ ] Board, order sheet with hand tracking, clock, announcer, network sync of seals, PlayerData saves.

## Notes for whoever continues this (human or another model)

- The engine must stay inside the subset listed in DECISIONS §3. `UdonSubsetTests` will
  tell you when you step outside. Put anything fancy in `tools/`.
- Engine classes are never created with `new` inside the engine: in Unity they are
  components wired in the scene. `tools/Parley.Tools/Game.cs` shows the wiring
  (GameFlow now also needs an `OrderSeal` and an `Announcement`).
- `tools/Parley.Tools/SealedDevice.cs` plays one well-behaved device in sealed-order tests.
- Game codes must stay canonical. Run `parley sim` on a few maps after touching the
  adjudicator or codec.
- If the code or archive format changes incompatibly, bump `P1` / `PA1`.
- The fetched maps and pictures in `third_party/` and `maps-local/` are other people's
  work; never commit them (git ignores both folders).
- Web server for testing: `PARLEY_DATA` (save folder), `PARLEY_URL`, `PARLEY_AWAY_SECONDS`
  (how fast a silent browser counts as away; set low in automated tests).
