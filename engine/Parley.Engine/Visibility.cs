// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Fog of war: what one power can see.
    ///
    /// A power sees every province in which it has a unit and every province next to one
    /// of those (by land or by sea), every supply centre it owns and (unless switched
    /// off) the provinces next to those. A walled city is seen with the province around it.
    ///
    /// This is only the rule. It hides nothing by itself: a world or a server uses it to
    /// decide what to show a player. Whether that is real secrecy or an honour system
    /// depends on who holds the whole position; see docs/fog-of-war.md.
    ///
    /// Usage: Look(board, power), then ask Sees..., or read <see cref="seen"/>. To cover
    /// "before or after the turn", Look at the earlier board and AlsoLook at the later one.
    /// </summary>
    public class Visibility : UdonSharpBehaviour
    {
        public MapData map;
        /// <summary>Per province: can the power that was looked for see it?</summary>
        public bool[] seen;
        /// <summary>True (default): an owned centre sees its neighbours too, like a unit. False: only itself.</summary>
        public bool centresSeeNeighbours = true;

        public void Init(MapData m)
        {
            map = m;
            seen = new bool[m.provCount];
            centresSeeNeighbours = true;
        }

        /// <summary>Works out what the power sees on this board.</summary>
        public void Look(BoardState s, int power)
        {
            for (int p = 0; p < map.provCount; p++) seen[p] = false;
            AlsoLook(s, power);
        }

        /// <summary>Adds what the power sees on this board to what it already saw.</summary>
        public void AlsoLook(BoardState s, int power)
        {
            for (int u = 0; u < s.unitCount; u++)
                if (s.unitPower[u] == power) Around(Land(map.locProv[s.unitLoc[u]]), true);
            if (s.phase == BoardState.PHASE_RETREAT)
                for (int d = 0; d < s.disCount; d++)
                    if (s.disPower[d] == power) Around(Land(map.locProv[s.disLoc[d]]), true);
            for (int p = 0; p < map.provCount; p++)
                if (map.provSc[p] && s.owner[p] == power) Around(Land(p), centresSeeNeighbours);
            // a city is seen exactly when the province around it is
            for (int p = map.landCount; p < map.provCount; p++) seen[p] = seen[map.cityProv[p]];
        }

        private int Land(int prov) { return map.cityProv[prov] >= 0 ? map.cityProv[prov] : prov; }

        private void Around(int prov, bool neighbours)
        {
            seen[prov] = true;
            if (!neighbours) return;
            int st = map.anyStart[prov]; int c = map.anyCount[prov];
            for (int i = 0; i < c; i++) seen[map.anyAdj[st + i]] = true;
        }

        public bool SeesProvince(int prov) { return prov >= 0 && prov < map.provCount && seen[prov]; }

        public bool SeesPlace(int loc) { return loc >= 0 && loc < map.locCount && seen[map.locProv[loc]]; }

        /// <summary>Is unit u of this board in sight?</summary>
        public bool SeesUnit(BoardState s, int u) { return seen[map.locProv[s.unitLoc[u]]]; }

        /// <summary>
        /// Did the power witness line i of an announcement? Its own lines always; another
        /// power's order if the unit's province or the place it was heading for is in
        /// sight; notes addressed to everybody (power -1) always.
        /// </summary>
        public bool SeesLine(Announcement a, int i, int power)
        {
            if (i < 0 || i >= a.count) return false;
            if (a.power[i] == power || a.power[i] < 0) return true;
            if (a.kind[i] == Announcement.K_NOTE) return false; // another power's private business
            if (SeesPlace(a.fromLoc[i]) || SeesPlace(a.moveTo[i])) return true;
            int k = a.kind[i];
            if (k == Announcement.K_BUILD || k == Announcement.K_REMOVE || k == Announcement.K_AUTO_REMOVE) return SeesPlace(a.a[i]);
            return false;
        }

        /// <summary>How many provinces are in sight.</summary>
        public int Count()
        {
            int n = 0;
            for (int p = 0; p < map.provCount; p++) if (seen[p]) n++;
            return n;
        }
    }
}
