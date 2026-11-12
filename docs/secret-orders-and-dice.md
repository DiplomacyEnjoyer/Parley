# Secret orders and fair dice

*How Parley keeps orders secret and dice honest when there is no neutral server, what
an adversarial review of the first design found, and what remains possible for a
determined cheat. Written for the project owner; the last section is for whoever
builds the VRChat side.*

## The situation

In VRChat there is no referee machine. One player's headset or PC acts as the hub (the
"host"), and anything that is synced can in principle be read by every device in the
room, including a modified one. So the design assumes:

- any player may be running a modified client that reads everything it receives;
- the host may be one of the cheats;
- players may leave, crash or pretend to crash at any moment.

## Secret orders, in plain words

1. While the clock runs, your order sheet exists **only on your own device**.
2. When you flip *submit* on, your device publishes a **seal**: a fingerprint (SHA-256)
   of your sheet mixed with a long random secret, the **salt**. The seal says nothing
   about the sheet. You can flip submit off and on again; each time is a new seal.
3. When everyone present has submitted, or the clock hits zero, all seals are **frozen**.
4. Only then does every device **open**: it publishes its sheet and its salt. Every
   device checks every opened sheet against the frozen seal. A sheet that does not
   match is refused.
5. Every device resolves the turn itself and gets the same result.

Because every seal was frozen before any sheet was visible, nobody can choose their
orders after seeing someone else's, and nobody can swap their sheet for another.

A seal is tied to: the power it belongs to, the exact position on the board, the
number of the phase, and the **run** of the game (see "Replays" below).

## What the review found, and what was done

Each row is something a cheat could try. "Test" names the automated test that plays
the attack (`tests/Parley.Tests/AdversaryTests.cs` unless another file is named).

| # | Attack | Was it possible before? | Now |
|---|---|---|---|
| 1 | **Host stops the opening early.** The host watches sheets come in and presses "stop waiting" at the moment a rival's sheet has not arrived yet; that rival's units hold. | Yes. | The host cannot end the opening before every device has had a fair chance (10 seconds by default; honest devices open within one or two). Test: `TheHostCannotCutTheOpeningShort...` |
| 2 | **Take orders back by handing over the seat.** After seeing everyone's sheets, a player leaves and a friend takes the seat; the takeover wiped the seat's orders. | Yes. | Once seals are frozen a seat change no longer clears anything; the newcomer starts next phase. Test: `HandingASeatToSomeoneElseAfterTheFreeze...` |
| 3 | **Replay an old seal and sheet.** After a roll-back, or in a new game on the same map, the same position comes round again; a seal and opened sheet from the earlier time fitted it. Anybody who could write in the victim's name could play the victim's old orders. | Yes (the seal named the position but not the game). | Every start, load, resume and roll-back begins a new *run* with a fresh random name that is part of what a seal is tied to. Old seals fit nothing. Test: `ASealFromAnotherGameOrFromBeforeARollBack...` |
| 4 | **Guess the salt.** If a device makes its salt from something guessable (the time, a weak random number), an eavesdropper can try every likely sheet with every likely salt and read the orders before the reveal. | The engine accepted 64-bit salts and offered no help in making good ones. | Salts must be 128 bits. The engine now has a private **entropy pool** per device (`OrderSeal.Stir`, `NextSalt`) that the world feeds with head and hand positions and tap timings; a published salt reveals nothing about earlier or later ones. Test: `AGuessableSaltLetsOthersReadASeal...` (which also performs the attack on a weak salt) |
| 5 | **Text that hashes differently on different devices.** Odd characters are turned into bytes differently by different hash implementations; a sheet could match its seal on one device and not on another. | Possible with a modified client. | Sealed text must be plain ASCII; anything else is refused at the opening. Test: `SealedTextMustBePlain...` |
| 6 | **A stand-in host edits the hand-over.** When the host leaves mid-phase, the next host receives the phase's state and could alter an opened sheet or mark an unopened seal as opened. | No hand-over existed (a host leaving mid-phase lost the phase). | A phase can now be handed over (`GameFlow.LiveState`). Every opened sheet in a hand-over is re-checked against its seal; a forged or stale one is refused whole. Tests: `ANewHostCarriesOnMidPhase...`, `AForgedOrStaleLiveStateIsRefused` |
| 7 | **Host announces a false result.** | Devices could re-check sheets against seals by hand. | One call (`GameFlow.VerifyLast`) checks every used sheet against its seal and the dice seed against the opened secrets. The VR world runs it on every device after every phase. Test: `EveryDeviceCanCheckTheResolvedPhase...` |
| 8 | **Open different orders than were sealed**, open someone else's seat, seal after the freeze, withdraw after the freeze. | No (already refused). | Unchanged; tests in `TurnFlowTests`. |
| 9 | **Copy another player's seal** as your own. | No: a seal names its power, so the copy cannot be opened. | Unchanged. |
| 10 | **Flaws in the hand-written SHA-256.** | None found. | Checked against the official NIST examples, including the one-million-letter message, and against the standard library on every length around every block boundary and on non-English text (`SealTests`, `AdversaryTests`). The code keeps 32-bit words as two 16-bit halves so it never relies on overflow. |

### What cannot be fully prevented

**Withholding.** The last device to open has seen everyone else's sheets. It cannot
change its orders, but it can choose **not to open at all**. Its units then all hold,
and its real orders stay secret (useful for hiding a stab that would have failed).

There is no way around this without a neutral party or much heavier cryptography (see
"Stronger options"). What Parley does:

- An unopened seal costs the **whole turn**: every unit holds, any spending is void.
- It is **announced** ("Sealed orders were never opened") and **counted** per player
  for the session (`GameFlow.unopened`). An honest device opens automatically within a
  second or two of the freeze, so apart from a crash in that short window, or having
  left earlier, it does not happen by accident. A player who "crashes" at convenient
  moments will be noticed.

**The host decides who is present.** The clock closes early when everyone *present*
has submitted, and the host's machine is what notices arrivals and departures. A
dishonest host could treat a player as absent. The player sees it at once (their
device is told the phase closed while they were still writing), and the VR design has
every device watch for exactly this (`docs/vrchat-sync-design.md`).

**The web client's server sends the page.** The server never sees orders early, but it
does serve the JavaScript that keeps them. Whoever runs the server could serve a page
that leaks. Play on a server run by someone you trust, or by a non-player.

## Dice

Some rulesets need dice (famine, plague, income, assassination). The rule is the same
as for orders: **nobody may know or steer the result, including the host.**

### How a roll is made

1. Every seal already contains a secret: its salt. In a phase with dice, **every
   player present seals**, even one with nothing to order (an empty sheet).
2. When the sheets are opened, the salts become public. The **seed** of the phase is a
   fingerprint of all of them together, plus the phase's identity.
3. Every roll is a fixed calculation from the seed and the roll's name ("famine row",
   "assassination of X by Y"). Every device computes the same rolls. Anyone can
   re-check them later from the published seed.

Why this is fair:

- Each salt was fixed by its seal before any other salt was visible, so nobody can
  choose theirs to steer the seed.
- The seed cannot be known before the reveal, because it needs every salt. **One
  honest player with a good salt is enough** to make it unpredictable, whatever the
  others do, even if all the others and the host work together.
- The host adds nothing to the seed and does not compute it for anyone. A host that
  announces different dice is caught by `VerifyLast` on every device.
- Rolls use rejection sampling, so a d6 is exactly even (not "nearly").

### The one weakness: trading a turn for a re-roll

The last device to open can compute the dice *with* its salt, and by withholding it
changes the seed. So withholding is, in effect, **one re-roll, paid for with your whole
turn, in public**. (For a 1-in-6 event that raises the chance to about 31%.)

What limits it:

- **You cannot re-roll your own gamble.** An assassination attempt is part of the
  sheet; not opening the sheet cancels the attempt. Only an accomplice can re-roll for
  you, by throwing away *their* turn.
- **Dice are only rolled in movement phases**, where everybody alive has a turn to
  lose. Events that happen "between" phases (famine, income, plague) are rolled when
  the preceding movement phase resolves. If they were rolled in a retreat or build
  phase, a player with nothing to do in that phase could re-roll for free. This moves
  two events slightly earlier than the board game has them; see
  `docs/machiavelli-rulings.md`.
- It is announced and counted, as above.

Roll-back is also a re-roll (the host replays the phase and new salts give new dice).
Roll-back is host-only and visible to everyone; treat it as a table decision.

Games without sealed orders (bot simulations, tests) have no secrets to build a seed
from; whoever runs them supplies the randomness. Do not play real dice games unsealed.

## Edge cases

| Situation | What happens |
|---|---|
| **Clock hits zero, some players never submitted** | A short closing stage (5 s in VR, 10 s on the web): every device seals whatever is on its sheet. Then freeze, open, resolve. |
| **Clock hits zero during the closing or opening stage** | Nothing: those stages have their own short timers and cannot be interrupted or extended by the phase clock. |
| **A device never seals** (frozen headset) | After the closing stage the seals are frozen without it. Its units hold; "No orders received". It adds nothing to the dice. |
| **Disconnect between sealing and opening** | The opening waits up to 20 s for players who are still present, not for those who have left. An unopened seal is not used: units hold, it is announced and counted. The dice are made from the sheets that were opened. |
| **The player comes back in time** | If their device still has the sheet and salt (a web browser does; a headset that only lost connection does; a headset that restarted does not), it opens normally. |
| **Host drops during the clock** | The next host loads the archive and the live state. The clock continues with the time that was left; published seals still count; players keep their sheets. |
| **Host drops during closing or opening** | Same. Sheets opened so far are carried over and re-checked; devices that had not opened yet open to the new host. |
| **Host drops during the announcement** | The phase was already resolved and saved. The new host skips the rest of the reading and moves on. |
| **No hand-over data at all** (everyone crashed; next week's session) | Resume from anyone's saved archive: the game returns at the start of the current phase, paused, as a new run. Sheets written before must be entered again. |
| **Late joiner (spectator)** | Receives the archive and live state. These contain seals and already-opened sheets only: nothing secret. |
| **Late joiner takes an empty seat mid-phase** | Starts a fresh sheet; the phase now waits for them like anyone else. After the freeze they wait for the next phase. |
| **Someone takes over a leaver's seat mid-phase** | Before the freeze: the old seal is dropped (the newcomer cannot open it) and they write a new sheet. After the freeze: nothing changes until the next phase. |
| **Resume from a game code or archive, or roll-back** | A new run: every earlier seal is void. Dice of replayed phases will differ. |
| **Only one player present in a dice game** | That player controls the dice. Unavoidable, and harmless in practice. |

## For implementers

**Formats** (all text is plain ASCII):

- Seal: `SHA-256("PS1|" salt "|" power "|" phaseKey "|" sheet)`, 64 lower-case hex digits.
  `salt`: 32 to 128 letters and digits. `phaseKey` = `timeline "." phaseNumber "." position`
  (`GameFlow.SealKey()`); treat it as opaque.
- Dice seed: `SHA-256("PD1|seed|" phaseKey "|" p ":" salt ...)` over the powers that
  opened, in power order (`GameFlow.DiceSeed`, re-derived by `VerifyLast`).
- Roll: read 28 bits from `SHA-256("PD1|" seed "|" label "|" attempt)`; if below the
  largest multiple of `sides`, the result is `value mod sides + 1`; otherwise next
  attempt (`Dice.Roll`).
- Live state: `GameFlow.LiveState` / `LoadLiveState` (format `PL1`).

**What a device must do**

- Keep sheet and salt local until it sees the frozen stage. Never put them in a synced
  variable or PlayerData before that (both are readable by everyone in the instance).
- Feed its entropy pool continuously (`OrderSeal.Stir`) with tracking data and input
  timings, and take every salt from `NextSalt()`. Never reuse a salt.
- In a phase with dice, seal even with an empty sheet (`GameFlow.NeedsSeal`).
- Open as soon as the stage is REVEAL; run `VerifyLast` after each phase and tell the
  player loudly if it fails.

**Stronger options considered (not built)**

| Option | What it would fix | Why not now |
|---|---|---|
| **Escrow shares.** When sealing, a device splits its sheet into random-looking pieces and gives one privately to every other player. If it then fails to open, the others can put the pieces together. | Withholding, and with it the dice re-roll, for players who drop after sealing. | Needs a private channel between each pair of players. VRChat's owner-targeted network events may provide one, but that could not be verified from this machine; without it, public-key encryption would have to be written in Udon (seconds of computation per player on Quest, and a large body of hand-written cryptography to trust). Also, with pieces spread over all other players, anyone absent blocks the recovery. Worth revisiting once the Unity phase can test private events. |
| **A referee client.** A spare PC account that sits in the instance as host and never plays. | Host-presence abuse; makes the host uninterested. | A fine option for groups that have one; the design does not require it. It does not fix withholding. |
| **Slow hashing of the seed** (so the last opener cannot compute the dice in time). | The dice re-roll. | A modified client can run the hash natively, thousands of times faster than Udon on a Quest. No setting works for both. |
| **Trust the host.** | Nothing; simpler. | Rejected by the owner (DECISIONS §10). |
