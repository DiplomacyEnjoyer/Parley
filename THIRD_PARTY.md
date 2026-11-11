# Third-party material

Parley's own code is MPL 2.0 and its own maps and art are CC BY-SA 4.0. Everything
listed here belongs to other people. **None of it is committed to this repository.**
`scripts/fetch-reference-maps.sh` downloads it at setup time into the git-ignored
folders `third_party/` and `maps-local/`.

The project owner has accepted the low risk of using fan-distributed maps for now
(DECISIONS.md §2). Before publishing a VRChat world, re-check every row.

| What | Where it comes from | Used for | Licence status |
|---|---|---|---|
| DPjudge-format `.map` files (classic board and 13 variants: Modern, Ancient Mediterranean, Colonial, World, Pure, ...) | Python package `diplomacy` 1.1.2 on PyPI (`maps/*.map`) | Rule tests (DATC), the importer, the web client's classic map | Distributed inside an **AGPL-3.0** package. Most of the map files were written by hobbyists for the DPjudge project and carry no licence of their own. The classic board's layout is the published game's design; its owner has tolerated fan copies for decades, but has not licensed them. |
| Board pictures `standard.svg`, `modern.svg`, `ancmed.svg`, `pure.svg` | Same package (`maps/svg/`) | The web client draws the real board from them | The file header says "Copyright jDip - GPL License". jDip is GPL-2.0 or later. The classic board's background bitmap credit is J. Fatula III, the SVG by Zach DelProposto. Redistributed by the AGPL Python package. |
| jDip variant packs (Hundred, Youngstown, Sail Ho!, 1898, Fleet Rome, Milan, Asia, Loeb9, Root Z, and others) | jDip 1.6.8 on SourceForge (`jdip-1.6.8-bin-noinstaller.zip`, folder `variants/`) | Converted with `parley import-jdip` into `maps-local/` with their pictures | jDip is **GPL**. The variant maps were made by their individual authors and contributed to jDip; no separate licence is stated. |
| godip (consulted, not yet used) | Go module proxy (`github.com/zond/godip`) | Research only so far | **GPL-3.0**. |
| DATC test cases | Lucas B. Kruijswijk, *Diplomacy Adjudicator Test Cases* v3.0 | `tests/datc/` (**committed**; the DATC's own terms allow copying the chapter 6 cases) | Free to copy per the DATC. |
| UdonHashLib (not used, only consulted) | github.com/Gorialis/vrchat-udon-hashlib | Confirmed that hashing in Udon has to be written by hand | MIT. Parley's SHA-256 (`OrderSeal.cs`) was written independently. |

## What this means in practice

- **Playing at home or with friends** with the fetched maps: fine; nothing is redistributed.
- **Publishing a VRChat world** that contains the classic board or its picture would
  mean redistributing GPL artwork and a layout nobody has licensed. Options at that point:
  ask the artists, draw an original picture over the (uncopyrightable) adjacency data,
  or have the world download the map at run time the way this script does.
- **Code from these projects is never copied into Parley.** GPL and AGPL code cannot be
  mixed into MPL files. Only data files are read, at run time, from the ignored folders.
- Converted files in `maps-local/` (`*.pmap`, `*.art.json`) are derived from the above
  and carry the same status.
