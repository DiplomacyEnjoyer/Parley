#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Cross-check whole games, every phase type, against the Python `diplomacy` package.

Reads the JSON lines written by `parley crosscheck-games` (whole bot games on the classic
board: every phase's orders and Parley's position afterwards). The other engine replays
each game from the start, and after every phase (movement, retreats, builds) the units,
dislodged units, supply centres and the next phase are compared. A game is followed until
its first disagreement.

The package is AGPL-3.0, used here as a separate program for testing only.

    parley crosscheck-games maps-local/standard.pmap 200 1 12 > games.jsonl
    python3 scripts/crosscheck_games.py games.jsonl
"""
import collections
import json
import sys

from diplomacy import Game


def norm_units(units, ours):
    return {p: sorted(u) for p, u in units.items()}


def main(path, show=10):
    games = phases = 0
    by_type = collections.Counter()
    same_by_type = collections.Counter()
    diffs, known = [], []
    stats = collections.Counter()
    for line in open(path):
        case = json.loads(line)
        games += 1
        game = Game(map_name='standard')
        for ph in case['phases']:
            label = ph['label']
            kind = label[-1]
            theirs_label = game.get_current_phase()
            if theirs_label != label:
                diffs.append((case['game'], label, 'phase', label, theirs_label, ph))
                break
            for power, orders in ph['orders'].items():
                game.set_orders(power, orders)
            game.process()
            phases += 1
            by_type[kind] += 1
            ours_u = {p: sorted(u) for p, u in ph['units'].items()}
            theirs_u = {p: sorted(game.get_units(p)) for p in ph['units']}
            # The other engine removes a dislodged unit at once when it has nowhere to retreat;
            # Parley lists it as dislodged until the retreat phase. Same outcome.
            for p in ours_u:
                extra = set(ours_u[p]) - set(theirs_u[p])
                if extra and all(u.startswith('*') for u in extra) and set(theirs_u[p]) <= set(ours_u[p]):
                    theirs_u[p] = ours_u[p]
            ours_c = {p: sorted(c) for p, c in ph['centres'].items()}
            theirs_c = {p: sorted(game.get_centers(p)) for p in ph['centres']}
            ok = ours_u == theirs_u and ours_c == theirs_c
            nxt = game.get_current_phase()
            # the end of a game is "W1913X" here and "COMPLETED" there
            if ph['next'].endswith('X') and nxt == 'COMPLETED':
                nxt = ph['next']
            if ok and nxt != ph['next'] and not (ph['next'].endswith('R') and not nxt.endswith('R')):
                ok = False
            if ok:
                same_by_type[kind] += 1
                continue
            entry = (case['game'], label, 'position', ours_u, theirs_u, ph, ours_c, theirs_c, ph['next'], nxt)
            # known rule differences: convoy to a neighbouring province (DATC 4.A.3), and which
            # unit goes when a power fails to disband (Parley follows the 2023 rule)
            convoy_adjacent = False
            for os_ in ph['orders'].values():
                for o in os_:
                    w = o.split()
                    if len(w) == 7 and w[2] == 'C' and game.map.abuts('A', w[4], '-', w[6]):
                        convoy_adjacent = True
            stats['known_' + kind] += 1 if ((kind == 'M' and convoy_adjacent) or (kind == 'A' and ph.get('forced'))) else 0
            if kind == 'A' and ph.get('forced'):
                stats['forced_in_even_game'] += case['game'] % 2 == 0
            if (kind == 'M' and convoy_adjacent) or (kind == 'A' and ph.get('forced')):
                known.append(entry)
            else:
                diffs.append(entry)
            break
    print(f'{games} games replayed, {phases} phases compared')
    for k, name in (('M', 'movement'), ('R', 'retreat'), ('A', 'build/disband')):
        print(f'  {name:14s}: {same_by_type[k]} of {by_type[k]} identical')
    print(f'{len(known)} games stopped at a known rule-edition difference (convoy next door; choice of unit in a forced disband)')
    print(f'{len(diffs)} games stopped at another difference')
    print('   known differences by phase type:', {k: v for k, v in stats.items() if k.startswith('known')},
          '; forced disbands in games where every disband was ordered:', stats['forced_in_even_game'])
    for d in diffs[:show]:
        print('--- game', d[0], 'phase', d[1], d[2])
        if d[2] == 'phase':
            print('   parley expects', d[3], 'other engine is at', d[4])
            continue
        ours_u, theirs_u, ph, ours_c, theirs_c = d[3], d[4], d[5], d[6], d[7]
        if d[8] != d[9]:
            print('   next phase: parley', d[8], ' other', d[9], ' centres:', {p: len(c) for p, c in ours_c.items() if c})
        for p, os_ in ph['orders'].items():
            if os_:
                print('  ', p, '; '.join(os_))
        for p in ours_u:
            if ours_u[p] != theirs_u[p]:
                print('   parley units:', p, ours_u[p]); print('   other units :', p, theirs_u[p])
            if ours_c[p] != theirs_c[p]:
                print('   parley centres:', p, ours_c[p]); print('   other centres :', p, theirs_c[p])
    return 0 if not diffs else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 10))
