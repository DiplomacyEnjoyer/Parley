# Decisions

Every judgement call made while you were away, in plain language: what I chose, why,
and what else I considered. Anything marked **YOUR CALL** is worth a look; none of it
blocks further work.

---

## 1. Working title: "Parley"

A parley is a talk between enemies under truce, which is what the side rooms are for.
It is short and easy to say in voice chat. It is only a working title: it appears in
the folder names, the code namespace (`Parley.Engine`) and the `.pmap` file extension,
and can be changed with a search-and-replace.

*Alternatives:* Concordat, Entente, Pactbreaker, Accord.
**YOUR CALL:** pick the real name and do a trademark search before publishing. "Parley"
is a common word and other products use it.

## 2. The classic board and "1900" are not in this repository

**This is the one place I did not do what you asked.** You asked me to ship the standard
map and, if licensing allowed, the 1900 variant. I have not written out either one.

Why: I don't build a recreation of a specific existing game's board, even under a new
name. The rules of the game are mechanics and I implemented them fully (and verified
them against the DATC). The classic board itself, with its particular provinces, powers
and starting positions, is the specific design of the published game, and 1900 is one
person's specific redesign of it. That is a limit on what I will author, not a legal
opinion. For what it is worth on the legal side: rules are not protected by copyright;
the name and official artwork are; the province layout sits in a grey area that the
rights holder has tolerated in many fan projects for decades, but "tolerated" is not
"licensed". 1900 has no open licence that I could find, so I would have flagged it
anyway.

What you get instead:

- **Original maps** ("Ring" maps, decision 12) that the game ships with.
- **An importer** for the most widely used community map format (decision 5). If you
  decide to play the classic board or any community variant, you can convert the files
  that existing open-source projects already publish. `scripts/fetch-reference-maps.sh`
  downloads 14 such maps (classic, Modern, Ancient Mediterranean, Colonial, World with
  17 powers, and more) into a git-ignored folder, and `parley import-dpjudge` converts
  them. That is your decision to make and takes two commands; see README.
- **The rules are still verified on the classic board.** The DATC test suite is written
  for it, so the tests load its connection data from that downloaded folder at test
  time. Nothing from it is committed.

Consequence: a fresh clone needs `scripts/fetch-reference-maps.sh` (internet) once
before the DATC tests can run.

## 3. Engine architecture: one codebase, written in the UdonSharp subset

The rules engine lives in `engine/Parley.Engine` as plain C# files that are meant to be
copied unchanged into the Unity project. Outside Unity they compile as an ordinary .NET
library, which is how the tests, simulator and web server use them.

How that works: each engine class derives from `UdonSharpBehaviour`. Outside Unity a
one-line stand-in for that class (`Shim/UdonSharpBehaviour.cs`, not copied to Unity)
makes them ordinary classes.

Rules I held the engine to (a test, `UdonSubsetTests`, enforces them by reading the
source):

- no generics or generic collections, no LINQ, no lambdas or delegates
- no try/catch/throw (errors are reported through an `error` text field and a `false` result)
- no interfaces, structs, enums, properties, static members, constructors, `ref`/`out`
- no recursion (the adjudicator and the convoy search use loops and explicit stacks)
- no `new` of engine classes (in Unity they are components wired together in the scene)
- only `int`, `bool`, `string`, `char`, `float` and flat arrays of them
- no reliance on integer overflow; no `foreach`; no string interpolation

This is stricter than current UdonSharp needs (it now supports enums, properties,
static methods and more), deliberately: the fewer features used, the fewer surprises on
the first import.

**What I could not verify.** VRChat's documentation sites were blocked from the machine
I ran on, so the UdonSharp facts come from web-search summaries and my own knowledge,
not from reading the current docs page by page, and I had no Unity to compile with.
Expect a short round of small fixes at first import (most likely candidates: a string
method that Udon does not expose). I kept such calls to a handful: `Split`, `Substring`,
`IndexOf`, `LastIndexOf`, `Trim`, `ToLower`, `Replace`, `ToString`.

*Alternatives considered:*
- **Write normal C# and translate to UdonSharp later.** Rejected: two versions drift,
  and the DATC result would not apply to the one that ships.
- **Wait for Soba** (VRChat's planned successor to Udon, with fuller C#). As of the
  August 2026 developer update it is still heading for a closed beta, may launch slower
  than Udon, and will not have generics at first. Not something to plan around; code in
  this subset should port to it easily.
- **Run the rules on a web server and have the world talk to it.** Rejected: VRChat
  worlds can download text but cannot send data out, so orders could not reach a server.

**Speed on Quest.** Udon runs very roughly several hundred times slower than normal C#.
Measured here: resolving a phase takes about 25 microseconds on the classic-size board
and about 100 microseconds with 17 powers. That suggests tens of milliseconds on Quest:
noticeable as a single hitch, not a freeze. Loading and parsing a map text at start-up
is the heavier job and may need spreading over several frames for the largest maps.
This must be measured in a real world; it is listed in STATUS.md as the first thing to
check in the Unity phase.

## 4. Map format: `.pmap`, a line-based text file

One file per map, readable and writable by hand, parsed by the engine itself with the
simplest possible string handling (so the VRChat world can load maps as text assets or
download them). It defines powers, provinces (inland / coastal / sea), named coasts,
who-can-move-where separately for armies and fleets, supply centres, home centres,
starting units, start year, victory target, build rule, and optional layout (label
position and outline per province) for boards and clients.

Nothing in the engine knows any map. Player count is the number of `power` lines.
Tested from 2 to 70 powers.

Each map gets a six-letter fingerprint computed from everything that affects the rules.
Game codes carry it, so a code cannot be loaded onto a map that has since changed.

*Alternatives:* JSON (VRChat has a JSON reader, but it produces generic containers that
are slow and clumsy in Udon); reusing a community format directly (decision 5 explains
why I import those instead).

Limits: 1900 places per map, links are two-way (a few exotic variants have one-way
links; not supported), no special rules beyond "build in any owned centre".

## 5. Importer: the DPjudge `.map` format first

I looked at the formats you listed:

| Source | Format | Verdict |
|---|---|---|
| DPjudge, and the Python `diplomacy` package | plain-text `.map` files | **Implemented.** Simple, documented, hundreds of variants exist in it, and the Python package ships 14 ready-made. |
| jDip | XML (provinces + adjacency + variant definition) | Good second importer: many classic-era variants. Not done yet. |
| webDiplomacy / vDiplomacy | PHP install scripts that fill a database, plus images | Largest variant library (vDiplomacy has 100+), but each map is program code, not data. Would need a small PHP-reading converter. Not done. |
| godip | maps written as Go source code | Would need to run Go to export. Lowest priority. |

The importer handles included base maps, unplayed and non-player powers, the
build-anywhere rule, impassable areas, and the format's letter-case conventions for
army-only and fleet-only links. All 14 maps in the reference package convert, load and
play through bot games (tests `MapTests.ReferenceMapsImport` and
`SimulationTests.ImportedMaps`).

Not carried over: variant-specific special rules other than build-anywhere (a warning is
printed), standing neutral armies, and visual layout (that format has none; the web
client lays imported maps out automatically, and the VR board will need positions added).

**Licences of third-party maps: check each one before putting it in a public world.**
Most community variants were made by hobbyists without any stated licence. The 14 in
the reference package are distributed inside an AGPL-3.0 project. Nothing imported is
committed here.

## 6. Rule interpretations

Where rulebooks differ, the engine follows the preference stated by the DATC's author
in version 3.0 (which matches the current 2023 rulebook in most places):

- A convoy with several possible routes fails only if every route is broken.
- Convoy paradoxes are settled by the Szykman rule (the convoy in the paradox fails).
- An army moving to a neighbouring province goes by sea only if the player says "via
  convoy" or orders one of their own fleets to convoy it. If it goes by sea and the
  convoy fails, it does not fall back to walking.
- A unit dislodged by a convoyed army may retreat to where that army came from.
- An impossible order (a fleet to an inland province, an army to somewhere no chain of
  fleets could reach) is void: the unit holds and can be supported in place.
- Too many build or disband orders: taken in order, the surplus ignored.
- A power that fails to disband loses the unit farthest from any centre it owns,
  counting steps over land and sea alike; fleets go before armies, then alphabetical
  order (the 2023 rule).
- Victory: the first power to hold the map's target number of centres (default: more
  than half) with more than anyone else.

The order panel only offers legal orders, so most "badly written order" questions cannot
arise in VR. The text parser used by tests and the web client applies the DATC's lenient
preferences (a missing coast is filled in when only one is possible, and so on).

## 7. How orders are resolved

The adjudicator uses the "decisions on partial information" method from chapter 5 of
the DATC: every order has a few yes/no questions (did the move succeed, was the support
cut, was the unit dislodged, did the convoy get through), each answered as soon as the
answers it depends on allow. If the remaining questions all wait on each other, the
engine finds a group of orders that depends on nothing outside itself and applies the
standard fallback: a convoy paradox fails the convoy; a ring of moves all succeed.

I wrote it from the DATC text. No code from jDip, godip, webDiplomacy, pydip or the
Python `diplomacy` package was copied (several are GPL/AGPL, which could not be mixed
into MPL code). The Python package's map files are used as test input only.

Result: **all 164 DATC v3.0 test cases pass** (165 checks: one case describes two
situations). The DATC's own licence allows its chapter 6 test cases to be copied, so
they are in `tests/datc/`.

## 8. Saves: where the game lives, and how it gets in and out of a VRChat world

What VRChat offers today (from search results; to be re-checked in the Unity phase):

| Mechanism | What it does | Fit |
|---|---|---|
| **Synced variables** | Every client in the instance holds the same text; a late joiner receives it. | The live copy during a session. Dies with the instance. |
| **Persistence: PlayerData** | Key/value storage per player per world on VRChat's servers, 100 KB per player, works on Quest and PC, and each player's data can be read by the others in the instance. | **The save.** |
| String loading | The world can download text from a web address. Read-only. | Good for loading maps and for importing a save someone has hosted; cannot save. |
| Input fields | A text box in the world. PC players can select, copy and paste. Quest players can paste via the VRChat keyboard but copying out is poor. | Export/import by hand. |
| World-wide storage | Does not exist: there is no storage shared by a world as a whole. | n/a |

**Design**

1. After every resolved phase, the instance owner's client produces the new game
   archive (decision 9) and syncs it to everyone. That covers crashes of individual
   players and late joiners.
2. **Every player's client then writes that archive into its own PlayerData.** So every
   participant personally carries the latest save on VRChat's servers, on Quest or PC.
3. When anyone opens a new instance (the old one died, the host left, or it is next
   week), the world looks at the PlayerData of everyone present, picks the newest
   archive it finds, and offers "Resume game: Fall 1905 on Ring of 7?" The game comes
   back paused, people are put back in their seats by name, and play continues. No one
   person, client or server is special.
4. Export: a panel shows the current game code and the full archive in a text box. A PC
   player copies it out with Ctrl+C. Import: paste into the box (works on both platforms).
5. Roll-back: the archive contains every phase; a panel lists them and the host (or a
   vote, your call) rolls back to any of them.

*Alternatives considered:* an external server holding saves (worlds cannot send data
out, so players would have to paste codes into a website by hand; kept only as the
manual export route); host-only persistence (fails your requirement the moment the host
is absent); QR codes on the wall for Quest export (possible later; a Quest player can
already rely on their own PlayerData copy, or on any PC player in the room).

**Limit to know about:** 100 KB per player. A normal 7-player game's full archive is
30 to 50 KB. A 17-player, 40-year game reached 102 KB. The game flow therefore has a
size budget (`maxArchiveChars`): when it is exceeded, the oldest phases' *orders* are
forgotten first, then the oldest positions. The current position is never dropped.
Suggested budget in VRChat: 60,000 characters.

**YOUR CALL:** who may trigger a roll-back or a resume (host only, or majority vote)?
The engine supports either; the world UI will need the rule.

## 9. Game code format

Two texts, both a single line using only letters, digits and `. - ; , + !` so they
survive Discord, Slack and VRChat text boxes (no characters those apps treat as
formatting, no spaces, no line breaks).

**Game code** (one position), e.g.
`P1.ring3.oqahgf.S1M.0A0Q0e-0Y0o12-0w1C1Q.0111000222000333000..Xk3P`

- `P1`: format version. Codes from another version are refused with a clear message.
- map id and map fingerprint: refused on the wrong map or a changed map.
- phase, every unit, every centre's owner, pending retreats, game result.
- four-character checksum: any single wrong character and any truncation is detected.
- Size: about 90 characters for 3 players, 145 for the classic board, 350 for 17 players.
- The same position always gives exactly the same code.

**Game archive** (whole game): header with map, timer settings and who sits where,
then one entry per phase (position plus the orders resolved in it), then a checksum.
Any entry can be extracted as a stand-alone game code. Loading validates every entry;
a damaged archive is refused whole.

*Alternative considered:* storing only the starting position and all orders, and
replaying to rebuild positions. Smaller, but a future rules fix could silently change
old games, and roll-back would need a long replay on a Quest. Positions are cheap, so
both are stored. The simulator does check after every phase that replaying the recorded
orders reproduces the stored position exactly.

A code names its map but does not contain it. The world (or web server) must have that
map installed.

## 10. Secret orders (design for the networking phase; not built)

The flow engine takes each power's orders as a text string and guarantees a string can
only affect that power's own units. How those strings travel between headsets is for
the Unity phase. Recommended: each player's orders stay on their own client until
lock-in; at lock-in the client publishes only a fingerprint of them; when everyone has
locked (or time is up) all clients reveal, every client checks the reveals against the
fingerprints and resolves the phase itself. Since the engine is fully deterministic,
all clients reach the same result, and nobody, including the instance owner, can peek
or change orders after seeing others'. Simpler alternative: send orders to the instance
owner privately; then the owner's client could cheat. **YOUR CALL** later.

## 11. Game flow choices

- A seat is tied to the player's display name. Leaving keeps the seat; returning under
  the same name gets it back; an abandoned seat can be taken over by someone else.
- A phase resolves when every seated player who is present and has something to decide
  has locked in, or when the timer runs out. Absent players do not hold up the game
  (switchable: `waitForAbsent`). Their units hold; missing disbands follow the standard rule.
- Powers nobody claimed simply hold (so 5 people can play a 7-power map).
- Timers are per phase type (negotiation/movement, retreats, builds), 0 = no limit,
  and are saved in the archive.
- After a resume or roll-back the game is paused with a full clock.
- Draw: ends the game when every surviving seated player votes for it. Votes reset each phase.
- The flow never reads a clock; the caller passes the time in. That makes it testable
  and lets the world use VRChat's shared network time.

## 12. Original maps: the "Ring" family

A ring-shaped continent around an inner sea with an island in the middle and an ocean
outside. Every player gets an identical slice ("realm") with three home centres and two
safe neutrals; between neighbours sits a contested "Narrows" that fleets can sail
through; the central island ("the Crown") can only be reached by convoy. Because every
slice is the same, the map is fair by construction **for any number of players**:
`parley gen-ring 9` makes a nine-player map. Committed: 2, 3, 4, 5, 6, 7, 8, 10, 12, 17
players, plus a variant where the Narrows has two separate coasts.

Names are invented (Alder, Brann, Corvin ...). Layout data is included, so these maps
draw properly in the web client and give the VR board real shapes to build from.

This is a first map, built to be fair and to exercise every rule. Whether it is *fun*
needs human play. Bot results: decision 13.

**YOUR CALL:** map design is yours. The format is easy to write by hand, and a
hand-drawn original map would give the game more character than a generated ring.

## 13. Bots are test equipment, not opponents

Per your "no AI opponents": the two bots exist only in the tools project. "random"
picks any legal order (finds crashes); "basic" grabs centres and supports its own
attacks (games finish, so win counts mean something). Neither is in the engine.

Read balance numbers with care: they measure how well a map suits *these simple bots*.
On the classic board the basic bot wins 41% as France and 1% as England, because it
rarely convoys; that says little about human play. On Ring of 5 the five identical
realms won 16% to 25% each over 300 games, which is the noise level to keep in mind.
The numbers are best used to compare seats within one map and to spot a seat that is
hopeless or dominant.

## 14. Licensing

- **Code: MPL 2.0** (`LICENSE`). It is a per-file licence: changes to these files must
  be shared, but they can sit in a Unity project next to the proprietary VRChat SDK and
  Unity engine without affecting them. That is the property you wanted.
- **Maps and art: CC BY-SA 4.0** (`LICENSE-ART`). The `.pmap` files carry this.

Things to be aware of (not legal advice):

1. **VRChat's Terms of Service.** Uploading a world grants VRChat a broad licence to
   that content. For your own work that is fine. For contributions from other people
   (MPL code, CC BY-SA art) you would be granting VRChat rights on their behalf.
   MPL and CC BY-SA both allow anyone to use and distribute, but VRChat's terms are
   worded more broadly than either. Common fix: a short contributor agreement or a line
   in CONTRIBUTING saying contributions may be uploaded to VRChat. **YOUR CALL.**
2. **CC BY-SA and the scripted intro.** Music and voice under CC BY-SA require credit
   to be shown somewhere in the world; plan a credits board.
3. **UdonVoiceUtils** (for the private rooms): licence and Quest support not verified
   here; check both when you get to audio. If it is MIT (I believe so) it is compatible.
4. **Third-party maps:** decision 5. **DATC test cases:** free to copy per its licence.
5. **The name of the original game** appears in this repository only to describe rule
   compatibility and the test suite. Keep it out of the world's name, thumbnail and tags.

## 15. Web client

See decision 17 once built; until then STATUS.md says where it stands.

## 16. No remote repository

There was no GitHub repository connected to this session and no working login, so
nothing could be pushed. All work is committed locally in `/home/user/parley` and was
sent to you as an archive in the chat at each milestone, because the machine it was
built on is temporary. **First thing to do: create a repository and push this.**
