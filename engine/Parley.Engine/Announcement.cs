// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// What the announcer reads out when a phase ends: every order, country by country,
    /// in a fixed order, with where each piece is trying to go (known before any result)
    /// and what became of it (known after the referee has ruled).
    ///
    /// The world uses it in two steps. While an order is being read, its piece travels
    /// towards <see cref="moveTo"/>; two pieces ordered to the same place both head
    /// there. When the reading is over, the outcomes are shown: who got in, who bounced,
    /// who was dislodged, and where the fights were (<see cref="battleProv"/>).
    ///
    /// Usage: Capture (orders entered, phase not yet resolved), resolve, Finish.
    /// </summary>
    public class Announcement : UdonSharpBehaviour
    {
        // kinds of line (the first five match the Adjudicator's order types)
        public const int K_HOLD = 0;
        public const int K_MOVE = 1;          // a = destination loc
        public const int K_SUPPORT_HOLD = 2;  // a = province of the supported unit
        public const int K_SUPPORT_MOVE = 3;  // a = province of the moving unit, b = its destination loc
        public const int K_CONVOY = 4;        // a = province of the army, b = destination province
        public const int K_RETREAT = 5;       // a = destination loc
        public const int K_DISBAND = 6;       // a dislodged unit that is taken off the board
        public const int K_BUILD = 7;         // a = loc of the new unit
        public const int K_REMOVE = 8;        // a = loc of the unit removed
        public const int K_WAIVE = 9;         // a build not used
        public const int K_AUTO_REMOVE = 10;  // removed by the rules because no removal was ordered

        // what became of it
        public const int O_OK = 0;
        public const int O_BOUNCED = 1;
        public const int O_CUT = 2;
        public const int O_VOID = 3;       // impossible order: the unit simply held
        public const int O_NO_PATH = 4;    // convoy did not get through
        public const int O_DESTROYED = 5;  // retreat failed: unit lost
        public const int O_REFUSED = 6;    // build or removal not allowed

        // how a power's orders arrived
        public const int S_ORDERS = 0;     // handed in normally
        public const int S_NONE = 1;       // player was there but nothing arrived in time
        public const int S_UNOPENED = 2;   // sealed orders were published but never opened
        public const int S_NOBODY = 3;     // no player holds this power
        public const int S_AWAY = 4;       // the player had left

        // where the reading has got to (see Locate)
        public const int AT_LEAD = 0;      // end-of-round sound and a short pause
        public const int AT_POWER = 1;     // naming a country
        public const int AT_ORDER = 2;     // reading one order
        public const int AT_RESULT = 3;    // everything read: the board shows the outcome
        public const int AT_DONE = 4;

        public MapData map;

        /// <summary>Phase type that was announced (BoardState.PHASE_*), and its label such as "S1M".</summary>
        public int phase;
        public string label = "";

        public int count;
        public int[] power;
        public int[] unitType;
        /// <summary>Where the unit stood (-1 for builds and waives).</summary>
        public int[] fromLoc;
        public int[] kind;
        public int[] a;
        public int[] b;
        public bool[] via;
        /// <summary>For supports and convoys: type of the unit standing in province a, or -1 if none.</summary>
        public int[] otherType;
        /// <summary>Loc the piece heads for while its order is read, or -1 if it stays where it is.</summary>
        public int[] moveTo;
        public int[] outcome;
        /// <summary>True if the piece really ended up at moveTo.</summary>
        public bool[] moved;
        public bool[] dislodged;

        /// <summary>Reading order: seq[k] is the line read k-th. Lines of one country are together.</summary>
        public int[] seq;
        public int groupCount;
        public int[] groupPower;
        public int[] groupFirst;   // index into seq
        public int[] groupItems;
        /// <summary>Per power: S_* (how its orders arrived).</summary>
        public int[] powerStatus;

        // places where units clashed in a movement phase
        public int battleCount;
        public int[] battleProv;
        /// <summary>Units that tried to move in.</summary>
        public int[] battleMovers;
        /// <summary>Supports that counted, for the attacks and for the defender.</summary>
        public int[] battleSupports;
        /// <summary>True if a unit was standing there and did not leave.</summary>
        public bool[] battleDefended;
        /// <summary>Power that holds the province afterwards, or -1 if it was left empty (standoff).</summary>
        public int[] battleWinner;

        // result of Locate
        public int at;
        public int atPower;
        public int atLine;

        private int[] provType; // unit type per province before the phase (-1 none)
        private int[] provItem; // line of the unit standing in each province (-1 none)

        public void Init(MapData m)
        {
            map = m;
            int cap = m.provCount * 3 + m.powerCount + 16;
            power = new int[cap]; unitType = new int[cap]; fromLoc = new int[cap]; kind = new int[cap];
            a = new int[cap]; b = new int[cap]; via = new bool[cap]; otherType = new int[cap]; moveTo = new int[cap];
            outcome = new int[cap]; moved = new bool[cap]; dislodged = new bool[cap]; seq = new int[cap];
            groupPower = new int[m.powerCount]; groupFirst = new int[m.powerCount]; groupItems = new int[m.powerCount];
            powerStatus = new int[m.powerCount];
            battleProv = new int[m.provCount]; battleMovers = new int[m.provCount]; battleSupports = new int[m.provCount];
            battleDefended = new bool[m.provCount]; battleWinner = new int[m.provCount];
            provType = new int[m.provCount]; provItem = new int[m.provCount];
            Clear();
        }

        public void Clear()
        {
            count = 0; groupCount = 0; battleCount = 0; label = ""; phase = BoardState.PHASE_FINISHED;
            at = AT_DONE; atPower = -1; atLine = -1;
            for (int p = 0; p < map.powerCount; p++) powerStatus[p] = S_ORDERS;
        }

        private int AddLine(int pw, int type, int from, int k, int va, int vb, bool viaConvoy)
        {
            int i = count;
            if (i >= power.Length) return -1;
            power[i] = pw; unitType[i] = type; fromLoc[i] = from; kind[i] = k; a[i] = va; b[i] = vb; via[i] = viaConvoy;
            otherType[i] = -1; moveTo[i] = -1; outcome[i] = O_OK; moved[i] = false; dislodged[i] = false;
            count = i + 1;
            return i;
        }

        /// <summary>
        /// Reads the position and the orders entered into the referee, before the phase is
        /// resolved. status gives S_* per power (may be null: everyone handed in normally).
        /// </summary>
        public void Capture(BoardState s, Adjudicator adj, int[] status)
        {
            Clear();
            phase = s.phase; label = s.PhaseLabel();
            for (int p = 0; p < map.powerCount; p++) powerStatus[p] = status == null ? S_ORDERS : status[p];
            for (int p = 0; p < map.provCount; p++) { provType[p] = -1; provItem[p] = -1; }
            for (int u = 0; u < s.unitCount; u++) provType[map.locProv[s.unitLoc[u]]] = s.unitType[u];

            if (s.phase == BoardState.PHASE_MOVE)
            {
                // line i is unit i, which is how the results are looked up afterwards
                for (int u = 0; u < s.unitCount; u++)
                {
                    int k = adj.oType[u];
                    int i = AddLine(s.unitPower[u], s.unitType[u], s.unitLoc[u], k, adj.oA[u], adj.oB[u], adj.oVia[u]);
                    if (i < 0) continue;
                    provItem[map.locProv[s.unitLoc[u]]] = i;
                    if (k == K_MOVE) moveTo[i] = adj.oA[u];
                    if ((k == K_SUPPORT_HOLD || k == K_SUPPORT_MOVE || k == K_CONVOY) && adj.oA[u] >= 0 && adj.oA[u] < map.provCount)
                        otherType[i] = provType[adj.oA[u]];
                }
            }
            else if (s.phase == BoardState.PHASE_RETREAT)
            {
                for (int d = 0; d < s.disCount; d++)
                {
                    int dst = adj.retDest[d];
                    int i = AddLine(s.disPower[d], s.disType[d], s.disLoc[d], dst >= 0 ? K_RETREAT : K_DISBAND, dst, -1, false);
                    if (i >= 0) moveTo[i] = dst;
                }
            }
            else if (s.phase == BoardState.PHASE_ADJUST)
            {
                for (int n = 0; n < adj.adjCount; n++)
                {
                    int ak = adj.adjKind[n]; int loc = adj.adjLoc[n];
                    if (ak == Adjudicator.ADJ_BUILD_ARMY) AddLine(adj.adjPower[n], MapData.ARMY, -1, K_BUILD, loc, -1, false);
                    else if (ak == Adjudicator.ADJ_BUILD_FLEET) AddLine(adj.adjPower[n], MapData.FLEET, -1, K_BUILD, loc, -1, false);
                    else if (ak == Adjudicator.ADJ_DISBAND)
                    {
                        int t = loc >= 0 && loc < map.locCount ? provType[map.locProv[loc]] : -1;
                        AddLine(adj.adjPower[n], t, loc, K_REMOVE, loc, -1, false);
                    }
                    else AddLine(adj.adjPower[n], -1, -1, K_WAIVE, -1, -1, false);
                }
            }
        }

        /// <summary>Reads the outcomes after the referee has resolved the phase that was captured.</summary>
        public void Finish(Adjudicator adj)
        {
            if (phase == BoardState.PHASE_MOVE)
            {
                for (int i = 0; i < count; i++)
                {
                    outcome[i] = adj.rStatus[i];
                    moved[i] = adj.rMoved[i];
                    dislodged[i] = adj.rDislodged[i];
                }
                FindBattles();
            }
            else if (phase == BoardState.PHASE_RETREAT)
            {
                for (int i = 0; i < count; i++)
                {
                    bool ok = kind[i] == K_DISBAND || adj.retOk[i];
                    outcome[i] = ok ? O_OK : O_DESTROYED;
                    moved[i] = kind[i] == K_RETREAT && ok;
                }
            }
            else if (phase == BoardState.PHASE_ADJUST)
            {
                int ordered = count;
                for (int i = 0; i < ordered; i++) outcome[i] = adj.adjOk[i] ? O_OK : O_REFUSED;
                for (int n = 0; n < adj.autoDisbandCount; n++)
                {
                    int loc = adj.autoDisbandLoc[n];
                    AddLine(adj.autoDisbandPower[n], provType[map.locProv[loc]], loc, K_AUTO_REMOVE, loc, -1, false);
                }
            }
            // reading order: country by country, lines in their original order within a country
            int k = 0; groupCount = 0;
            for (int p = 0; p < map.powerCount; p++)
            {
                int first = k;
                for (int i = 0; i < count; i++) if (power[i] == p) { seq[k] = i; k++; }
                if (k > first) { groupPower[groupCount] = p; groupFirst[groupCount] = first; groupItems[groupCount] = k - first; groupCount++; }
            }
        }

        private void FindBattles()
        {
            battleCount = 0;
            for (int p = 0; p < map.provCount; p++)
            {
                int movers = 0; int supports = 0; int winner = -1;
                for (int i = 0; i < count; i++)
                {
                    if (kind[i] == K_MOVE && outcome[i] != O_VOID && outcome[i] != O_NO_PATH && moveTo[i] >= 0 && map.locProv[moveTo[i]] == p)
                    {
                        movers++;
                        if (moved[i]) winner = power[i];
                    }
                }
                if (movers == 0) continue;
                int holder = provItem[p];
                bool defended = holder >= 0 && !(kind[holder] == K_MOVE && moved[holder]);
                if (movers < 2 && !defended) continue;
                for (int i = 0; i < count; i++)
                {
                    if (outcome[i] != O_OK) continue;
                    if (kind[i] == K_SUPPORT_MOVE && b[i] >= 0 && map.locProv[b[i]] == p) supports++;
                    else if (kind[i] == K_SUPPORT_HOLD && a[i] == p && defended) supports++;
                }
                if (winner < 0 && defended && !dislodged[holder]) winner = power[holder];
                int n = battleCount;
                battleProv[n] = p; battleMovers[n] = movers; battleSupports[n] = supports; battleDefended[n] = defended; battleWinner[n] = winner;
                battleCount = n + 1;
            }
        }

        // =====================================================================
        // Words
        // =====================================================================

        private string UnitWord(int type) { return type == MapData.FLEET ? "Fleet" : (type == MapData.ARMY ? "Army" : "Unit"); }

        private string ProvName(int p) { return p >= 0 && p < map.provCount ? map.provName[p] : "?"; }

        /// <summary>The line as the announcer says it, for example "Army Alder Gate moves to Alder Marsh".</summary>
        public string Text(int i)
        {
            if (i < 0 || i >= count) return "";
            int k = kind[i];
            string unit = UnitWord(unitType[i]) + " " + map.LocName(fromLoc[i]);
            if (k == K_HOLD) return unit + " holds";
            if (k == K_MOVE) return unit + " moves to " + map.LocName(a[i]) + (via[i] ? " by convoy" : "");
            if (k == K_SUPPORT_HOLD) return unit + " supports " + UnitWord(otherType[i]) + " " + ProvName(a[i]);
            if (k == K_SUPPORT_MOVE) return unit + " supports " + UnitWord(otherType[i]) + " " + ProvName(a[i]) + " to " + map.LocName(b[i]);
            if (k == K_CONVOY) return unit + " convoys " + UnitWord(otherType[i]) + " " + ProvName(a[i]) + " to " + ProvName(b[i]);
            if (k == K_RETREAT) return unit + " retreats to " + map.LocName(a[i]);
            if (k == K_DISBAND) return unit + " disbands";
            if (k == K_BUILD) return "Builds " + (unitType[i] == MapData.FLEET ? "a fleet" : "an army") + " in " + map.LocName(a[i]);
            if (k == K_REMOVE) return "Removes the " + UnitWord(unitType[i]).ToLower() + " in " + map.LocName(a[i]);
            if (k == K_WAIVE) return "Waives a build";
            if (k == K_AUTO_REMOVE) return unit + " is removed (no removal was ordered)";
            return "";
        }

        /// <summary>One or two words on what became of the line; "" when it simply worked.</summary>
        public string OutcomeText(int i)
        {
            if (i < 0 || i >= count) return "";
            string r = "";
            int o = outcome[i];
            if (o == O_BOUNCED) r = "bounced";
            else if (o == O_CUT) r = "cut";
            else if (o == O_VOID) r = "impossible";
            else if (o == O_NO_PATH) r = "no convoy";
            else if (o == O_DESTROYED) r = "destroyed";
            else if (o == O_REFUSED) r = "refused";
            if (dislodged[i]) r = r == "" ? "dislodged" : r + ", dislodged";
            return r;
        }

        /// <summary>What to say about how a power's orders arrived; "" when there is nothing to remark on.</summary>
        public string StatusText(int status)
        {
            if (status == S_NONE) return "No orders received";
            if (status == S_UNOPENED) return "Sealed orders were never opened";
            if (status == S_NOBODY) return "No player";
            if (status == S_AWAY) return "Player away";
            return "";
        }

        // =====================================================================
        // Timing: everyone computes the same place in the reading from the same clock
        // =====================================================================

        /// <summary>How long the whole announcement takes, in seconds.</summary>
        public float Duration(float lead, float perPower, float perOrder, float result)
        {
            if (count == 0) return 0;
            return lead + groupCount * perPower + count * perOrder + result;
        }

        /// <summary>
        /// Where the reading is, t seconds after it began. Sets <see cref="at"/> (AT_*),
        /// <see cref="atPower"/> (the country being read, or -1) and <see cref="atLine"/>
        /// (the line being read, or -1; every line before it in <see cref="seq"/> order has been read).
        /// </summary>
        public void Locate(float t, float lead, float perPower, float perOrder, float result)
        {
            atPower = -1; atLine = -1;
            if (count == 0) { at = AT_DONE; return; }
            if (t < lead) { at = AT_LEAD; return; }
            float x = t - lead;
            for (int g = 0; g < groupCount; g++)
            {
                if (x < perPower) { at = AT_POWER; atPower = groupPower[g]; return; }
                x -= perPower;
                float span = groupItems[g] * perOrder;
                if (x < span)
                {
                    int n = perOrder > 0 ? (int)(x / perOrder) : 0;
                    if (n >= groupItems[g]) n = groupItems[g] - 1;
                    at = AT_ORDER; atPower = groupPower[g]; atLine = seq[groupFirst[g] + n];
                    return;
                }
                x -= span;
            }
            at = x < result ? AT_RESULT : AT_DONE;
        }

        /// <summary>Position of a line in the reading order (0 = read first), or -1.</summary>
        public int ReadIndex(int line)
        {
            for (int k = 0; k < count; k++) if (seq[k] == line) return k;
            return -1;
        }
    }
}
