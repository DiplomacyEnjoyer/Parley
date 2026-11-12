# Fog of War: what is honestly possible

*You asked for a Fog of War option: you only see spaces near your own units and your
own centres. This note works out whether that can be real hidden information in
VRChat, what it would cost, and what to build. Short version at the top.*

## Recommendation

1. **Build "honour fog" in the VR world first.** Every device still holds the whole
   position; the world simply shows each player only what their power can see. It is
   cheap (the engine part is done: `Visibility.cs`), it plays exactly like real fog,
   and it is safe against everything except a deliberately modified client. Call it
   what it is in the menu: *Fog of War (honour system)*.
2. **For games where it must be real, put the secret outside the headsets.** Run the
   game on the web judge (the existing `web/` server), hosted by someone who is not
   playing. The server knows everything and tells each player only their own view.
   Players talk in VR and use the web page for the map and orders; later the world
   can show each player their private view by downloading it from the judge.
3. **Do not attempt cryptographic fog inside VRChat.** It is possible on paper and not
   in practice (reasons below).

There is one thing worth testing in the Unity phase that could move real fog into the
world itself: whether VRChat's owner-targeted network events are truly private. If
they are, a non-playing "referee" client in the instance could do the web judge's job.

## Why fog is harder than secret orders

Secret orders are secret only **for a while**. Everyone commits, then everything is
opened, and every device checks the result for itself. No machine ever needs to know
something the others must never learn. That is why sealing works without a referee.

Fog is secret **for the whole game**. To resolve a turn, something has to combine
everybody's orders with everybody's positions, including the ones you are not allowed
to see. Whatever does that combining knows everything. So the question "can fog be
real?" is the question "who is that, and can the players trust it?"

## The options

| | Who knows the whole board | Hidden from a modified client? | Cost | Verdict |
|---|---|---|---|---|
| **A. Honour fog** | every device | **No** | Small: a visibility filter (done) and a board that draws only what you see | Build first |
| **B. The host as referee** | the host's device only | From everyone but the host | Needs private messages from host to each player; the host, a player, sees all | No: gives one player the whole map |
| **C. A referee client in the instance** (a spare PC account that does not play) | that client | Yes, *if* VRChat can deliver private messages | Unknown until tested; the game then depends on that machine staying up | Test in the Unity phase |
| **D. The web judge** (a server outside VRChat) | the server | **Yes** | Moderate: fog views on the server (not built yet); someone neutral runs it; orders entered on the web page | Build when real fog is wanted |
| **E. Cryptography** (multi-party computation: the headsets jointly compute the turn without any of them seeing the inputs) | nobody | Yes | Research-grade. Every comparison in the referee becomes many rounds of messages between seven headsets, in a scripting language hundreds of times slower than normal code, over a network layer that passes a few kilobytes a second. Minutes to hours per turn, and months of specialist work to trust it | No |

### A. Honour fog, and how strong it really is

Every device receives the full position, as now, because every device still resolves
the turn itself. The world shows your power's view and nothing else: enemy units out
of sight are not drawn, the announcer reads you only the orders you could witness, and
the history shows only what you saw at the time.

What stops someone looking behind the curtain:

- **A normal player cannot.** There is no button for it.
- **A modified client can read all of it.** The data is in the device's memory.
- VRChat runs anti-cheat on PC and modified clients break its terms of service; on a
  standalone Quest, modifying the client is harder still. So the door is locked, but
  it is not a vault. Among friends in a weekly campaign that is the right price.
- Nothing can *prove* that somebody peeked. This is the essential difference from the
  sealed orders, where cheating is either impossible or leaves evidence.

A fog game also has to hide the save: the game code and the archive contain the whole
position, so in a fog game the "copy code" panel is host-only, and PlayerData saves
are, technically, readable by every device in the instance like everything else.

### B and C. A referee inside VRChat

A referee machine keeps the truth and sends each player their view. Two obstacles:

1. **Private delivery.** Synced variables and PlayerData are visible to every device.
   The only candidate for a private channel is a network event aimed at the owner of
   a particular object, which VRChat's relay should deliver to that one player.
   Whether it really is delivered to nobody else could not be checked from this
   machine (VRChat's documentation was unreachable), and it is the kind of thing to
   measure, not assume.
2. **Trust.** If the referee is the host's own headset (B), the host sees everything:
   not acceptable in a game of hidden information. If it is a spare PC logged into a
   second account that sits in the world and plays nobody (C), the secret is only as
   safe as nobody looking at that PC's screen. For a group with such a PC this is a
   good solution, and the game no longer depends on any player's connection.

With a referee, orders are no longer sealed *against the referee* (it has to read
them to resolve the turn). Seals still do one useful job: each player publishes the
seal of their sheet, and when the game is over the referee publishes everything. Then
every device can check that the referee used exactly the sealed orders and resolved
every turn correctly. The referee can still peek, but it cannot falsify the game
without being caught afterwards.

### D. The web judge

This is option C with the referee moved to where referees normally live. The web
server already runs the same engine. For fog it would keep the position to itself and
give each browser only its power's view (visibility filter, filtered announcement and
history, no game code for players). Hosted by somebody who is not in the game, or on a
small cloud machine, it is properly hidden from every player.

How it fits with VR, in order of effort:

1. **Now:** fog games are played on the web page (a phone, or a desktop window
   pinned in VR), with VRChat as the place to talk. No world changes.
2. **Later:** the world shows your private map. A world can download text from a web
   address; each player pastes their personal link once, and their headset then
   fetches their view after every turn. Each headset downloads only its own view, so
   nothing secret is shared. Limits: players must allow "untrusted URLs" in VRChat's
   settings unless the judge sits on an allow-listed host, and a world may make one
   download every five seconds.
3. **Orders from inside VR** are the hard part: a world cannot send data out. Known
   workaround: the world ships with a table of thousands of fixed addresses and
   "sends" a message by requesting a sequence of them, a few bits at a time, at one
   request per five seconds. An order sheet would take a minute or two. It works, it
   is a hack, and entering fog orders on the web page is the sensible start.

What it costs in trust: the server sees everything and serves the page, so its
operator must be neutral. The post-game audit described above applies here too.

### E. Cryptographic fog

Mentioned for completeness. Protocols exist in which several parties compute a result
from private inputs without revealing them. Resolving a Diplomacy turn that way, and
working out who can see what afterwards, would be a research project even in ordinary
code on ordinary computers. In Udon, on Quest headsets, across VRChat's sync layer, it
is out of reach. Simpler tricks (each player keeps their own units secret and reveals
them only to those who can see them) founder on the same point: deciding who can see
whom is itself a computation on everybody's secret positions.

## What "seeing" means (engine rule)

`Visibility.cs` computes, for one power, the set of provinces it sees:

- every province in which it has a unit, and every province next to one of those
  (by land or by sea);
- every supply centre it owns, and (by default) the provinces next to those;
- a walled city is seen together with the province around it.

You see a unit if you see its province. You witness an order if it is your own, or if
you see the province the unit starts in or the one it is heading for, before or after
the turn. This matches the usual "fog of war" variant played on the web sites. The
"next to a centre" part is a switch (`centresSeeNeighbours`), because some groups
prefer centres to see only themselves.

Things a fog game must also decide, left as settings for the world or the judge:

- **Ownership of centres you cannot see:** shown as last seen (each device remembers),
  never updated from hidden information.
- **The announcement:** each player hears only the orders they witnessed, so the
  reading has a different length for each player; the timing of the shared clock
  stays the same, with silence where others hear more.
- **Draw votes, eliminations and victory** are announced to everyone.
- **The Renaissance ruleset:** treasuries and expenses are public in a normal game
  (rulings document, ruling 16). In a fog game the judge would show each power only
  its own treasury and the visible effects of expenses.

## What is built, what is not

| Piece | State |
|---|---|
| Engine: who sees which provinces, units and announcement lines (`Visibility.cs`) | Built and tested (`VisibilityTests`) |
| VR world: draw only what you see (honour fog) | For the Unity phase; uses the engine piece as is |
| Web judge: fog views, filtered history, hidden game code, post-game audit | Not built. Roughly: filter `Views.State` and `Views.HistoryEntry` through `Visibility`, add a game setting, withhold code and archive from players until the game ends |
| Private network events in VRChat (for a referee client) | To be tested in the Unity phase |
