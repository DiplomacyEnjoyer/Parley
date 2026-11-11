// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Answers "what can I tap next?" for the order panel, so that a player can only
    /// ever build orders that make sense on the current board.
    ///
    /// Typical tap sequences:
    ///   unit -> Hold
    ///   unit -> Move    -> destination            (ListMoves)
    ///   unit -> Support -> which unit             (ListSupportable)
    ///                   -> hold, or where it goes (ListSupportTargets)
    ///   fleet -> Convoy -> which army             (ListConvoyArmies)
    ///                   -> where to               (ListConvoyDestinations)
    ///   dislodged unit -> retreat destination or disband   (ListRetreats)
    ///   build phase    -> build army/fleet at a site, or disband a unit (ListBuilds / ListDisbands)
    ///
    /// Each List method fills optA / optB (first optCount entries) and returns optCount.
    /// </summary>
    public class OptionGenerator : UdonSharpBehaviour
    {
        public MapData map;
        public Adjudicator adj;

        public int optCount;
        public int[] optA;
        public int[] optB;

        private bool[] reachable; // per province: an army could be convoyed there
        private int[] queue;
        private int[] found;
        private bool[] seen;

        public void Init(MapData m, Adjudicator a)
        {
            map = m; adj = a;
            int cap = m.locCount * 2 + 8;
            optA = new int[cap]; optB = new int[cap];
            reachable = new bool[m.provCount]; queue = new int[m.provCount]; found = new int[m.provCount]; seen = new bool[m.provCount];
        }

        private void Add(int a, int b)
        {
            if (optCount >= optA.Length) return;
            optA[optCount] = a; optB[optCount] = b; optCount++;
        }

        // =====================================================================
        // Movement phase
        // =====================================================================

        /// <summary>
        /// Where can unit u move? optA = destination loc; optB = 0 for an ordinary move,
        /// 1 for a move by convoy. A destination an army can reach both ways is listed twice.
        /// </summary>
        public int ListMoves(BoardState s, int u)
        {
            optCount = 0;
            int loc = s.unitLoc[u];
            if (s.unitType[u] == MapData.FLEET)
            {
                int st = map.fleetStart[loc]; int c = map.fleetCount[loc];
                for (int i = 0; i < c; i++) Add(map.fleetAdj[st + i], 0);
                return optCount;
            }
            int sp = map.locProv[loc];
            int ast = map.armyStart[sp]; int ac = map.armyCount[sp];
            for (int i = 0; i < ac; i++) Add(map.armyAdj[ast + i], 0);
            MarkConvoyReach(s, sp, -1);
            for (int p = 0; p < map.provCount; p++) if (reachable[p]) Add(p, 1);
            return optCount;
        }

        /// <summary>
        /// Marks every coastal province an army in src could be convoyed to over the fleets
        /// now at sea (ignoring the fleet in skipProv, if any).
        /// </summary>
        private void MarkConvoyReach(BoardState s, int src, int skipProv)
        {
            for (int p = 0; p < map.provCount; p++) { reachable[p] = false; seen[p] = false; }
            if (map.provType[src] != MapData.COAST) return;
            int qh = 0; int qt = 0;
            int st = map.seaStart[src]; int c = map.seaCount[src];
            for (int i = 0; i < c; i++)
            {
                int p = map.seaAdj[st + i];
                if (p != skipProv && !seen[p] && FleetAtSea(s, p)) { seen[p] = true; queue[qt] = p; qt++; }
            }
            while (qh < qt)
            {
                int p = queue[qh]; qh++;
                st = map.seaStart[p]; c = map.seaCount[p];
                for (int i = 0; i < c; i++)
                {
                    int q = map.seaAdj[st + i];
                    if (map.provType[q] == MapData.COAST) { if (q != src) reachable[q] = true; }
                    else if (q != skipProv && !seen[q] && FleetAtSea(s, q)) { seen[q] = true; queue[qt] = q; qt++; }
                }
            }
        }

        private bool FleetAtSea(BoardState s, int prov)
        {
            if (map.provType[prov] != MapData.SEA) return false;
            int u = s.provUnit[prov];
            return u >= 0 && s.unitType[u] == MapData.FLEET;
        }

        private bool CanReach(BoardState s, int u, int prov)
        {
            if (s.unitType[u] == MapData.ARMY) return map.provType[prov] != MapData.SEA && map.ArmyAdjacent(map.locProv[s.unitLoc[u]], prov);
            return map.FleetCanReachProv(s.unitLoc[u], prov);
        }

        /// <summary>Which units could unit u support? optA = province of that unit.</summary>
        public int ListSupportable(BoardState s, int u)
        {
            int n = 0;
            for (int t = 0; t < s.unitCount; t++)
            {
                if (t == u) continue;
                if (ListSupportTargets(s, u, map.locProv[s.unitLoc[t]]) > 0) { found[n] = map.locProv[s.unitLoc[t]]; n++; }
            }
            optCount = 0;
            for (int i = 0; i < n; i++) Add(found[i], 0);
            return optCount;
        }

        /// <summary>
        /// What can unit u support the unit in targetProv to do?
        /// optA = -1 means "hold where it is"; otherwise optA = province it moves to.
        /// </summary>
        public int ListSupportTargets(BoardState s, int u, int targetProv)
        {
            optCount = 0;
            int t = s.provUnit[targetProv];
            int sp = map.locProv[s.unitLoc[u]];
            if (t < 0 || t == u) return 0;
            if (CanReach(s, u, targetProv)) Add(-1, 0);
            if (s.unitType[t] == MapData.FLEET)
            {
                int loc = s.unitLoc[t];
                int st = map.fleetStart[loc]; int c = map.fleetCount[loc];
                for (int i = 0; i < c; i++)
                {
                    int d = map.locProv[map.fleetAdj[st + i]];
                    if (d == sp || !CanReach(s, u, d) || Listed(d)) continue;
                    Add(d, 0);
                }
                return optCount;
            }
            int ast = map.armyStart[targetProv]; int ac = map.armyCount[targetProv];
            for (int i = 0; i < ac; i++)
            {
                int d = map.armyAdj[ast + i];
                if (d == sp || !CanReach(s, u, d)) continue;
                Add(d, 0);
            }
            // convoyed moves: a fleet cannot support a move that would have to be convoyed by itself
            int skip = (s.unitType[u] == MapData.FLEET && map.provType[sp] == MapData.SEA) ? sp : -1;
            MarkConvoyReach(s, targetProv, skip);
            for (int d = 0; d < map.provCount; d++)
            {
                if (!reachable[d] || d == sp || !CanReach(s, u, d) || Listed(d)) continue;
                Add(d, 0);
            }
            return optCount;
        }

        private bool Listed(int a)
        {
            for (int i = 0; i < optCount; i++) if (optA[i] == a) return true;
            return false;
        }

        /// <summary>Which armies could fleet u convoy? optA = province of the army.</summary>
        public int ListConvoyArmies(BoardState s, int u)
        {
            int n = 0;
            if (s.unitType[u] == MapData.FLEET && map.provType[map.locProv[s.unitLoc[u]]] == MapData.SEA)
            {
                for (int a = 0; a < s.unitCount; a++)
                {
                    if (s.unitType[a] != MapData.ARMY) continue;
                    int ap = map.locProv[s.unitLoc[a]];
                    if (map.provType[ap] != MapData.COAST) continue;
                    if (ListConvoyDestinations(s, u, ap) > 0) { found[n] = ap; n++; }
                }
            }
            optCount = 0;
            for (int i = 0; i < n; i++) Add(found[i], 0);
            return optCount;
        }

        /// <summary>Where could fleet u help carry the army in armyProv? optA = destination province.</summary>
        public int ListConvoyDestinations(BoardState s, int u, int armyProv)
        {
            optCount = 0;
            if (s.unitType[u] != MapData.FLEET) return 0;
            if (map.provType[map.locProv[s.unitLoc[u]]] != MapData.SEA) return 0;
            int a = s.provUnit[armyProv];
            if (a < 0 || s.unitType[a] != MapData.ARMY) return 0;
            MarkConvoyReach(s, armyProv, -1);
            for (int d = 0; d < map.provCount; d++)
            {
                if (!reachable[d]) continue;
                // MarkConvoyReach's scratch arrays are not used by ConvoyOrderLegal, so this is safe
                if (adj.ConvoyOrderLegal(s, u, armyProv, d)) Add(d, 0);
            }
            return optCount;
        }

        /// <summary>True when the unit has anything it could support / convoy (to decide which buttons to show).</summary>
        public bool CanSupportAnything(BoardState s, int u) { return ListSupportable(s, u) > 0; }
        public bool CanConvoyAnything(BoardState s, int u) { return ListConvoyArmies(s, u) > 0; }

        // =====================================================================
        // Retreat phase
        // =====================================================================

        /// <summary>Where may dislodged unit d retreat? optA = loc. (Disbanding is always allowed too.)</summary>
        public int ListRetreats(BoardState s, int d)
        {
            optCount = 0;
            for (int l = 0; l < map.locCount; l++)
                if (adj.RetreatValid(s, d, l)) Add(l, 0);
            return optCount;
        }

        // =====================================================================
        // Adjustment phase
        // =====================================================================

        /// <summary>
        /// What may this power build? optA = loc, optB = unit type. Empty when it has no builds.
        /// (It may always build fewer than allowed.)
        /// </summary>
        public int ListBuilds(BoardState s, int power)
        {
            optCount = 0;
            if (adj.AdjustDelta(s, power) <= 0) return 0;
            for (int l = 0; l < map.locCount; l++)
            {
                if (!adj.IsBuildSite(s, power, map.locProv[l])) continue;
                if (map.CanStand(MapData.ARMY, l)) Add(l, MapData.ARMY);
                if (map.CanStand(MapData.FLEET, l) && map.fleetCount[l] > 0) Add(l, MapData.FLEET);
            }
            return optCount;
        }

        /// <summary>Which of its units may this power disband? optA = loc. Empty unless it must disband.</summary>
        public int ListDisbands(BoardState s, int power)
        {
            optCount = 0;
            if (adj.AdjustDelta(s, power) >= 0) return 0;
            for (int u = 0; u < s.unitCount; u++)
                if (s.unitPower[u] == power) Add(s.unitLoc[u], s.unitType[u]);
            return optCount;
        }
    }
}
