# Status

*Last updated at the end of the first overnight session.*

## Read this first

1. **Nothing is pushed anywhere.** No GitHub repository was connected, so the work is
   committed only on the temporary machine it was built on, and was sent to you in the
   chat as an archive file (`parley-*.zip` / `.bundle`). Save that file. Then create a
   repository and push (the `.bundle` file is a complete git history you can clone from).
2. **I did not write out the classic board or the 1900 variant.** The rules are fully
   implemented and verified; the game ships with original maps; and an importer converts
   community map files if you choose to use them. Reasons and details: DECISIONS.md §2.
3. **Nothing has been compiled inside Unity/VRChat yet.** The engine is written to
   UdonSharp's restrictions and a test enforces them, but I had no Unity here and could
   not open VRChat's documentation site. Expect small fixes at first import.

## Done and tested

| Piece | State | How it is tested |
|---|---|---|
| Map format, any map / any player count | Done | Loads 11 original maps and 14 imported community maps; 2 to 70 powers |
| Rules engine (moves, supports, convoys and paradoxes, retreats, builds, forced disbands, ownership, victory, draw) | Done | **DATC v3.0: all 164 cases pass** (165 checks) |
| Map importer (DPjudge `.map` format) | Done | All 14 reference maps convert, load and play |
| Game codes and full-history archive | Done | Round-trip, damage detection, wrong-map detection, size budget |
| Legal-option generation for the tap panel | Done | "Options offered = orders the referee accepts", checked on thousands of orders across 4 maps |
| Game flow (seats, hand-in, lock-in, timer, pause, drop/rejoin, draw, resume, roll-back) | Done | 9 scenario tests |
| Bot simulation with per-phase checks | Done | Thousands of games, 2 to 34 players, original and imported maps; no failures |
| UdonSharp-subset guard | Done | Test reads the engine source and rejects anything outside the subset |
| Web client (flat map, tap order entry, secret orders, history, roll-back, save/resume) | Working; not yet played by humans | Automated three-browser play-through; see `web/README.md` |

Test count: see the last line of `dotnet test`.

## Numbers worth knowing

- Resolving one phase: about 25 microseconds on a classic-size board, 100 microseconds
  with 17 powers (desktop .NET). Udon on Quest will be several hundred times slower:
  probably tens of milliseconds. Needs measuring in a real world.
- Game code length: about 90 characters (3 players), 145 (classic-size), 350 (17 players).
- Full game archive: 30 to 50 KB for a typical 7-player game; a 17-player 40-year game
  hit 102 KB, over VRChat's 100 KB per-player storage, so the flow has a size budget
  that forgets the oldest orders first (DECISIONS.md §8).
- Bot balance, Ring of 5, 300 games: each identical realm won 16% to 25% (that spread
  is noise). Classic board, 400 games: the simple bot wins 41% as France and 1% as
  England because it rarely convoys. Bot numbers compare seats; they do not predict
  human play (DECISIONS.md §13).

## Decisions waiting for you (none block work)

1. The real name (DECISIONS §1).
2. Whether to use the importer for the classic board / community variants, and which
   variants have licences you are comfortable with (§2, §5).
3. Who may trigger resume and roll-back: host only, or a vote (§8).
4. Secret-order networking: commit-and-reveal (nobody can cheat) or trust the instance
   owner (simpler) (§10).
5. Contributor terms so other people's contributions can be uploaded to VRChat (§14).
6. Map design: keep the generated Ring maps as the launch maps, or draw an original
   map by hand (§12).

## Next steps

Still in the "no Unity needed" phase:

- [ ] Second importer: jDip XML (many classic-era variants). Then webDiplomacy/vDiplomacy.
- [ ] Cross-check the adjudicator against a second, independent engine on random
      positions (the Python `diplomacy` package can be scripted for this; keep it a
      separate process, it is AGPL).
- [ ] Human playtest of the web client; fix what friends trip over.
- [ ] A stronger bot (convoys, defence) so balance numbers mean more.

Unity/VRChat phase:

- [ ] Import the engine into a Unity project with the VRChat SDK and get it compiling
      under UdonSharp. Measure map loading and phase resolution on a Quest 3S.
- [ ] Board: build province meshes from the `shape` data; wall or tilted table at eye level.
- [ ] Order panel: unit, order type, target; large tap targets for hand tracking.
- [ ] Network sync of the archive; PlayerData persistence and "resume" flow (§8).
- [ ] Secret-order exchange (§10).
- [ ] Private audio rooms (check UdonVoiceUtils on Quest, and its licence).
- [ ] Intro sequence, music, announcer (original or CC-licensed; credits board).

## Notes for whoever continues this (human or another model)

- The engine must stay inside the subset listed in DECISIONS §3. `UdonSubsetTests` will
  tell you when you step outside. Put anything fancy in `tools/`.
- Engine classes are never created with `new` inside the engine: in Unity they are
  components wired in the scene. `tools/Parley.Tools/Game.cs` shows the wiring.
- Game codes must stay canonical (same position, same text). `Simulator.Verify` checks
  this plus save/load and replay after every simulated phase; run `parley sim` on a few
  maps after touching the adjudicator or codec.
- If the code or archive format changes, bump `P1` / `PA1` so old saves are refused
  with a clear message rather than misread.
- DATC expectations are generated into `tests/datc/datc_v3.0.txt`; the file is the
  source of truth now. Each `EXPECT` uses exact sets, so a rule change shows up at once.
- The reference maps in `third_party/` are other people's work; never commit them.
