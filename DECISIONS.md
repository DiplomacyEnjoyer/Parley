# Decisions

Every judgement call made while you were away, in plain language: what I chose, why,
and what else I considered. Anything marked **YOUR CALL** is worth a look; none of it
blocks further work.

---

## 1. Working title: "Parley" (owner: keep it)

A parley is a talk between enemies under truce, which is what the side rooms are for.
It is short and easy to say in voice chat. **The owner decided the working title stays
"Parley".** It appears in the folder names, the code namespace (`Parley.Engine`) and the
`.pmap` file extension. Before publishing, a trademark search is still sensible:
"Parley" is a common word and other products use it.

## 2. The classic board is the default map, fetched at setup time (owner's decision)

**Owner's decision (second session):** the classic board must be the default map,
because the theme is part of why people play. It is not authored or drawn here; it is
taken at setup time from existing open-source fan projects, the same way the rule tests
already got their data. The owner accepts the low risk of using fan-distributed maps for now.

How it works:

- `scripts/fetch-reference-maps.sh` downloads the Python `diplomacy` package from PyPI,
  copies its DPjudge-format map files and its board pictures (SVG) into `third_party/`,
  converts the classic board (plus Modern and Ancient Mediterranean, which also have
  pictures) into `maps-local/*.pmap`, and matches each picture to the map province by
  province into `maps-local/*.art.json` (decision 17).
- The web client loads everything in `maps-local/` and preselects "Standard" when it is
  there. Without the script, only the generated Ring test maps are offered.
- Nothing downloaded or converted is committed. Licence status of each source:
  `THIRD_PARTY.md`.
- The Ring maps (decision 12) are kept as **test maps only**, not launch maps.

*Earlier position (first session), kept for the record:* I had declined to write out the
classic board myself, and still do not author or draw it; the fetch-at-setup approach
uses other people's existing published files instead.

What remains open: publishing a VRChat world containing the classic picture would
redistribute GPL artwork (THIRD_PARTY.md suggests options). The 1900 variant: see
`docs/variants-research.md` (when written).

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

**Second opinion.** Passing a test suite I encoded myself is not proof, so I also
compared the engine with an independent one: 20,000 random situations on the classic
reference board (positions from bot games; half sensible orders, half wild) were
resolved by both Parley and the open-source Python `diplomacy` package, run as a
separate program. **19,988 gave identical results. The other 12 are all one known
difference between rulebook editions**: when an army is sent to a neighbouring province
and its own power also orders a fleet to convoy it, Parley (following DATC 3.0 and the
2023 rulebook wording) uses the convoy and the army stays put if the convoy fails; the
other engine follows the older preference and lets it walk instead. No other
disagreement was found. Re-run with `parley crosscheck-export` and
`scripts/crosscheck.py`.

**Second session: whole games, every phase type, two more engines.** `parley
crosscheck-games` writes whole bot games (movement, retreats, builds, forced disbands,
solo victories). Each game is replayed from the start in another engine and the position,
dislodged units, centres and next phase are compared after every phase.

| Engine | Games | Phases | Movement | Retreats | Builds/disbands |
|---|---|---|---|---|---|
| Python `diplomacy` (`scripts/crosscheck_games.py`) | 400 | 14,896 | 8,299 / 8,306 | 2,525 / 2,525 | 3,880 / 4,065 |
| godip, the Diplicity engine (`tools/crosscheck-godip`) | 400 | 14,928 | 8,315 / 8,323 | 2,526 / 2,526 | 3,895 / 4,079 |

Every difference was examined:

- **Forced disbands** (all build/disband differences): when a power fails to order
  enough disbands, the rules pick the unit. Parley follows the 2023 rule (farthest from
  home, fleets first); both other engines use older rules. Unit counts and centres agree;
  only the choice of unit differs. In the half of the games where every disband is
  ordered explicitly, builds and disbands match **exactly** in both engines.
- **Convoy to a neighbouring province** (7 and 8 movement phases): the known
  rule-edition difference above.
- **One godip case** (1 phase in 14,928): a head-to-head battle where one move is
  supported by the owner of the unit it attacks. Parley bounces both units, which is what
  the DATC's definitions give (section 5: a move's *defend strength* counts all its
  supports; only the attack on the supporter's own unit leaves that support out; see also
  DATC 6.E.3 and 6.E.12). godip lets the other unit through. I believe this is a godip
  deviation, not a Parley bug; the Python engine agrees with Parley.

*jDip as a third adjudicator:* not done. Its engine is Java with no command-line
adjudication; a harness would need its classes. jDip's *data* is cross-checked instead
(its Standard board converts to exactly the DPjudge board).

**Owner's decision: keep the newest DATC ruling** (no walking fallback). Background:
that rule can surprise players. In VR the
panel makes the choice explicit (move over land / move by convoy), so it should rarely
bite. If you would rather have the older, more forgiving behaviour, it is a few lines
in `Adjudicator.PrepareOrders`, at the cost of four DATC cases (6.G.8, 6.G.11, 6.G.20
and the like) changing to their "version 2" answers.

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

**Owner's decision: resume and roll-back are host only, no voting.** Every client keeps
the save, so a dropped host never loses the game: in VR each player's PlayerData holds the
archive; in the web client every browser keeps a copy of the archive in its local storage
and offers "Resume it here" if a server has lost the game, and the host role passes to
another player when the host's browser goes away (decision 22). A player who leaves: their
units stand still (they hold, and can be attacked like anyone's), and someone else can
take over the seat.

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

## 10. Secret orders: tamper-proof (owner's decision) — see decision 18

The owner chose **tamper-proof, not trust-the-host**: nobody's machine, the host's
included, sees anyone else's orders until all are locked in. Built as commit-and-reveal
with salted SHA-256 seals; full design and edge cases in decision 18.

## 11. Game flow choices

See decisions 18 to 21 for the turn flow the owner designed (timer schedule, submit
toggle, sealing, announcement, ready-up, pause). Other choices:

- A seat is tied to the player's display name. Leaving keeps the seat; returning under
  the same name gets it back; an abandoned seat can be taken over by someone else. A
  newcomer taking over mid-phase starts the sheet afresh (they could not open the
  previous player's seal anyway).
- A player who has left is not waited for (switchable: `waitForAbsent`, for slow
  play-by-Discord games). Their units hold; missing disbands follow the standard rule.
- Powers nobody claimed simply hold (so 5 people can play a 7-power map).
- If the clock runs out while nobody at all is present, the game pauses instead of
  playing on in an empty room.
- A retreat or build phase in which nobody present has a decision moves straight on.
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

**Owner's decision:** the Ring maps stay as **test maps only**, not launch maps. The
classic board is the default (decision 2).

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
   in CONTRIBUTING saying contributions may be uploaded to VRChat. **Owner: not needed
   yet.** Revisit before accepting outside contributions to anything that goes into the
   uploaded world.
2. **CC BY-SA and the scripted intro.** Music and voice under CC BY-SA require credit
   to be shown somewhere in the world; plan a credits board.
3. **UdonVoiceUtils** (for the private rooms): licence and Quest support not verified
   here; check both when you get to audio. If it is MIT (I believe so) it is compatible.
4. **Third-party maps:** decision 5. **DATC test cases:** free to copy per its licence.
5. **The name of the original game** appears in this repository only to describe rule
   compatibility and the test suite. Keep it out of the world's name, thumbnail and tags.

## 15. Web client: a small server running the real engine

For playing over Discord before VR exists. Details and instructions: `web/README.md`.

- **A server, not a static page.** Secret orders need somewhere neutral to sit until
  everyone has locked in. A small .NET server uses the engine directly, so the web game
  and the future VR game run identical rules code. *Alternative:* compile the engine to
  run inside the browser and pass codes around by hand; rejected because someone would
  have to collect everyone's secret orders manually each turn.
- **Order entry copies the VR tap sequence** (unit, order type, target; only legal
  options offered). So playing here also tests the option generator the VR panel will use.
- **The server is not trusted with orders** (second session). Each browser keeps its
  sheet in its own storage and sends only a seal; the server learns the orders when every
  seal is frozen, exactly as in VR (decision 18).
- **Hosting is on you.** Run it on your PC and share it through a free tunnel, or use
  the Dockerfile on any small server. The Dockerfile is written but untested.
- **Imported maps with a picture** (classic, Modern, Ancient Mediterranean) draw the
  real board; the rest are drawn as dots and lines by an automatic layout.
- Tested with an automated three-browser play-through (join, take seats, enter orders
  by clicking, lock in, resolve, view history, reload, resume from a code). Not yet
  played by humans.

## 16. Repository

The project lives at github.com/DiplomacyEnjoyer/Parley; work is pushed to `main` in
small steps. The leftover `initial-engine` branch is fully contained in `main` and
should be deleted, but this session's GitHub access was refused the deletion (HTTP 403
on `git push --delete`). Delete it from the GitHub web page (Branches, bin icon).

## 17. Classic board picture: the Python package's SVG

Candidates were the Python `diplomacy` package (`maps/svg/`), godip's classical variant,
and jDip's variant packs. **Chosen: the Python package's `standard.svg`**, because:

- it comes from the same package as the map data we already import, so province names
  match one to one (five sea areas use alternative names, resolved through the map
  file's own alias list);
- it is a clean SVG with a separate clickable outline per province, unit positions for
  every province and coast, label text and supply-centre markers: exactly what a board
  needs;
- it downloads from PyPI, which works where GitHub does not (this machine could not
  reach github.com; godip is reachable through the Go module proxy, jDip through
  SourceForge, both checked).

It is jDip's artwork (GPL), so the same file family covers jDip. The package also has
pictures for Modern and Ancient Mediterranean, which are installed too.

The converter (`parley import-svg`, `tools/Parley.Tools/SvgArt.cs`) keeps only plain
shapes and text, because the markup ends up inside the web page: no scripts, links,
images, styles or event handlers survive, and a test checks this.

## 18. Tamper-proof secret orders: commit and reveal

**The problem.** Orders are secret until everyone is done. If any one machine (the
instance owner's, a server) collects them, its owner can peek or change them.

**The design.**

1. While the clock runs, each player's sheet exists only on their own device.
2. Flipping **submit on** publishes a *seal*: SHA-256 of
   `PS1|salt|power|phaseKey|orders`. The salt is 32 random hex digits that never leave the
   device until step 4; `phaseKey` ties the seal to this exact position, so it cannot be
   replayed in another phase or game. Flipping submit off withdraws the seal.
3. When everyone present has submitted, or the clock runs out, seals are **frozen**.
4. Only then does each device publish its salt and orders ("opening"). Every device
   checks each opening against the frozen seal; a mismatch is refused. Because the seal
   was published before anyone's orders were visible, nobody can adapt their orders to
   anybody else's.
5. All devices resolve the phase themselves (the engine is deterministic) and keep the
   seals, salts and sheets, so anyone can re-check afterwards.

**What UdonSharp can use for hashing.** Udon does not expose .NET's cryptography
classes to world scripts, so hashing must be written in Udon itself; the community
library UdonHashLib (MIT) does exactly that, which confirms it works. (I could not read
VRChat's own documentation from this machine; this is from the library and earlier
research, to be confirmed at the first Unity import.) Parley has its own SHA-256 in
`OrderSeal.cs`, written to the engine's subset: it keeps each 32-bit word as two 16-bit
halves in ordinary `int`s, so nothing depends on integer overflow behaviour, which is
the riskiest thing to assume in Udon. Tested against published test vectors and against
.NET's SHA-256 on every length from 0 to 200 characters and on non-English text. The web
client uses a plain-JavaScript SHA-256 with the same input format.

*Speed:* a seal hashes about 100 characters; tens of thousands of times faster than
anything a player would notice even at Udon's speed.

**Edge cases.**

| Situation | What happens |
|---|---|
| Timer reaches zero, some players never flipped submit | Stage CLOSING: every device seals whatever is on its sheet automatically (5 seconds in VR, 10 in the web client, which polls). Units with nothing entered hold. |
| A device never seals (frozen headset) | After the grace period seals are frozen without it; that power's units hold; the announcement says "No orders received". |
| Disconnect between sealing and opening | Opening waits up to 20 seconds (`revealSeconds`) for players who are still present; players who have left are not waited for. Unopened seals are **not** used: those units hold, and the announcement says "Sealed orders were never opened". Rationale: nobody else can open the seal, and waiting indefinitely would let one player stall everyone. |
| The player comes back in time | Their device still has salt and sheet, and opens normally. |
| Somebody takes over the seat mid-phase | The old seal is discarded; the newcomer starts a fresh sheet. |
| Someone tries to open different orders | Refused ("these orders do not match the seal"); after the window their units hold. |
| Host override | The host can end any waiting stage early ("End the phase now", "Stop waiting for sheets"). This cannot reveal or change anyone's orders. |

*Alternative considered:* trust the instance owner (simpler; rejected by the owner).

## 19. Timer schedule

Movement (negotiation) phases follow a schedule: **30 minutes for the first two, then
15 minutes**, as the owner described. The schedule is a list (`GameFlow.SetMoveSchedule`)
whose last entry repeats, saved in the game archive so a resumed game keeps its pace,
and editable by the host in the web client ("30, 30, 15"). The archive format changed
to store it; old `PA1` archives with a single number still load.

Retreat and build phases: **2 minutes for retreats, 3 for builds.** They involve no
negotiation; 2 minutes is enough to pick a retreat from a short list, and builds
sometimes need a word with an ally about which unit to build where. Both are settings.
A retreat or build phase in which nobody present has anything to decide ends at once.

## 20. End of phase, announcement, adjudication, ready-up

As the owner described:

- **End of phase** (everyone submitted, or zero): the world plays the end-of-round
  sound (sound to be chosen; the web client plays a placeholder chime), then a short
  pause (`announceLeadSeconds`, 3 s).
- **Announcement**: the engine's `Announcement` lists every order grouped **country by
  country** (in the map's power order), with the text the announcer reads (for example
  "Army Paris moves to Burgundy"), the unit's starting place and the **intended
  destination** (known before any result, so two pieces ordered to the same place both
  head there), plus each power's hand-in status. Timing: 1.5 s to name a country, 3 s per
  order, then 6 s of results; every device computes the same reading position from the
  same clock (`GameFlow.LocateAnnouncement`). In a 7-player opening (22 units) that is
  about 1 minute 30; it can be set to "fast" or "off".
- **Adjudication** runs instantly; the result (moved, bounced, cut, dislodged) is in the
  same list, plus every contested province with how many units went for it and how many
  supports counted, for the battle effects.
- **Ready-up**: before each negotiation clock starts, everyone present must press Ready.
  The host can start the clock anyway (for someone who stepped away). Players who have
  left are not waited for. The web client lets the game creator switch ready-up off.

## 21. Pausing — OPEN QUESTION for the owner

Built: **the host can pause and resume** the clock. While paused, players can still work
on their sheets and flip submit, but nothing ends early.

The worry: a conspiring majority could use any vote-to-pause to buy time. Alternatives:

1. **Host only, honour system** (built). Simple; relies on the host being fair.
2. **Anyone can pause, anyone can resume.** The victim of a stalling tactic can simply resume.
3. **Pause budget**: each player gets, say, 5 minutes of pause per game; the clock
   shows whose pause it is. Self-limiting, visible.
4. **Unanimous pause**: needs everyone present to agree. Cannot be abused by a majority,
   but one player can refuse a genuine bathroom break.
5. **Capped automatic pause**: a pause lasts at most N minutes, then the clock resumes by itself.
6. **Pause adds nothing**: pausing stops the clock but also locks every sheet, so nobody
   can use the time to negotiate or plan.

My suggestion when you decide: 2 or 3 combined with 5 (anyone can pause, it ends by
itself after a few minutes, and it is visible who paused).

## 22. Web client: host handover and every browser keeps the save

If the host's browser has not checked in for 45 seconds (`PARLEY_AWAY_SECONDS`), the host
role passes to the present player who joined earliest (seated players first). Every
browser keeps the whole archive in its local storage after each phase; if a server
loses the game (or it is opened on another server), the page offers "Resume it here".
The private order sheet lives only in the browser it was written in; a player switching
devices mid-phase starts that phase's sheet again on the new device.
