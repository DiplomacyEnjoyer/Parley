// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Simple computer players used only for automated testing and map-balance statistics.
/// They are not opponents for people (the game has no AI players); they exist to push
/// thousands of varied, legal orders through the engine and to play games to the end.
///
/// "random": every unit picks any legal order at random - maximum variety, finds crashes.
/// "basic":  greedy and a bit coordinated (heads for centres it does not own, supports its
///           own attacks, sometimes convoys) - games actually finish, so win rates mean something.
/// </summary>
public sealed class Bot
{
    private readonly Game g;
    private readonly Random rng;
    private readonly bool basic;
    private readonly BoardState shadow; // private copy so orders can be built with the engine's own setters
    private readonly Adjudicator pad;   // scratch order pad

    public Bot(Game game, Random random, bool basicStrategy)
    {
        g = game; rng = random; basic = basicStrategy;
        shadow = new BoardState(); shadow.Init(g.Map);
        pad = new Adjudicator(); pad.Init(g.Map);
    }

    /// <summary>Chooses this power's orders for the current phase, as GameCodec order text.</summary>
    public string Choose(int power)
    {
        var s = g.State; var map = g.Map; var opt = g.Options;
        pad.ClearOrders();
        if (s.phase == BoardState.PHASE_MOVE) ChooseMoves(power);
        else if (s.phase == BoardState.PHASE_RETREAT)
        {
            for (int d = 0; d < s.disCount; d++)
            {
                if (s.disPower[d] != power) continue;
                int n = opt.ListRetreats(s, d);
                if (n == 0 || rng.Next(12) == 0) continue; // sometimes disband by choice
                int best = opt.optA[rng.Next(n)];
                if (basic)
                    for (int i = 0; i < n; i++)
                    {
                        int p = map.locProv[opt.optA[i]];
                        if (map.provSc[p] && s.owner[p] != power) best = opt.optA[i];
                    }
                pad.SetRetreat(d, best);
            }
        }
        else if (s.phase == BoardState.PHASE_ADJUST)
        {
            int delta = g.Adj.AdjustDelta(s, power);
            if (delta > 0)
            {
                int n = opt.ListBuilds(s, power);
                var sites = Enumerable.Range(0, n).Select(i => (loc: opt.optA[i], type: opt.optB[i])).OrderBy(_ => rng.Next()).ToList();
                var used = new HashSet<int>();
                foreach (var (loc, type) in sites)
                {
                    if (delta == 0) break;
                    if (!used.Add(map.locProv[loc])) continue;
                    if (rng.Next(15) == 0) { pad.AddAdjust(power, Adjudicator.ADJ_WAIVE, -1); delta--; continue; }
                    pad.AddAdjust(power, type == MapData.ARMY ? Adjudicator.ADJ_BUILD_ARMY : Adjudicator.ADJ_BUILD_FLEET, loc);
                    delta--;
                }
            }
            else if (delta < 0 && rng.Next(4) != 0) // one time in four: forget, and let the forced-disband rule decide
            {
                int n = opt.ListDisbands(s, power);
                var locs = Enumerable.Range(0, n).Select(i => opt.optA[i]).OrderBy(_ => rng.Next()).Take(-delta).ToList();
                foreach (var loc in locs) pad.AddAdjust(power, Adjudicator.ADJ_DISBAND, loc);
            }
        }
        return g.Codec.EncodeOrders(s, pad, power);
    }

    private void ChooseMoves(int power)
    {
        var s = g.State; var map = g.Map; var opt = g.Options;
        var mine = Enumerable.Range(0, s.unitCount).Where(u => s.unitPower[u] == power).OrderBy(_ => rng.Next()).ToList();
        var claimed = new Dictionary<int, int>(); // destination province -> my unit moving there
        var done = new HashSet<int>();

        if (basic)
        {
            // an occasional convoy: fleet at sea carries a neighbouring army of mine
            foreach (int f in mine)
            {
                if (done.Contains(f) || s.unitType[f] != MapData.FLEET || rng.Next(5) != 0) continue;
                int na = opt.ListConvoyArmies(s, f);
                var armies = Enumerable.Range(0, na).Select(i => opt.optA[i]).Where(p => s.unitPower[s.provUnit[p]] == power && !done.Contains(s.provUnit[p])).ToList();
                if (armies.Count == 0) continue;
                int ap = armies[rng.Next(armies.Count)];
                int nd = opt.ListConvoyDestinations(s, f, ap);
                var dests = Enumerable.Range(0, nd).Select(i => opt.optA[i]).Where(d => !claimed.ContainsKey(d) && !(s.provUnit[d] >= 0 && s.unitPower[s.provUnit[d]] == power)).ToList();
                if (dests.Count == 0) continue;
                int dest = dests.OrderByDescending(d => (map.provSc[d] && s.owner[d] != power ? 10 : 0) + rng.Next(5)).First();
                int a = s.provUnit[ap];
                pad.SetConvoy(f, ap, dest); pad.SetMove(a, dest, true);
                done.Add(f); done.Add(a); claimed[dest] = a;
            }
        }

        foreach (int u in mine)
        {
            if (done.Contains(u)) continue;
            if (!basic) { RandomOrder(u); continue; }

            // support an attack one of my units is already making, if I can reach the target
            if (rng.Next(3) != 0)
            {
                var helpable = claimed.Where(kv => kv.Value != u && CanReach(u, kv.Key) && !pad.oVia[kv.Value]
                                                   && s.provUnit[kv.Key] >= 0 && s.unitPower[s.provUnit[kv.Key]] != power).ToList();
                if (helpable.Count > 0)
                {
                    var (dest, mover) = helpable[rng.Next(helpable.Count)];
                    pad.SetSupportMove(u, map.locProv[s.unitLoc[mover]], dest);
                    done.Add(u);
                    continue;
                }
            }
            int n = opt.ListMoves(s, u);
            int bestLoc = -1; double bestScore = 0.5 + rng.NextDouble(); // staying put has some value
            int here = map.locProv[s.unitLoc[u]];
            if (map.provSc[here] && s.owner[here] != power && s.season == BoardState.FALL) bestScore += 6; // sit on a centre to capture it
            for (int i = 0; i < n; i++)
            {
                if (opt.optB[i] != 0) continue; // plain moves only; convoys are handled above
                int loc = opt.optA[i]; int p = map.locProv[loc];
                if (claimed.ContainsKey(p)) continue;
                int occ = s.provUnit[p];
                if (occ >= 0 && s.unitPower[occ] == power && !done.Contains(occ)) continue; // don't bump into my own waiting unit
                double score = rng.NextDouble() * 2;
                if (map.provSc[p] && s.owner[p] != power) score += s.owner[p] < 0 ? 5 : 4;
                if (occ >= 0 && s.unitPower[occ] != power) score -= 1.0;
                score += 0.4 * NearbyTargets(p, power);
                if (score > bestScore) { bestScore = score; bestLoc = loc; }
            }
            if (bestLoc >= 0) { pad.SetMove(u, bestLoc, false); claimed[map.locProv[bestLoc]] = u; }
            else
            {
                // hold; maybe support a neighbour of mine in place
                int ns = opt.ListSupportable(s, u);
                var friends = Enumerable.Range(0, ns).Select(i => opt.optA[i]).Where(p => s.unitPower[s.provUnit[p]] == power && pad.oType[s.provUnit[p]] != Adjudicator.MOVE && CanReach(u, p)).ToList();
                if (friends.Count > 0 && rng.Next(2) == 0) pad.SetSupportHold(u, friends[rng.Next(friends.Count)]);
            }
            done.Add(u);
        }
    }

    private bool CanReach(int u, int prov)
    {
        var s = g.State; var map = g.Map;
        if (s.unitType[u] == MapData.ARMY) return map.provType[prov] != MapData.SEA && map.ArmyAdjacent(map.locProv[s.unitLoc[u]], prov);
        return map.FleetCanReachProv(s.unitLoc[u], prov);
    }

    // how many centres I do not own are next to this province (a cheap sense of direction)
    private int NearbyTargets(int prov, int power)
    {
        var map = g.Map; int c = 0;
        for (int i = 0; i < map.anyCount[prov]; i++)
        {
            int q = map.anyAdj[map.anyStart[prov] + i];
            if (map.provSc[q] && g.State.owner[q] != power) c++;
        }
        return c;
    }

    /// <summary>Any legal order, uniformly-ish at random (including pointless ones).</summary>
    private void RandomOrder(int u)
    {
        var s = g.State; var opt = g.Options;
        int kind = rng.Next(10);
        if (kind == 0) return; // hold
        if (kind <= 5)
        {
            int n = opt.ListMoves(s, u);
            if (n == 0) return;
            int i = rng.Next(n);
            pad.SetMove(u, opt.optA[i], opt.optB[i] == 1);
        }
        else if (kind <= 8)
        {
            int n = opt.ListSupportable(s, u);
            if (n == 0) return;
            int tp = opt.optA[rng.Next(n)];
            int m = opt.ListSupportTargets(s, u, tp);
            if (m == 0) return;
            int d = opt.optA[rng.Next(m)];
            if (d < 0) pad.SetSupportHold(u, tp); else pad.SetSupportMove(u, tp, d);
        }
        else
        {
            int n = opt.ListConvoyArmies(s, u);
            if (n == 0) return;
            int ap = opt.optA[rng.Next(n)];
            int m = opt.ListConvoyDestinations(s, u, ap);
            if (m == 0) return;
            pad.SetConvoy(u, ap, opt.optA[rng.Next(m)]);
        }
    }
}
