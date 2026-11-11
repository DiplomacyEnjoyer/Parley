# VR world design notes

These are the project owner's design notes for the VRChat world, recorded as given.
**Nothing here is built yet.** The engine and web client implement the turn flow
(section 1) so it can be tested before any Unity work; everything visual is for the
later Unity phase. Game mechanics come first; visuals and animation after the game works.

Where a note raises a question for hand tracking, it is flagged in section 4.

---

## 1. Turn flow

### Timers

- Movement phases start at **30 minutes** of negotiation for the first couple of turns,
  then switch to **15 minutes**.
- The schedule is configurable; it will be experimented with.
- Retreat and build phases get short timers (current values: DECISIONS.md §19).

### Clock

- In VR, a **big 3D countdown clock floats above everyone**, always ticking down.

### Board view

- Any player can pull up the current board at any time.
- It highlights **your own units** and **your possible moves**.

### Order sheet

- Any player can pull up their **private order sheet** at any time (for example, a
  button on the right hand).
- **Only that player can see it.**
- It lists all of your units, and for each one the possible orders, chosen by simple taps.
- You can work on it secretly throughout the turn while talking to people and looking
  at the map.

### Submit toggle

- Working on the sheet does not mean you are done. You are only done when you flip the
  **submit toggle**.
- You can flip it back off to keep editing.
- If every player is submitted before the timer runs out, the phase ends early.

### Timer reaching zero

- Whatever is on your sheet becomes your orders automatically.
- Units with nothing entered hold.

### Pausing

- There should be a way to pause the timer for real-world interruptions (bathroom,
  someone's connection, and so on), **not as a strategic tool**.
- Open question from the owner: how to stop a conspiring majority from abusing a vote
  to extend time; maybe it is just an honor-system thing among players.
- First version: the host can pause and resume. Alternatives are listed in
  DECISIONS.md §21 as an open question.

### End of phase

- When everyone has submitted or the clock hits zero, **a sound plays** (to be chosen
  later) signalling that the round is over and everyone should return to the map.
- Then a brief pause of a couple of seconds.

### Announcement

- The orders are **announced publicly, one country at a time, by an authoritative
  announcer voice**.
- This matters because players need to know what the people they negotiated with
  actually did.
- As each order is announced, **the pieces move toward where they were ordered**.
- When two orders conflict, **both pieces visibly move to the same place**.

### Adjudication

- It runs instantly afterwards and the board shows the result.

### Ready-up

- Before the next round's timer starts, **everyone has to click Ready**.
- That gives people a natural break to chat or step away.

---

## 2. The world

### The map

- A **big 3D map in the center of the main hall**, outside all the private talking rooms.
- In the style of the war-table maps in *Game of Thrones* / *House of the Dragon*, with
  **physical army and fleet pieces** on it.

### Movement

- **Little animations when armies and fleets move, and when a fleet convoys an army**,
  so it is not just blocks and colors.

### Battles

- A simple **puff of smoke with battle sounds and some small explosions**.
- Ideally this reflects things like three armies attacking one, if that is not too hard.
- Since adjudication resolves everything at once, these can play together.

### Personal board view and order sheet

- A **summonable personal board view** and **order sheet**, as described in section 1.
- These **must work with hand tracking alone (no controllers)**, with **big tap targets**.
- Anything in the design that would really need controllers must be flagged (section 4).

### Sound

- An **end-of-round sound**.
- An **announcer voice**.
- Both original; nothing copyrighted.

### Priorities

- **Game mechanics come first; visuals and animation after the game works.**

---

## 3. What the engine already provides for this

So the Unity phase does not have to invent it:

| Design note | Engine support |
|---|---|
| Timer schedule (30, 30, then 15 minutes) | `GameFlow.SetMoveSchedule`, saved in the game archive |
| Submit toggle, early finish, auto-submit at zero | `GameFlow` stages and `SetSubmitted` |
| Host pause and resume | `GameFlow.Pause` / `Resume` |
| End-of-phase sound, short pause | `GameFlow.STAGE_ANNOUNCE` begins with a configurable lead-in (`announceLeadSeconds`); the world plays the sound when the stage begins |
| Announcement, one country at a time | `Announcement` lists every order grouped by country, in a fixed order, with the text to speak, the unit, and where the piece should travel |
| "Pieces move toward where they were ordered", conflicts visibly meet | Each announced order carries the *intended* destination, before any result is known; two units ordered to one place both get that place |
| Result shown afterwards | Each announced order also carries its outcome (moved, bounced, dislodged, cut), and the list of contested places with how many units went for each (for the battle puffs) |
| Ready-up | `GameFlow.STAGE_READY`, `SetReady` |
| Board view highlights | `OptionGenerator` lists every legal order for a unit |
| Secret order sheet | Commit-and-reveal exchange: `OrderSeal` (DECISIONS.md §18) |

---

## 4. Hand-tracking review: what might really need controllers

Flags on the notes above, to settle in the Unity phase with a headset on:

1. **"A button on the right hand" to summon the sheet.** With hand tracking there are
   no buttons. VRChat maps a few hand gestures to inputs, but worlds cannot define new
   gestures, and the system pinch is already used for the menu and for grabbing. Safer
   options that need no controller: a **wrist-mounted panel** that appears when you turn
   your palm toward your face (worlds can read hand bone positions), a floating button
   that follows the player at waist height, or physical "summon" plinths around the hall.
   Needs testing: palm-facing detection must not trigger while someone gestures in
   conversation.
2. **Tapping orders.** Poking flat buttons with a fingertip works with hand tracking, but
   tracking is jittery at arm's length and when one hand covers the other. Targets should
   be large (at least about 5 cm across, with gaps), need no double-taps, long presses,
   drags or scrolling, and give a clear click sound and colour change on each tap. The
   three-tap order sequence (unit, order type, target) fits this.
3. **Tapping provinces on a personal board view.** Small provinces are hard to hit with a
   fingertip. The order sheet should therefore work **by lists alone** (unit → order →
   target from a list of big buttons), with the board view as a display that highlights
   what the list selection means, not as the only way to choose a target.
4. **The submit toggle** must be hard to flip by accident (a hand brushing the panel):
   a large, separate switch with a clear on/off state, placed away from the order buttons.
5. **Moving around the hall and into private rooms.** VRChat's hand-tracking locomotion
   is limited (teleport-style gestures); a large hall with many rooms costs hand-tracking
   players more effort than controller players. Consider teleport pads or a "go to room"
   list on the wrist panel so nobody is slower to reach a private conversation.
6. **Text entry** (pasting a game code to resume) needs VRChat's keyboard, which is slow
   with hand tracking. The resume flow already avoids it: saves travel in each player's
   own VRChat storage and the world offers "Resume game?" (DECISIONS.md §8).
7. **Private sheet visibility.** "Only that player can see it" is done by showing the
   panel only on the owner's own device (it is never networked), which works the same
   with hands or controllers. The orders themselves stay on the owner's device until
   everyone is locked in (DECISIONS.md §18).

Nothing in the notes strictly requires controllers. Items 1 and 3 are the ones most
likely to need a redesign once tried with real hands.
