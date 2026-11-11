#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Cross-check Parley's adjudicator against an independent one.

Reads the JSON lines written by `parley crosscheck-export` (random positions on the
classic reference board, a set of orders, and Parley's verdict), asks the open-source
Python `diplomacy` package to resolve the same orders, and reports every disagreement.

The Python package is AGPL-3.0. It is used here as a separate program for testing only;
none of it is part of Parley. Install it yourself:  pip install diplomacy

    parley crosscheck-export standard.pmap 5000 1 > cases.jsonl
    python3 scripts/crosscheck.py cases.jsonl
"""
import json
import sys

from diplomacy import Game


def main(path, show=15):
    total = same = 0
    diffs = []
    known = []
    for line in open(path):
        case = json.loads(line)
        game = Game(map_name='standard')
        game.clear_units()
        for power, units in case['units'].items():
            game.set_units(power, units, reset=True)
        for power, orders in case['orders'].items():
            game.set_orders(power, orders)
        before = {p: set(u) for p, u in case['units'].items()}
        game.process()
        total += 1
        theirs = {}
        for p in case['units']:
            power = game.get_power(p)
            units = [u for u in game.get_units(p) if not u.startswith('*')]
            gone = ['*' + u for u in power.retreats]
            theirs[p] = sorted(units + gone)
        ours = {p: sorted(u) for p, u in case['result'].items()}
        # The other engine removes a dislodged unit at once when it has nowhere to retreat.
        # Parley reports it as dislodged first. Treat those as the same outcome.
        for p in ours:
            missing = set(ours[p]) - set(theirs[p])
            if all(u.startswith('*') for u in missing) and set(theirs[p]) <= set(ours[p]):
                theirs[p] = ours[p]
        if theirs == ours:
            same += 1
            continue
        # Known rule difference: Parley follows DATC 3.0 issue 4.A.3 (an army sent to a
        # neighbouring province "via convoy", or whose own power orders a fleet to convoy it,
        # uses the convoy and does not fall back to walking if the convoy fails). The other
        # engine follows the older preference, where it walks instead.
        via_adjacent = False
        for power, orders in case['orders'].items():
            for o in orders:
                w = o.split()
                if w[-1] == 'VIA' and game.map.abuts('A', w[1], '-', w[3]):
                    via_adjacent = True
                if len(w) == 7 and w[2] == 'C' and game.map.abuts('A', w[4], '-', w[6]) \
                        and ('A %s - %s' % (w[4], w[6])) in orders:
                    via_adjacent = True
        (known if via_adjacent else diffs).append((case, ours, theirs))
    print(f'{same} of {total} situations resolved identically')
    print(f'{len(known)} differ in situations with a convoy to a neighbouring province (known rule-edition difference, DATC 4.A.3)')
    print(f'{len(diffs)} differ otherwise')
    for case, ours, theirs in diffs[:show]:
        print('--- case', case['id'])
        for p, orders in case['orders'].items():
            if orders:
                print('  ', p, '; '.join(orders))
        for p in ours:
            if ours[p] != theirs[p]:
                print('   parley :', p, ours[p])
                print('   other  :', p, theirs[p])
    return 0 if not diffs else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 15))
