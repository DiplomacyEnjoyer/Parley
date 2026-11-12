# Status

*Third session ("hard design" session) in progress. This block is kept current after
every step so that anyone, human or model, can pick up exactly here.*

## Third session: where things stand right now

| Step | State |
|---|---|
| 1. Rulesets as modules | **Done, tested, on main.** `Ruleset.cs` (the door), `StandardRules.cs` (the standard game), `Adjudicator.cs` (shared referee). DECISIONS §23. DATC 165/165; old and new engine give byte-identical answers on 20,000 situations and 400 games; cross-checks and browser playtest pass. |
| 2. Tamper-proof orders reviewed; fair dice | **Done, tested, on main.** `docs/secret-orders-and-dice.md`, DECISIONS §24, §25, `AdversaryTests`. |
| 3. Renaissance ruleset ("Purse and Dagger") | **In progress.** Done: the module `PurseRules.cs`, importer for the nJudge board (`parley import-njudge-mach`), random-play harness (`parley purse-sim`, thousands of phases, no problems), a small original test board (`tests/purse/valleys.pmap`), test helper `tests/Parley.Tests/PurseBoard.cs`. **Left:** hand-written rule cases (`PurseTests.cs`), `docs/machiavelli-rulings.md`, DECISIONS §26, fetch script and THIRD_PARTY entry for the nJudge files, web client (treasury, expenses, garrison orders) and its browser playtest. |
| 4. Fog of war | Not started (`docs/fog-of-war.md`, engine-side visibility filter if time). |
| 5. VRChat sync design | Not started (`docs/vrchat-sync-design.md`). |

Branch note: work is committed on `claude/youthful-pasteur-9satoz` and fast-forwarded to
`main` after each verified step (no other session was active on main).

Reference material used for step 3 (not committed): the nJudge 0.8.10 source archive
from SourceForge (`data/rules.machiavelli`, `data/map.machiavelli`, `data/seed.9`).
diplom.org and VRChat's documentation sites are blocked from this machine. The
Condottieri project is AGPL-3.0: ideas only, no code.

---

*Below: status as of the end of the second session.*

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
6. **Variants:** research and shortlist in `docs/variants-research.md`. Ten community
   variants with pictures now install from jDip. 1900 could not be fetched from here.

## Done and tested

| Piece | State | How it is tested |
|---|---|---|
| Map format, any map / any player count | Done | 11 generated maps and 14 imported community maps; 2 to 70 powers |
| Rules engine | Done | **DATC v3.0: all 164 cases pass.** Cross-checked against the Python engine on 20,000 random movement situations (DECISIONS §7) |
| Map importer (DPjudge `.map`) | Done | All 14 reference maps convert, load and play |
| Map importer (jDip variant packs, with pictures) | Done | jDip's Standard converts to exactly the DPjudge board; 10 variants installed and played by bots (`JdipTests`) |
| Cross-checks against other engines, all phase types | Done | Whole games replayed in the Python engine and in godip: 400 games, ~15,000 phases each; only documented rule-edition differences (DECISIONS §7) |
| Dockerfile | Built and run | Serves 23 maps with the classic board as default |
| Classic board picture in the web client | Done | `parley import-svg` matches the picture to the map; classic, Modern, Ancient Mediterranean all match 100%; test checks coverage and that no unsafe markup survives |
| Game codes and full-history archive | Done | Round-trip, damage detection; archive now stores the timer schedule |
| Legal-option generation | Done | "Options offered = orders the referee accepts", thousands of orders, 4 maps |
| Turn flow (schedule, submit toggle, early finish, auto hand-in, pause, announcement, ready-up) | Done | `TurnFlowTests`: 21 scenarios including every sealed-order edge case |
| Sealed orders (`OrderSeal`, SHA-256 in the UdonSharp subset) | Done | Test vectors; matches .NET SHA-256 on 200+ inputs; cheating attempts refused |
| Announcement data (orders by country, intended moves, results, battles) | Done | Movement, retreat and build phases |
| Web client on the new flow | Working | `web/e2e/playtest.js`: whole multi-player games through the page (details below) |
| UdonSharp-subset guard | Done | Covers the new engine files too |

Test count: 280 (`dotnet test`, after running the fetch script).

### Automated playtest (`web/e2e/playtest.js`)

Plays through the web page with one browser per player plus one API-only "cheating"
client, per map: ready-up; timer running out with partial sheets; early finish; submit
toggled on/off; host pause and resume; a player dropping (units hold) and rejoining by
personal link; a client trying to open different orders than it sealed (refused);
secrecy of everything the server sends and stores; the host dropping (host role moves
on); roll-back; resume from a game code; the server losing the game and a browser
resuming it from its own copy; random play through retreats and builds.

Results: **classic board, Modern, Ancient Mediterranean and Ring of 5 pass every check**
(152 checks in the last full run, about 20 minutes). An earlier intermittent timeout on
the 10-power maps was a race in the test itself (it sometimes started waiting after the
phase had already ended), now fixed. Timeouts print the page's state if one ever recurs.

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
- [x] Automated playtesting through the web client (`web/e2e/playtest.js`).
- [x] Variant research (`docs/variants-research.md`); jDip importer (10 variants with pictures).
- [x] Cross-checks against the Python engine and godip, all phase types.
- [x] Dockerfile built and run.

Left for next time:

- [ ] 1900: fetch DPjudge's `1900.map` at setup (GitHub was unreachable here) and check
      what its special rule needs.
- [ ] godip variants (Cold War, Western World 901, Youngstown Redux, ...): a Go exporter
      at setup plus a picture matcher for godip's SVG layout (docs/variants-research.md).
- [ ] Variants that start with a build phase (Chaos).
- [ ] Human playtest of the web client.

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
