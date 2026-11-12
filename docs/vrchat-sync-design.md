# VRChat sync and recovery: design

*For the Unity phase. Which headset is in charge, what is sent to whom, how someone
who arrives late catches up, what happens when the host drops, how a campaign that
runs for weeks is saved between sessions, and how all of it carries the tamper-proof
orders and dice of `docs/secret-orders-and-dice.md`.*

**Read this first.** VRChat's documentation could not be reached from the machine this
was written on, so every statement about what VRChat does comes from memory of its
documentation, not from reading it now, and nothing here has run in Unity. The last
section lists what must be measured before building on it. The engine side (everything
called `GameFlow.…` below) exists and is tested.

## The idea in five lines

1. One device is the **hub**. It keeps the clock and moves the game from stage to stage.
2. **Every** device runs the whole engine and checks everything the hub announces.
3. Each player's secrets travel through an object **only that player can write**, so
   not even the hub can forge a seal or an opened sheet.
4. Everything needed to continue is in two texts that every device holds: the
   **archive** (the game so far) and the **live state** (the phase in progress).
5. After every phase each player's device stores the archive in that player's own
   VRChat save data. Next week, whoever turns up brings the game with them.

## What VRChat gives us (to be confirmed)

| Mechanism | What it does | Limits to respect |
|---|---|---|
| Synced variables, manual mode | The owner of an object writes; everyone receives; late joiners get the latest values automatically. Reliable. | A size cap per object per send (remembered as roughly 280 KB) and an overall budget of roughly 11 KB a second per client. |
| Ownership | Every synced object has one owner; only the owner writes. When an owner leaves, the object passes to the instance master. Ownership requests can be refused in code. | A moment of handover when a player leaves. |
| Player objects | One copy of a template object per player, owned by that player. | Same as other synced objects. |
| Network events | A message to everyone, or to an object's owner; newer SDKs allow parameters. | Rate limited. Whether owner-only events are private is untested. |
| Persistence (PlayerData) | Key/value storage per player per world on VRChat's servers; readable by everyone in the instance once that player's data has loaded. | About 100 KB per player. Written only by that player's own client. |
| Shared clock | `Networking.GetServerTimeInMilliseconds()` is the same on all devices. | An integer that wraps; use differences. |
| Nothing | There is no private local storage, no world-wide storage, and no way to send data out of the world. | |

## Who is in charge

**The hub is the owner of one object, `GameHub`.** At first that is the instance master.
The hub runs `GameFlow` for real: it decides when the clock has run out, when everyone
has submitted, when sheets have been opened, and it resolves the phase.

The hub is *in charge of time, not of truth*. Three things keep it honest:

- It cannot write players' seals or sheets (next section).
- Resolution is a pure calculation. Every device does it too and compares.
- Dice come from the players' secrets, not from the hub.

**Every other device is a checking copy.** It loads the same archive and live state into
its own `GameFlow` and, after each phase, checks (see "What every device checks").

*Why not "nobody in charge"?* Somebody has to say "time is up" and "the phase is
closed", or two devices will disagree by a fraction of a second about whether a late
seal counts. One clock-keeper whose every decision can be checked is simpler and safer
than a vote on every transition.

*Which device should be the hub?* Prefer a PC player with a wired connection. The
world offers a "host here" button; `GameHub` accepts an ownership request only from the
player the current hub has named, or from anyone when the hub has left. If nobody
chooses, VRChat's own rule (the instance master) applies.

## What is synced, and by whom

| Object | Owner | Contents | When it changes |
|---|---|---|---|
| `GameHub` | the hub | The live state (`GameFlow.LiveState`), the list of seals as frozen, a session start time, the hub's name | A few times per phase: stage changes, a seal arrives, a sheet is opened |
| `ArchivePage` × about 12 | the hub | The archive, cut into pages of about 8,000 characters | Normally only the last page, once per phase |
| `PlayerSlot` (one per player) | **that player** | Seat request; ready; draw vote; the current **seal** and the phase key it belongs to; after the freeze, the **opening** (salt and sheet) | When the player does something |

Sizes, to see that this fits comfortably:

- A seal is 64 characters; an opened sheet is about 100 to 250; the live state for
  seven players is 1 to 2 KB.
- A whole phase costs well under 20 KB of traffic in total, against a budget of about
  11 KB **per second**.
- The archive is the only large thing: up to the budget in `GameFlow.maxArchiveChars`
  (suggested 40,000 characters in VR, see "Saves"). Cutting it into pages means a new
  phase re-sends one page, not the whole game, and a late joiner's download of all
  pages takes a few seconds, once.

The clock is never synced tick by tick. The live state says how many seconds were left
at the moment it was written; with the shared server time every device counts down by
itself. `GameFlow` takes "now" as seconds; the world passes
(server time − session start) / 1000.

The announcement (the reading-out of orders) is not synced either. Every device
replays the last phase from the archive and computes the same reading position from
the same clock.

## Secret orders and dice over this plumbing

| Step | Who does what |
|---|---|
| Writing | The sheet exists only in local variables on the player's device. Its entropy pool (`OrderSeal.Stir`) is fed every few frames with head and hand positions and tap timings. |
| Submit on | The device computes the seal and writes it, with the phase key, into its own `PlayerSlot`. The hub sees it and calls `SetSeal`. Submit off clears it. |
| Freeze | The hub's `GameFlow` reaches REVEAL. The hub publishes the list of frozen seals in `GameHub`. |
| Open | Each device finds its own seal in the frozen list. If it is the seal it published, it writes salt and sheet into its `PlayerSlot`. (A device keeps the salt and sheet of its last few seals until the phase is over, in case the hub froze the one before a last-second change.) If the frozen list shows a seal the device never made, it refuses to open and raises an alarm. |
| Resolve | The hub feeds the openings to `Open`, resolves, appends to the archive, publishes. Every other device does the same calculation from the same slots and compares. |
| Dice | The seed is computed by every device from the opened salts. Nothing about dice is sent. |

Never put a sheet or a salt into any synced variable, PlayerData or event before the
freeze. After the freeze they are public by design.

## What every device checks

After each phase, on every device, automatically:

1. **My seal was frozen as I published it**, or not at all.
2. **The phase closed fairly:** the clock had run out by the shared time, or every
   player present had submitted. A device that was present, still writing, and sees
   the phase close early says so loudly: that is a host treating a player as absent.
3. **Every sheet that was used matches its frozen seal, and the dice seed follows from
   the opened salts** (`GameFlow.VerifyLast`).
4. **The result is right:** the device's own resolution gives the same game code.
5. **History was not rewritten:** the new archive is the old one plus one phase,
   unless a roll-back was announced.

If a check fails the device shows a warning that names the hub and what failed. The
players can then move hosting to someone else: the new hub loads the archive that
most devices agree on. Cheating by the hub is therefore not prevented by force, but it
is detected by everyone at once and undone in a minute.

## Late joiners

- VRChat delivers the current values of `GameHub`, all `ArchivePage`s and all
  `PlayerSlot`s to a newcomer. Their device rebuilds the archive, checks its checksum,
  loads it, loads the live state, and shows the board. Nothing in any of it is secret.
- A newcomer whose display name matches a seat gets the seat back (this is also how a
  crashed player returns). Anyone else is a spectator and may take an empty or
  abandoned seat (`ClaimSeat`).
- Someone who takes a seat in the middle of a phase starts a fresh sheet; after the
  freeze they wait for the next phase.
- **A player whose headset restarted has lost the sheet and salt** of the phase in
  progress, because there is nowhere private to keep them. Before the freeze they
  simply write the sheet again. If they had sealed and the freeze has already
  happened, that seal can never be opened: their units hold this turn, and it is
  counted. This is the price of having no private storage; it is small and visible.

## When the hub drops

VRChat hands `GameHub` and the archive pages to the new instance master. That device
was a checking copy a moment ago, so it already holds everything:

1. It notices it now owns `GameHub`.
2. It loads the archive it has, then the live state it last received
   (`LoadArchive`, `LoadLiveState`). The same phase continues: same run of the game, so
   seals already published still fit; the clock shows the time that was left.
3. It re-reads every `PlayerSlot`. Seals and openings live there, not in the hub, so
   nothing a player published is lost with the old hub.
4. It carries on. Players see a short notice: "X is now hosting."

| The hub died ... | What the new hub does |
|---|---|
| while the clock was running | Continues the clock. |
| during closing or opening | Continues that stage; sheets already opened are in the slots and are re-checked. |
| after resolving but before everyone received the result | It may hold the old state. It resolves the phase again from the slots; the result is identical, because resolution and dice depend only on what is in the slots. Devices that did receive the first result see the same archive arrive again. |
| during the announcement | Skips the rest of the reading. |

A device clears its own opening from its slot only when it sees the **next** phase's
key, so the slots always hold what is needed to redo the last step.

If the new master is a poor choice (a Quest on weak Wi-Fi), anyone can press "host
here" afterwards.

## Saves between weekly sessions

The instance does not survive the week: when the last person leaves, everything synced
is gone. The save is PlayerData.

- **After every resolved phase, every seated player's device writes the archive into
  that player's own PlayerData.** So each participant carries the latest save on
  VRChat's servers, on Quest or PC. No single person has to be there next week.
- **Next session,** when players' data has loaded, the world looks at the saves of
  everyone present and groups them: two saves are the same game if one archive is the
  beginning of the other; the longer is newer. The host is offered, for example,
  "Resume: Fall 1905, classic board, seats Ada, Bo, Cy ... (5 of 6 present hold this
  save)". If people from two different campaigns are in the room, both are listed.
- **The save that most present players hold wins.** A single differing copy (an edited
  one, or one from before a roll-back) is shown but not preferred.
- The game returns paused at the start of the phase that was in progress, as a new
  run. Anything sealed last week is void; sheets are written again. Seats go back to
  the same display names; an absent player's power holds until someone takes it over.
- **Size.** PlayerData allows about 100 KB per player, and text may count two bytes a
  character. So the archive budget in VR should be about 40,000 characters, enough
  for a long seven-player standard game; when it is exceeded the engine drops the
  oldest phases' orders first, then the oldest positions, never the current one
  (`GameHistory.TrimTo`). The Renaissance ruleset's positions are larger (treasuries,
  cities); expect roughly a third fewer phases of history in the same space. Measure
  the real accounting in Unity before fixing the number.
- **By hand, as a second line of defence:** a panel shows the game code and archive
  for copying out on PC, and accepts a pasted one. The web server can load the same
  text. A weekly group might paste the code into its Discord channel after each
  session; it is one line.
- **Roll-back** is the host choosing an earlier phase from the archive. Every device
  announces it; the old future is discarded on all devices and, at the next write,
  in all saves.

## Rhythm of a two-hour session (what the design is for)

1. People arrive over ten minutes. The first to arrive is hub; the save is offered as
   soon as two or three players' data has loaded.
2. Host presses Resume. Everyone finds their seat. Ready-up starts the clock.
3. Phases run. People drop and return; a seat whose player is away holds. If the host
   drops, the game continues under the next device within a few seconds.
4. At the end the host pauses. Nothing else is needed: the save is already in
   everybody's PlayerData since the last resolved phase.

## To verify in the Unity phase, in this order

| Question | Why it matters | If the answer is bad |
|---|---|---|
| Does the hand-written SHA-256 run correctly in Udon, and how long does one hash take on a Quest? | Seals, dice, checks. A Renaissance campaign needs perhaps fifty hashes at resolution. | Spread the work over frames; it is already loop-based. |
| How long does resolving a phase take on a Quest (standard and Renaissance)? | Every device resolves every phase. | Checking copies can resolve a second later than the hub, spread over frames. |
| Real limits of manual sync (bytes per object, bytes per second, how strings are counted). | Page size, archive budget. | Smaller pages, more of them. |
| What exactly happens to objects and their values when their owner leaves, and how fast? | Hub handover. | The design only needs "eventually the master owns it and has some recent copy". |
| PlayerData: size accounting, when another player's data becomes readable, write frequency limits. | Saves. | Save every phase but write less history; or save only at pause and at session end. |
| Are owner-targeted network events delivered only to the owner? | Would allow escrow of sheets (no more "never opened") and a referee client for real fog of war. | Nothing in this design depends on it. |
| Can a modified client take ownership of someone's `PlayerSlot`? | The design assumes only the player writes their slot. | Devices already alarm on "a seal I never made"; add the same alarm for openings. |

## Where the pieces already are

| Need | Engine call |
|---|---|
| Run the game on the hub | `GameFlow` (`Tick`, `SetSeal`, `Open`, `ClaimSeat`, `PlayerJoined`, `PlayerLeft`, ...) |
| The two texts every device holds | `GameFlow.Archive()`, `GameFlow.LiveState(now)` |
| Become hub mid-phase | `LoadArchive`, then `LoadLiveState` |
| Check the hub | `VerifyLast`, and comparing `CurrentCode()` after resolving locally |
| Seals and salts on a device | `OrderSeal.Stir`, `NextSalt`, `Make`; key from `GameFlow.SealKey()` |
| Keep the save within the limit | `GameFlow.maxArchiveChars` |
| Which seat needs a seal this phase | `GameFlow.NeedsSeal` (in dice phases: everyone present) |
