# Status

*Last updated at the end of the third session (the "hard design" session). All five
tasks you set were worked through in order; what is finished, what is partly finished
and what is left is listed below. Everything is committed and on `main`.*

## Read this first

1. **The engine now has pluggable rulesets.** The standard game is one module; nothing
   about standard play changed (old and new engine give byte-for-byte identical
   answers on 20,000 situations and 400 whole games; DATC 165/165; old saves load).
2. **The tamper-proof orders were attacked on paper and in tests, and hardened.** Seven
   real weaknesses were found and fixed; one cannot be fixed without a referee (a
   player can refuse to open their sheet after seeing the others': their units hold,
   it is announced and counted). Plain-language write-up:
   `docs/secret-orders-and-dice.md`.
3. **There are fair dice.** Built from every player's sealed secret; the host cannot
   steer or predict them; anyone can re-check every roll from the saved game.
4. **The Renaissance game is playable**, under the name **"Purse and Dagger"**
   (the original's name is a trademark; tell me if you want another). Money, loans,
   bribes, garrisons, sieges, rebellions, assassins, famine, plague. Run
   `./scripts/fetch-reference-maps.sh` and pick the map "Italy 1454" in the web
   client. It is drawn as dots and lines: there is no board picture.
5. **Please read `docs/machiavelli-rulings.md` if you know the game.** There is no
   second implementation to check against, so the rulings are my reading of the
   e-mail judges' rules. Five of them differ on purpose (dice timing, automatic
   upkeep, public expenses and treasuries, rebellions take a full campaign). They are
   marked **(differs)**.
6. **Fog of War:** real hidden information is not possible inside VRChat without a
   referee machine. Recommendation and options in `docs/fog-of-war.md`. The engine
   rule for "who sees what" is built; nothing else is.
7. **VRChat sync design** is written (`docs/vrchat-sync-design.md`) but rests on my
   memory of VRChat's documentation, which was unreachable. Its last section lists
   what to verify first in Unity.
8. Still open from before: delete the old `initial-engine` branch on GitHub by hand;
   how pausing should work (DECISIONS §21).

## The five tasks

| Task | State | Where |
|---|---|---|
| 1. Pluggable rules | **Done, tested** | `Ruleset.cs`, `StandardRules.cs`, shared `Adjudicator.cs`; calendar and cities come from the map file; DECISIONS §23; `RulesetTests` |
| 2. Harden secret orders; fair dice | **Done, tested** | `OrderSeal.cs`, `Dice.cs`, `GameFlow` (run names, reveal floor, `LiveState`, `VerifyLast`); `docs/secret-orders-and-dice.md`; DECISIONS §24, §25; `AdversaryTests` |
| 3. Renaissance ruleset | **Done, tested**, with listed simplifications | `PurseRules.cs`; importer `parley import-njudge-mach`; `docs/machiavelli-rulings.md`; DECISIONS §26; `PurseTests` (38 cases), `parley purse-sim`; web client and browser playtest |
| 4. Fog of War | **Design done; engine rule built; no fog game mode yet** | `docs/fog-of-war.md`; `Visibility.cs`; DECISIONS §27 |
| 5. VRChat sync and recovery | **Design done** (unverified against VRChat's docs) | `docs/vrchat-sync-design.md`; DECISIONS §28 |

## How it was tested

| What | Result |
|---|---|
| `dotnet test` | 354 tests pass |
| DATC v3.0 | 165 of 165 |
| Old engine against new, same inputs | Identical output on 20,000 situations and 400 whole games |
| Python `diplomacy` engine | 19,986 of 20,000 situations identical; the 14 others are rule-edition differences (DECISIONS §7); 400 games, no unexplained difference |
| godip | 400 games, no unexplained difference |
| Sealed orders under attack | `AdversaryTests`: one test per attack (replay, host cutting the reveal short, seat hand-over, weak salts, forged hand-over, lying host, withholding) |
| SHA-256 | NIST vectors including the million-letter message; every block-boundary length against the standard library |
| Renaissance ruleset | 38 hand-written cases; random play: tens of thousands of phases on two boards with no impossible position, every position surviving its game code, and exact replay from the archive including dice |
| Browser playtest (`node web/e2e/playtest.js standard ring5 valleys`) | 123 checks, none failed: the earlier whole-game tests, plus a year of the Renaissance ruleset through the page (loan, bribe, assassination attempt on shared dice, conversions by tapping, a server restart in mid-campaign) |

Not re-run this session: the browser playtest on Modern and Ancient Mediterranean
(they use the same code paths as the classic board).

## Left to do (for any model or person)

Renaissance ruleset:

- [ ] A human who knows the game reads `docs/machiavelli-rulings.md`.
- [ ] Simplified on purpose, listed there: straits (no permission order, convoys not
      blocked), no fortresses, no per-power limits on special units, one unclear
      strength-2 siege rule left out.
- [ ] Web client: no way yet to choose the options (famine, plague, loans, assassins,
      special units, adjacency) when creating a game; the board's defaults are used.
      `PurseRules.startOptions` is the switch. Special units cannot be built from the page.
- [ ] Web client: the Italy board has no picture; a hand-made layout (`xy` lines) would
      make it far more readable than the automatic one.
- [ ] Bots for it are random only (test equipment).

Fog of War:

- [ ] Web judge fog mode: filter `Views.State` and `Views.HistoryEntry` through
      `Visibility`, add a game setting, withhold code and archive from players, publish
      everything at the end for the audit. About a day.

Sealed orders:

- [ ] "Escrow shares" (so that the other players can open the sheet of someone who
      dropped) if VRChat's owner-targeted events prove private. See the write-up.

Carried over from before: 1900 variant; godip variants; variants that start with a
build phase; a human playtest of the web client; the Unity phase itself.

## Notes for whoever continues

- **Adding a ruleset:** write a module shaped like `StandardRules.cs` or `PurseRules.cs`,
  add a `KIND_` and one line per method in `Ruleset.cs`, wire it in
  `tools/Parley.Tools/Game.cs`. Extra state goes into `BoardState.ext`, `unitFlag`,
  `provFlag` and is saved in game codes automatically when the map has a `rules` line.
- **Dice:** ask `Dice.Roll(sides, label)` with a label unique within the phase. Only use
  dice in a phase where `UsesDice` is true, and only in phases where every player has
  something to lose (see DECISIONS §25).
- **A walled city is an extra province entry** (type `CITY`, index at or above
  `map.landCount`). Loops meant for real provinces should stop at `landCount`.
- `GameFlow.Init` now takes the ruleset and the dice; `tools/Parley.Tools/Game.cs` shows
  the wiring. The archive keeps each dice phase's seed after the sheets
  (`GameHistory.OrdersAt`, `SeedAt`).
- Reference material for the Renaissance rules is fetched into `third_party/njudge/`
  (rules text included, for reading; not committed).
- Web server: `PARLEY_MAPS` adds map folders (the playtest uses it for `tests/purse`);
  `PLAYTEST_SERVER_LOG=<file>` keeps the server's output during a playtest.
- This session ran on a branch (`claude/youthful-pasteur-9satoz`) that was
  fast-forwarded to `main` after each verified step; no other session was active.

---

## Status at the end of the second session (kept for reference)

### Read this first (second session)

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

### Done and tested (second session)

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

Test count then: 280.

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

### Decisions you made in the second session (recorded in DECISIONS.md)

Classic map default, fetched at setup (§2); resume and roll-back host only, every client
keeps the save (§8); tamper-proof orders (§10, §18); newest DATC convoy ruling (§7);
name stays "Parley" (§1); contributor terms not needed yet (§14); Ring maps are test
maps only (§12).

### Next steps as they stood then

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

### Notes for whoever continues this (human or another model)

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
