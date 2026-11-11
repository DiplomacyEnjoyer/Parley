// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Everything that changes during a game: the date, where the units are, who owns
    /// each supply centre, and (during a retreat phase) which units were dislodged.
    /// Plain arrays only, so it is cheap to copy and to turn into a game code.
    /// </summary>
    public class BoardState : UdonSharpBehaviour
    {
        public const int SPRING = 0;
        public const int FALL = 1;
        public const int WINTER = 2;

        public const int PHASE_MOVE = 0;     // everybody orders their units
        public const int PHASE_RETREAT = 1;  // dislodged units retreat or disband
        public const int PHASE_ADJUST = 2;   // builds and disbands
        public const int PHASE_FINISHED = 3; // game over

        public int year;
        public int season;
        public int phase;

        public int unitCount;
        public int[] unitType;
        public int[] unitPower;
        public int[] unitLoc;

        /// <summary>Per province: power that owns the supply centre there, or -1.</summary>
        public int[] owner;

        // Dislodged units waiting to retreat (only meaningful in PHASE_RETREAT).
        public int disCount;
        public int[] disType;
        public int[] disPower;
        public int[] disLoc;
        /// <summary>Province the attacker came from (retreat there is forbidden), or -1.</summary>
        public int[] disFrom;
        /// <summary>Per province: true if a standoff left it empty this turn (no retreats into it).</summary>
        public bool[] contested;

        /// <summary>Winning power once the game is over, -1 for none / draw.</summary>
        public int winner;
        /// <summary>True when the game ended by agreed draw.</summary>
        public bool drawn;

        /// <summary>Per province: index of the unit standing there, or -1. Rebuilt by <see cref="Reindex"/>.</summary>
        public int[] provUnit;

        private int provCount;

        public void Init(MapData map)
        {
            provCount = map.provCount;
            int cap = map.provCount;
            unitType = new int[cap]; unitPower = new int[cap]; unitLoc = new int[cap];
            disType = new int[cap]; disPower = new int[cap]; disLoc = new int[cap]; disFrom = new int[cap];
            owner = new int[cap]; contested = new bool[cap]; provUnit = new int[cap];
            Clear();
        }

        public void Clear()
        {
            unitCount = 0; disCount = 0; year = 0; season = SPRING; phase = PHASE_MOVE; winner = -1; drawn = false;
            for (int p = 0; p < provCount; p++) { owner[p] = -1; contested[p] = false; provUnit[p] = -1; }
        }

        /// <summary>Sets up the map's starting position.</summary>
        public void SetupStart(MapData map)
        {
            Clear();
            year = map.startYear;
            for (int p = 0; p < map.provCount; p++) owner[p] = map.startOwner[p];
            for (int u = 0; u < map.startUnitCount; u++)
                AddUnit(map, map.startUnitType[u], map.startUnitPower[u], map.startUnitLoc[u]);
        }

        public void CopyFrom(BoardState o)
        {
            year = o.year; season = o.season; phase = o.phase; winner = o.winner; drawn = o.drawn;
            unitCount = o.unitCount; disCount = o.disCount;
            for (int i = 0; i < unitCount; i++) { unitType[i] = o.unitType[i]; unitPower[i] = o.unitPower[i]; unitLoc[i] = o.unitLoc[i]; }
            for (int i = 0; i < disCount; i++) { disType[i] = o.disType[i]; disPower[i] = o.disPower[i]; disLoc[i] = o.disLoc[i]; disFrom[i] = o.disFrom[i]; }
            for (int p = 0; p < provCount; p++) { owner[p] = o.owner[p]; contested[p] = o.contested[p]; provUnit[p] = o.provUnit[p]; }
        }

        /// <summary>Adds a unit. Returns its index, or -1 if the province is occupied or the place is invalid.</summary>
        public int AddUnit(MapData map, int type, int power, int loc)
        {
            if (!map.CanStand(type, loc)) return -1;
            int p = map.locProv[loc];
            if (provUnit[p] >= 0) return -1;
            int u = unitCount;
            unitType[u] = type; unitPower[u] = power; unitLoc[u] = loc;
            provUnit[p] = u;
            unitCount++;
            return u;
        }

        /// <summary>Removes a unit (the last unit takes its index).</summary>
        public void RemoveUnit(MapData map, int u)
        {
            provUnit[map.locProv[unitLoc[u]]] = -1;
            int last = unitCount - 1;
            if (u != last)
            {
                unitType[u] = unitType[last]; unitPower[u] = unitPower[last]; unitLoc[u] = unitLoc[last];
                provUnit[map.locProv[unitLoc[u]]] = u;
            }
            unitCount = last;
        }

        public void Reindex(MapData map)
        {
            for (int p = 0; p < provCount; p++) provUnit[p] = -1;
            for (int u = 0; u < unitCount; u++) provUnit[map.locProv[unitLoc[u]]] = u;
        }

        public int CountUnits(int power)
        {
            int c = 0;
            for (int u = 0; u < unitCount; u++) if (unitPower[u] == power) c++;
            return c;
        }

        public int CountCenters(int power)
        {
            int c = 0;
            for (int p = 0; p < provCount; p++) if (owner[p] == power) c++;
            return c;
        }

        /// <summary>A power is eliminated when it has no units and no centres.</summary>
        public bool IsEliminated(int power)
        {
            return CountUnits(power) == 0 && CountCenters(power) == 0 && CountDislodged(power) == 0;
        }

        public int CountDislodged(int power)
        {
            if (phase != PHASE_RETREAT) return 0;
            int c = 0;
            for (int i = 0; i < disCount; i++) if (disPower[i] == power) c++;
            return c;
        }

        /// <summary>Short label such as "S1901M" (Spring 1901 Movement).</summary>
        public string PhaseLabel()
        {
            string s = season == SPRING ? "S" : (season == FALL ? "F" : "W");
            string t = phase == PHASE_MOVE ? "M" : (phase == PHASE_RETREAT ? "R" : (phase == PHASE_ADJUST ? "A" : "X"));
            return s + year + t;
        }
    }
}
