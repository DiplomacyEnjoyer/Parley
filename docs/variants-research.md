# Community variants: research and shortlist

*Second session. What is most played and liked, where it is hosted, whether it can be
downloaded with art, and its licence status. Then which ones Parley imports.*

## How this was researched, and its limits

- **Checked directly from this machine:** the Python `diplomacy` package (PyPI), godip
  (through the Go module proxy), and jDip 1.6.8 (SourceForge). Their files were downloaded,
  listed, converted and played with bots.
- **Could not be reached from this machine:** webDiplomacy, vDiplomacy, github.com and
  diplom.org pages directly. Facts about those come from web-search summaries and from
  general knowledge, and are marked as such. No current play-count ranking was found;
  popularity below is from community articles and the sites' own variant lists, not from
  numbers.

## Where variants live

| Project | Variants | Format | Art | Licence | Reachable here |
|---|---|---|---|---|---|
| **webDiplomacy** | Classic plus ~10 (World, Ancient Mediterranean, Modern II, Known World 901, Fall of the American Empire, 1v1 maps...) | PHP install scripts and database rows; map images are PNGs with coordinates | Yes (bitmaps) | Site code AGPL-3.0; variant maps from their authors, mostly no stated licence | No |
| **vDiplomacy** | 90 to 100+ (the largest library) | Same as webDiplomacy (it is a fork) | Yes (bitmaps) | Same | No |
| **godip** (Diplicity app) | 24: Classical, Ancient Mediterranean, Canton, Chaos, Classical Crowded, Cold War, Empires and Coalitions, Europe 1939, Fleet Rome, France-Austria, Hundred, Italy-Germany, North Sea Wars, Pure, Sengoku, 2020, Unconstitutional, Vietnam War, Western World 901, Year 1908, Youngstown Redux and others | Go source code (map graph built in code) | **Yes, SVG** per variant, with flags | **GPL-3.0** (checked) | Yes, via proxy.golang.org |
| **jDip** | 14 packs, ~25 variants: Standard and its family (1898, Crowded, Fleet Rome, Shift-Left/Right/Around, 5- and 6-player), Hundred, Youngstown, Chaos, Sail Ho!, Milan, Asia, Imperium, Loeb9, Octarine, Root Z, Modern, Pure | XML (adjacency + variant definitions) | **Yes, SVG** (jDip-style: clickable provinces, unit spots) | **GPL** (jDip itself; variant maps from their authors) | Yes, SourceForge |
| **Python `diplomacy`** | 14: Standard, Modern, Ancient Mediterranean, Colonial, World, Pure, and small/test maps | DPjudge `.map` text | SVG for 4 (Standard, Modern, Ancient Med, Pure) | Package **AGPL-3.0**; maps from DPjudge authors; SVGs are jDip's (GPL) | Yes, PyPI |
| **DPjudge** | Hundreds (the historical judge library, including **1900**) | DPjudge `.map` text | Some (PostScript/bitmaps) | Varies; mostly unstated | No (GitHub) |

## What is most played and most liked

1. **Classic (Standard).** By far the most played everywhere. Done: the default map.
2. **1900** (Baron Powell). The most acclaimed classic-map improvement: same rules,
   redrawn map to fix balance (France and Russia strong, Austria and Italy weak in
   classic), one special rule ("Russian Steamroller"), starts 1900, still 18 centres to
   win. Described as one of the most popular variants on the judges and played in postal
   and tournament play; webDiplomacy forum users call it "tried-and-true and critically
   acclaimed". Powell himself later wrote that he cannot claim 1900 is clearly better
   balanced than classic (Germany may be a little strong). *Sources: Diplomatic Pouch
   articles, webDiplomacy forum (via search).* **Not available from any source reachable
   here**: it is on DPjudge (`.map`, which Parley already imports), vDiplomacy (PHP), and
   as a PDF map on diplom.org. No open licence found.
3. **Ancient Mediterranean.** Five powers, widely hosted (webDiplomacy, vDiplomacy, godip,
   Python package). Done: installed with picture.
4. **Modern / Modern II.** Ten powers on a modern Europe. Installed with picture.
5. **World / Known World 901 / Western World 901.** Large maps (World has 17 powers).
   World and Colonial import from the Python package (no picture); Western World 901 is in godip.
6. **Hundred.** Three players; the usual answer to "only three of us tonight". Installed
   (jDip) with picture. Also in godip.
7. **Youngstown (Redux).** Ten powers, Europe plus Asia. jDip's original installed with
   picture; the Redux version is in godip.
8. **Chaos.** 34 players, each with one centre; party-size. jDip and godip. Starts with a
   build phase, which Parley maps cannot express yet.
9. **Small-group maps:** Cold War (2 players, godip), Sail Ho! (4, installed), Pure (7
   provinces, installed), France-Austria and Italy-Germany (2, godip).
10. **Fog of War and Gunboat** are popular *rule modes* (hidden map, no talking), not maps.

## Recommended shortlist for Parley

Chosen for: popularity, player counts that suit a VR group (2 to 10 people), art
available, and no special rules Parley cannot play.

| Priority | Variant | Players | Source | Status |
|---|---|---|---|---|
| 1 | Classic | 7 | Python package (data + jDip SVG) | **Installed, default** |
| 2 | 1900 | 7 | DPjudge `.map` (importer exists) | **Blocked here:** source not reachable; special "Russian Steamroller" rule not supported. Next session: fetch DPjudge's 1900 map from GitHub at setup and check the importer's warnings. |
| 3 | Hundred | 3 | jDip | **Installed** with picture |
| 4 | Ancient Mediterranean | 5 | Python package | **Installed** with picture |
| 5 | Modern | 10 | Python package | **Installed** with picture |
| 6 | Youngstown | 10 | jDip | **Installed** with picture |
| 7 | Sail Ho! | 4 | jDip | **Installed** with picture |
| 8 | Cold War | 2 | godip | Not yet: needs the godip exporter (below) |
| 9 | Western World 901 | 7 | godip | Not yet: godip exporter |
| 10 | Chaos | 34 | jDip / godip | Not yet: needs "start with a build phase" |

Also installed because they cost nothing once the importer existed: 1898, Fleet Rome,
Milan, Asia, Loeb9, Root Z (jDip, all with pictures; all play through bot games).
Not installed: Imperium (no home centres; needs special build rules), Octarine (data
error in the pack: a fleet starts inland), Chaos (build-phase start).

## Importers

| Format | Covers | State |
|---|---|---|
| DPjudge `.map` | Python package's 14 maps; DPjudge library including 1900 | Done (first session) |
| jDip XML + SVG | jDip's packs | **Done.** jDip's Standard converts to exactly the same board as the DPjudge Standard, link for link (test `JdipStandardIsTheSameBoardAsDpjudgeStandard`) |
| jDip-style SVG pictures | Python package and jDip | **Done** (`parley import-svg`) |
| godip | 24 variants with SVG | Not yet. Plan: a small Go program run at setup that loads godip and writes each variant's graph, centres, units and SVG as JSON; then a C# converter. godip's SVGs are a different layout from jDip's, so the picture matcher needs a second mode. |
| webDiplomacy / vDiplomacy | 100+ variants | Not planned: maps are PHP code plus bitmaps; sources unreachable here |

## Licence summary

None of these variants carries an open-content licence for the map itself. They are
distributed inside GPL/AGPL software and have been shared freely by their authors for
decades. Parley fetches them at setup and never commits them (THIRD_PARTY.md). Using them
in a published VRChat world needs a per-variant check: ask the author where one can be
found (Powell for 1900, for example), or redraw the art over the adjacency data.
