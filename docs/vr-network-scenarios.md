# VR networking: the scenarios a simulation must survive

*Companion to `docs/vrchat-sync-design.md`. That document says how the world's
networking works; this one says how to prove it before anyone opens Unity. It is the
specification for a .NET simulation of VRChat ("the harness") and the list of failure
cases the world's networking code must pass in it. Scope: the classic board, standard
rules. Nothing here is built yet.*

## Why a simulation first

The rules engine is proven (DATC, two independent engines). The networking is not: it
exists only as a design. Its bugs are about timing and ordering (two headsets
disagreeing who is in charge, an opening that arrives after a handover, a save written
from a stale copy). Such bugs show up only with several real people in VR, the most
expensive place to find them. A simulation finds them on a laptop in seconds, hundreds
of times over, with every random choice replayable from a seed.

## The boundary: what is world code, what is VRChat

World networking code (to be written in the engine's restricted C#, checked by
`UdonSubsetTests`, later pasted into Unity unchanged) talks to VRChat only through a
thin set of calls. In the harness those calls go to a fake; in Unity, a small adapter
forwards them to the real VRChat API. Keep the adapter dumb: no decisions in it.

| World code needs | Real VRChat | Harness fake |
|---|---|---|
| Who am I, who is here, who is instance master | `Networking.LocalPlayer`, player list, `IsMaster` | Simulated player list; master = earliest joiner still present |
| Read a synced object's fields | Synced variables | Each device's *own last-received copy* (may be stale) |
| Write my object and send it | `RequestSerialization` (manual sync) | Queued; delivered to each other device after a random delay; never reordered per sender-object pair; latest value wins |
| Ownership of an object, change of owner | `SetOwner`, `OnOwnershipTransferred`, ownership request hooks | Transfer on request or when owner leaves (to master), with a delay window where the old owner's last send may still arrive |
| Someone joined / left | `OnPlayerJoined`, `OnPlayerLeft` | Events, delivered with delay, in order per device |
| Shared clock | `GetServerTimeInMilliseconds` | One global clock; each device reads it exactly |
| Per-player save | PlayerData get/set; "data ready" event | Per-player store that survives sessions; another player's data becomes readable after a random delay; writes can fail (size limit) |
| Late joiner catch-up | Current values of all synced objects delivered on join | Same: latest values of every object, then live traffic |

Rules the fake must enforce, because the design depends on them:

- Only the owner's writes go out. A non-owner's write is silently dropped (as in VRChat).
- An object's new owner starts from **its own last-received copy**, not from the old
  owner's memory.
- Size limits: refuse a send above the per-object limit; keep a per-device byte counter
  per second and report when the budget is exceeded (do not silently allow it).
- Nothing is private: anything written to a synced object or PlayerData is readable by
  every device (the harness exposes a "spy" that sees all traffic, used by secrecy checks).

## The devices

Each simulated headset runs the real world code with its own `GameFlow`, archive and
live state. A device can be: honest; slow (large delays); flaky (randomly disconnects
and reconnects); or one of the adversaries below. Everything random comes from one seed
per run; a failing run prints its seed and replays exactly.

## Checks after every simulated step

Invariants that must hold in every scenario, checked continuously:

1. **Agreement.** After a phase resolves, every connected honest device has the same
   archive and the same game code within N simulated seconds.
2. **Engine truth.** That game code equals what `GameFlow` alone gives for the same
   sealed sheets (run the engine directly as the reference).
3. **Secrecy.** No sheet or salt appears anywhere in traffic or PlayerData before the
   freeze of its phase (the spy checks every byte).
4. **Liveness.** With at least one honest device present and the clock not paused, the
   game always reaches the next phase within (clock + grace + reveal window + slack).
5. **No silent loss.** A seal an honest player published before the freeze is either
   used, or its units hold *and* the announcement says why. Never "used with a
   different sheet", never "vanished without a word".
6. **Saves.** After each resolved phase, every seated present player's PlayerData holds
   an archive that is a prefix-or-equal of the agreed archive, within the size limit.
7. **Alarms.** Honest devices raise an alarm in every adversary scenario that is meant
   to be detected, and never raise one in honest runs.

## Scenarios

Each is a test. "Expected" is what must happen; anything else is a bug in the world
code (or, if the design itself cannot meet it, a design change recorded in DECISIONS).

### A. Ordinary play

| # | Scenario | Expected |
|---|---|---|
| A1 | 7 honest players, a whole game to a solo, random sensible orders | Invariants hold every phase; final archive equals a pure-engine replay |
| A2 | Same, with message delays up to 3 s | Same; phases may close later but never wrongly |
| A3 | Everyone submits early | Phase ends early on all devices; no device shows it closing before its own submit |
| A4 | Clock runs out; some never pressed submit | Devices seal what is on their sheet within the grace; those orders are used |
| A5 | Submit on, off, edit, on again, close to the deadline | The last seal before the freeze is the one used; the device still holds the salts of its last few seals |
| A6 | Ready-up with one player slow to press | Clock starts only when all present are ready; host override works |
| A7 | Host pauses and resumes | No device's clock drifts by more than a second afterwards |

### B. People coming and going

| # | Scenario | Expected |
|---|---|---|
| B1 | A player joins mid-phase as a spectator | Receives archive pages and live state; shows the same board; sees no sheet |
| B2 | A seated player's headset restarts before the freeze | Rejoins, gets the seat back by name, writes the sheet again, play continues |
| B3 | Same, after their seal was frozen | Their units hold this turn; announced "sealed orders were never opened"; everyone else proceeds without waiting beyond the reveal window |
| B4 | A player leaves for good; someone else takes the seat | New player starts a fresh sheet; old seal discarded; nothing of the old player is opened |
| B5 | Three players drop and rejoin repeatedly through a whole game | Invariants hold; nobody is waited for while away |
| B6 | Everyone leaves mid-phase | Instance gone; next session resumes from saves (section D) |
| B7 | A player arrives while archive pages are still being sent to them and a phase resolves | They end with the new archive, not a mix of old and new pages (check the archive checksum) |

### C. The hub (host) leaving

| # | Scenario | Expected |
|---|---|---|
| C1 | Hub leaves while the clock runs | New master becomes hub within seconds; clock continues with the time left; no seal lost |
| C2 | Hub leaves during closing | New hub continues closing; late seals within the grace still count |
| C3 | Hub leaves during the reveal, some sheets opened | Opened sheets stay in the players' slots; new hub re-reads them; same result |
| C4 | Hub leaves right after resolving, before its archive page reached anyone | New hub resolves again from the slots; result identical to the lost one (dice included) |
| C5 | Hub leaves during the announcement | Reading is skipped; next stage starts normally |
| C6 | Two hubs in a row leave within 10 s | Still converges; no phase resolved twice into the archive |
| C7 | The new master is a slow device | Works, only slower; "host here" moves it to a better device without a hiccup |
| C8 | Old hub's last send arrives *after* the handover | Ignored by everyone (stale owner); no fork |

### D. Saves and resuming next week

| # | Scenario | Expected |
|---|---|---|
| D1 | Session ends after a phase; next session all return | Resume offered with the right phase and seats; same game code |
| D2 | Next session only 2 of 6 return, arriving one by one | Offer appears once their data has loaded; resumes from the newest save among those present |
| D3 | Players hold different saves (one missed the last phase) | The longer archive that extends the shorter wins; the stale one is not preferred |
| D4 | One player's save was edited (forged) | Shown as differing, not preferred when others agree; never silently used |
| D5 | Players from two different games are in the room | Both offered; choosing one does not touch the other's saves |
| D6 | A long game exceeds the save size | `TrimTo` keeps it under the limit; resume still works; current position never dropped |
| D7 | A save write fails (limit or error) | Device warns its player; the game continues; others' saves are unaffected |
| D8 | Roll-back, then the session ends | Next session resumes from the rolled-back line, not the discarded future |

### E. Adversaries (one bad device each)

| # | Scenario | Expected |
|---|---|---|
| E1 | Hub freezes a seal the player never made | That player's device refuses to open and alarms; everyone sees the alarm |
| E2 | Hub closes the phase early, treating a present player as absent | That player's device alarms ("closed while I was still writing") |
| E3 | Hub publishes a wrong result | Every honest device's own resolution differs; alarm naming the hub |
| E4 | Hub rewrites history (archive not old + one phase) | Alarm; honest devices keep their own archive |
| E5 | A player opens a different sheet than they sealed | Refused by every device; their units hold; announced |
| E6 | A player withholds their opening after seeing others' | Their units hold after the reveal window; counted and announced (known limit, see `docs/secret-orders-and-dice.md`) |
| E7 | A modified client writes into another player's slot | Writes are dropped (not owner); if the fake allows a forced ownership grab, the victim's device alarms |
| E8 | A spectator spams seat claims and ready toggles | Play unaffected; rate stays within budget |

### F. Limits

| # | Scenario | Expected |
|---|---|---|
| F1 | Bandwidth: 7 players, worst-case phase | Under the per-second budget on every device; report the peak |
| F2 | Largest single send | Under the per-object limit; report it |
| F3 | Late joiner download of a full-size archive | Completes; report how long at the budget |

## Done means

- Every scenario above passes for 200 seeds (A1 and B5 for 50 whole games).
- The harness reports peak bytes per second, largest send and save size, to compare
  with VRChat's real limits once they are confirmed.
- Any scenario the design cannot meet is recorded in DECISIONS.md with what the players
  will see instead.

## Suggested build order

1. The fake VRChat (players, objects, ownership, delays, PlayerData, spy, seed replay).
2. One honest device type running `GameFlow` as hub or checking copy; scenarios A1 to A4.
3. Hub handover (C), then joins and drops (B), then saves (D).
4. Adversaries (E) and limits (F).
5. Only then the Unity adapter and scene scripts.
