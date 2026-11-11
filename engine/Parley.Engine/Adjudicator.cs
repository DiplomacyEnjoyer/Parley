// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// The rules referee. Give it a board and everybody's orders and it works out what
    /// happens, then moves the game on to the next phase.
    ///
    /// Movement is resolved with the "decisions on partial information" method from
    /// chapter 5 of the DATC (Diplomacy Adjudicator Test Cases, L. B. Kruijswijk):
    /// every order has a few yes/no questions attached (did the move succeed? was the
    /// support cut? was the unit dislodged? did the convoy get through?). Each question
    /// is answered as soon as the answers it depends on allow it. If everything left
    /// is waiting on everything else, the orders involved form either a ring of moves
    /// (they all succeed) or a convoy paradox (the Szykman rule: the convoy fails).
    ///
    /// Written from the DATC text; no code from other adjudicators is used.
    ///
    /// UdonSharp subset: arrays only, loops instead of recursion, no exceptions.
    /// </summary>
    public class Adjudicator : UdonSharpBehaviour
    {
        // ---- order types (movement phase) ----
        public const int HOLD = 0;
        public const int MOVE = 1;          // oA = destination loc, oVia = "via convoy"
        public const int SUPPORT_HOLD = 2;  // oA = province of the unit being supported
        public const int SUPPORT_MOVE = 3;  // oA = province of the moving unit, oB = its destination loc
        public const int CONVOY = 4;        // oA = province of the army, oB = destination province

        // ---- per-order result ----
        public const int R_OK = 0;       // did what it was told
        public const int R_BOUNCED = 1;  // move failed
        public const int R_CUT = 2;      // support was cut
        public const int R_VOID = 3;     // order was illegal or matched nothing: unit simply held
        public const int R_NO_PATH = 4;  // convoyed move had no working convoy

        // ---- adjustment (build phase) order kinds ----
        public const int ADJ_BUILD_ARMY = 1;
        public const int ADJ_BUILD_FLEET = 2;
        public const int ADJ_DISBAND = 3;
        public const int ADJ_WAIVE = 4;

        private const int UND = 0;
        private const int YES = 1;
        private const int NO = 2;

        public MapData map;

        // ---- input: movement orders, indexed like BoardState units ----
        public int[] oType;
        public int[] oA;
        public int[] oB;
        public bool[] oVia;

        // ---- input: retreat orders, indexed like BoardState dislodged units (loc, or -1 = disband) ----
        public int[] retDest;

        // ---- input: adjustment orders, in the order they were given ----
        public int adjCount;
        public int[] adjPower;
        public int[] adjKind;
        public int[] adjLoc;

        // ---- output of the last movement phase (indexed like the units BEFORE they moved) ----
        public int resCount;
        public int[] resType;
        public int[] resPower;
        public int[] resLoc;
        public int[] rStatus;
        public bool[] rMoved;
        public bool[] rDislodged;
        // ---- output of the last retreat / adjustment phase ----
        public bool[] retOk;
        public bool[] adjOk;
        /// <summary>Units removed by the forced-disband rule in the last adjustment phase.</summary>
        public int autoDisbandCount;
        public int[] autoDisbandLoc;
        public int[] autoDisbandPower;

        // ---- working memory ----
        private int cap;
        private int[] eff;       // effective order type after legality checks
        private int[] dest;      // destination loc of a mover
        private bool[] conv;     // mover travels by convoy
        private bool[] convLegal;
        private int[] supUnit;   // unit this (valid, matching) support helps, or -1
        private int[] convArmy;  // army this (valid, matching) convoy carries, or -1
        private int[] h2h;       // head-to-head opponent, or -1
        private int[] moveDec; private int[] supDec; private int[] disDec; private int[] pathDec;
        private bool[] convFail;
        private int[] mvHead; private int[] mvNext;   // movers into each province
        private int[] supHead; private int[] supNext; // supporters of each unit
        private int[] cvHead; private int[] cvNext;   // convoying fleets of each army
        private int[] queue; private bool[] seen; private int[] stackNode; private int[] stackIter; private bool[] onPath;
        private int[] tmp; private int[] nodeOf; private bool[] reach; private int[] dist;
        private int[] nuType; private int[] nuPower; private int[] nuLoc;

        public void Init(MapData m)
        {
            map = m;
            cap = m.provCount;
            oType = new int[cap]; oA = new int[cap]; oB = new int[cap]; oVia = new bool[cap];
            retDest = new int[cap];
            int acap = cap * 2 + m.powerCount + 8;
            adjPower = new int[acap]; adjKind = new int[acap]; adjLoc = new int[acap]; adjOk = new bool[acap];
            resType = new int[cap]; resPower = new int[cap]; resLoc = new int[cap];
            rStatus = new int[cap]; rMoved = new bool[cap]; rDislodged = new bool[cap]; retOk = new bool[cap];
            autoDisbandLoc = new int[cap]; autoDisbandPower = new int[cap];
            eff = new int[cap]; dest = new int[cap]; conv = new bool[cap]; convLegal = new bool[cap];
            supUnit = new int[cap]; convArmy = new int[cap]; h2h = new int[cap];
            moveDec = new int[cap]; supDec = new int[cap]; disDec = new int[cap]; pathDec = new int[cap];
            convFail = new bool[cap];
            mvHead = new int[cap]; mvNext = new int[cap]; supHead = new int[cap]; supNext = new int[cap];
            cvHead = new int[cap]; cvNext = new int[cap];
            queue = new int[cap]; seen = new bool[cap]; stackNode = new int[cap + 2]; stackIter = new int[cap + 2]; onPath = new bool[cap];
            tmp = new int[cap]; nodeOf = new int[cap]; dist = new int[cap];
            nuType = new int[cap]; nuPower = new int[cap]; nuLoc = new int[cap];
            reach = new bool[1];
            ClearOrders();
        }

        // =====================================================================
        // Entering orders
        // =====================================================================

        /// <summary>Resets every order: all units hold, all retreats disband, no builds.</summary>
        public void ClearOrders()
        {
            for (int i = 0; i < cap; i++) { oType[i] = HOLD; oA[i] = -1; oB[i] = -1; oVia[i] = false; retDest[i] = -1; }
            adjCount = 0;
        }

        public void SetHold(int unit) { oType[unit] = HOLD; oA[unit] = -1; oB[unit] = -1; oVia[unit] = false; }
        public void SetMove(int unit, int destLoc, bool viaConvoy) { oType[unit] = MOVE; oA[unit] = destLoc; oB[unit] = -1; oVia[unit] = viaConvoy; }
        public void SetSupportHold(int unit, int targetProv) { oType[unit] = SUPPORT_HOLD; oA[unit] = targetProv; oB[unit] = -1; oVia[unit] = false; }
        public void SetSupportMove(int unit, int fromProv, int destLoc) { oType[unit] = SUPPORT_MOVE; oA[unit] = fromProv; oB[unit] = destLoc; oVia[unit] = false; }
        public void SetConvoy(int unit, int armyProv, int destProv) { oType[unit] = CONVOY; oA[unit] = armyProv; oB[unit] = destProv; oVia[unit] = false; }
        public void SetRetreat(int dislodgedIndex, int destLoc) { retDest[dislodgedIndex] = destLoc; }

        public void AddAdjust(int power, int kind, int loc)
        {
            if (adjCount >= adjPower.Length) return;
            adjPower[adjCount] = power; adjKind[adjCount] = kind; adjLoc[adjCount] = loc; adjCount++;
        }

        /// <summary>Resolves whatever phase the board is in, using the orders entered, and advances the board.</summary>
        public void Resolve(BoardState s)
        {
            if (s.phase == BoardState.PHASE_MOVE) ResolveMovement(s);
            else if (s.phase == BoardState.PHASE_RETREAT) ResolveRetreats(s);
            else if (s.phase == BoardState.PHASE_ADJUST) ResolveAdjustments(s);
        }

        // =====================================================================
        // Movement phase
        // =====================================================================

        public void ResolveMovement(BoardState s)
        {
            int n = s.unitCount;
            PrepareOrders(s);

            // Answer every question that can be answered; when stuck, apply the backup rule.
            int guard = 0;
            while (true)
            {
                bool changed = Sweep(s);
                if (changed) continue;
                if (AllDecided(n)) break;
                ApplyBackupRule(s);
                guard++;
                if (guard > cap + 4) { FailEverythingUndecided(n); break; }
            }

            // ---- report ----
            resCount = n;
            for (int u = 0; u < n; u++)
            {
                resType[u] = s.unitType[u]; resPower[u] = s.unitPower[u]; resLoc[u] = s.unitLoc[u];
                rMoved[u] = eff[u] == MOVE && moveDec[u] == YES;
                rDislodged[u] = disDec[u] == YES;
                int st = R_OK;
                if (oType[u] != HOLD && eff[u] == HOLD) st = R_VOID;
                else if (eff[u] == MOVE) { if (moveDec[u] != YES) st = pathDec[u] == NO ? R_NO_PATH : R_BOUNCED; }
                else if (eff[u] == SUPPORT_HOLD || eff[u] == SUPPORT_MOVE) { if (supDec[u] != YES) st = R_CUT; }
                else if (eff[u] == CONVOY) { if (convFail[u] || disDec[u] == YES) st = R_NO_PATH; }
                rStatus[u] = st;
            }

            // ---- apply to the board ----
            int nn = 0;
            s.disCount = 0;
            for (int u = 0; u < n; u++)
            {
                if (rDislodged[u])
                {
                    int d = s.disCount;
                    s.disType[d] = s.unitType[u]; s.disPower[d] = s.unitPower[u]; s.disLoc[d] = s.unitLoc[u];
                    int from = -1;
                    int p = map.locProv[s.unitLoc[u]];
                    for (int x = mvHead[p]; x >= 0; x = mvNext[x])
                        if (moveDec[x] == YES) from = conv[x] ? -1 : map.locProv[s.unitLoc[x]];
                    s.disFrom[d] = from;
                    s.disCount = d + 1;
                }
                else
                {
                    nuType[nn] = s.unitType[u]; nuPower[nn] = s.unitPower[u];
                    nuLoc[nn] = rMoved[u] ? dest[u] : s.unitLoc[u];
                    nn++;
                }
            }
            // standoffs: a province left empty where at least one real attempt to enter bounced
            for (int p = 0; p < map.provCount; p++)
            {
                bool bounced = false;
                for (int x = mvHead[p]; x >= 0; x = mvNext[x])
                    if (moveDec[x] != YES && PreventS(s, x, false) > 0) bounced = true;
                s.contested[p] = bounced;
            }
            s.unitCount = nn;
            for (int u = 0; u < nn; u++) { s.unitType[u] = nuType[u]; s.unitPower[u] = nuPower[u]; s.unitLoc[u] = nuLoc[u]; }
            s.Reindex(map);
            for (int p = 0; p < map.provCount; p++) if (s.provUnit[p] >= 0) s.contested[p] = false;

            if (s.disCount > 0)
            {
                s.phase = BoardState.PHASE_RETREAT;
                // If nobody can actually retreat anywhere, the units are simply destroyed.
                bool any = false;
                for (int d = 0; d < s.disCount && !any; d++)
                    for (int l = 0; l < map.locCount && !any; l++)
                        if (RetreatValid(s, d, l)) any = true;
                if (!any) { s.disCount = 0; AfterRetreats(s); }
            }
            else AfterRetreats(s);
        }

        /// <summary>Checks every order for legality and links supports and convoys to the orders they help.</summary>
        private void PrepareOrders(BoardState s)
        {
            int n = s.unitCount;
            for (int p = 0; p < map.provCount; p++) mvHead[p] = -1;
            for (int u = 0; u < n; u++)
            {
                eff[u] = HOLD; dest[u] = -1; conv[u] = false; convLegal[u] = false; supUnit[u] = -1; convArmy[u] = -1; h2h[u] = -1;
                mvNext[u] = -1; supHead[u] = -1; supNext[u] = -1; cvHead[u] = -1; cvNext[u] = -1;
                convFail[u] = false;
            }

            // 1. which convoy orders are possible at all on this board?
            for (int u = 0; u < n; u++)
            {
                if (oType[u] != CONVOY) continue;
                convLegal[u] = ConvoyOrderLegal(s, u, oA[u], oB[u]);
            }

            // 2. moves
            for (int u = 0; u < n; u++)
            {
                if (oType[u] != MOVE) continue;
                int d = oA[u];
                if (d < 0 || d >= map.locCount) continue;
                int sp = map.locProv[s.unitLoc[u]];
                int dp = map.locProv[d];
                if (dp == sp) continue;
                if (s.unitType[u] == MapData.ARMY)
                {
                    d = dp; // coasts mean nothing to an army
                    if (map.provType[dp] == MapData.SEA) continue;
                    if (!map.ArmyAdjacent(sp, dp))
                    {
                        // only reachable by sea: legal if a chain of fleets could exist right now
                        if (!PossibleConvoy(s, sp, dp)) continue;
                        conv[u] = true;
                    }
                    else
                    {
                        // Adjacent: goes by land unless the player shows intent to convoy,
                        // either explicitly or by ordering one of their own fleets to carry it.
                        bool c = oVia[u] && PossibleConvoy(s, sp, dp);
                        for (int f = 0; f < n && !c; f++)
                            if (convLegal[f] && s.unitPower[f] == s.unitPower[u] && oA[f] == sp && oB[f] == dp) c = true;
                        conv[u] = c;
                    }
                }
                else
                {
                    if (!map.FleetAdjacent(s.unitLoc[u], d)) continue;
                }
                eff[u] = MOVE; dest[u] = d;
                mvNext[u] = mvHead[dp]; mvHead[dp] = u;
            }

            // 3. supports and convoys must match what the other unit is actually doing
            for (int u = 0; u < n; u++)
            {
                int sp = map.locProv[s.unitLoc[u]];
                if (oType[u] == SUPPORT_HOLD)
                {
                    int tp = oA[u];
                    if (tp < 0 || tp >= map.provCount || tp == sp) continue;
                    int t = s.provUnit[tp];
                    if (t < 0 || !CanReach(s, u, tp)) continue;
                    if (eff[t] == MOVE) continue; // a unit trying to move cannot be supported in place
                    eff[u] = SUPPORT_HOLD; supUnit[u] = t;
                    supNext[u] = supHead[t]; supHead[t] = u;
                }
                else if (oType[u] == SUPPORT_MOVE)
                {
                    int fp = oA[u]; int d = oB[u];
                    if (fp < 0 || fp >= map.provCount || d < 0 || d >= map.locCount) continue;
                    int dp = map.locProv[d];
                    if (dp == sp || fp == sp) continue;
                    int t = s.provUnit[fp];
                    if (t < 0 || !CanReach(s, u, dp)) continue;
                    if (eff[t] != MOVE || map.locProv[dest[t]] != dp) continue;
                    // a coast named in the support must be the coast the unit is really going to
                    if (d != dp && s.unitType[t] == MapData.FLEET && d != dest[t]) continue;
                    eff[u] = SUPPORT_MOVE; supUnit[u] = t;
                    supNext[u] = supHead[t]; supHead[t] = u;
                }
                else if (oType[u] == CONVOY)
                {
                    if (!convLegal[u]) continue;
                    int a = s.provUnit[oA[u]];
                    if (a < 0 || eff[a] != MOVE || !conv[a] || map.locProv[dest[a]] != oB[u]) continue;
                    eff[u] = CONVOY; convArmy[u] = a;
                    cvNext[u] = cvHead[a]; cvHead[a] = u;
                }
            }

            // 4. head-to-head battles: two units moving into each other's province, neither by convoy
            for (int u = 0; u < n; u++)
            {
                if (eff[u] != MOVE) continue;
                int t = s.provUnit[map.locProv[dest[u]]];
                if (t >= 0 && eff[t] == MOVE && !conv[u] && !conv[t] && map.locProv[dest[t]] == map.locProv[s.unitLoc[u]]) h2h[u] = t;
            }

            // 5. what do we already know?
            for (int u = 0; u < n; u++)
            {
                moveDec[u] = eff[u] == MOVE ? UND : NO;
                pathDec[u] = eff[u] == MOVE ? (conv[u] ? UND : YES) : NO;
                supDec[u] = supUnit[u] >= 0 ? UND : NO;
                disDec[u] = UND;
            }
        }

        private bool CanReach(BoardState s, int u, int prov)
        {
            if (s.unitType[u] == MapData.ARMY) return map.provType[prov] != MapData.SEA && map.ArmyAdjacent(map.locProv[s.unitLoc[u]], prov);
            return map.FleetCanReachProv(s.unitLoc[u], prov);
        }

        /// <summary>One pass over all units, answering whatever can now be answered. True if anything new was learned.</summary>
        private bool Sweep(BoardState s)
        {
            int n = s.unitCount;
            bool changed = false;
            for (int u = 0; u < n; u++)
            {
                if (eff[u] == MOVE && pathDec[u] == UND)
                {
                    if (!PathExists(s, u, true)) { pathDec[u] = NO; changed = true; }
                    else if (PathExists(s, u, false)) { pathDec[u] = YES; changed = true; }
                }
                if (supUnit[u] >= 0 && supDec[u] == UND)
                {
                    EvalSupport(s, u);
                    if (supDec[u] != UND) changed = true;
                }
                if (eff[u] == MOVE && moveDec[u] == UND)
                {
                    EvalMove(s, u);
                    if (moveDec[u] != UND) changed = true;
                }
                if (disDec[u] == UND)
                {
                    EvalDislodge(s, u);
                    if (disDec[u] != UND) changed = true;
                }
            }
            return changed;
        }

        private bool AllDecided(int n)
        {
            for (int u = 0; u < n; u++) if (Undecided(u)) return false;
            return true;
        }

        private bool Undecided(int u)
        {
            if (disDec[u] == UND) return true;
            if (eff[u] == MOVE && (moveDec[u] == UND || pathDec[u] == UND)) return true;
            if (supUnit[u] >= 0 && supDec[u] == UND) return true;
            return false;
        }

        private void FailEverythingUndecided(int n)
        {
            for (int u = 0; u < n; u++)
            {
                if (eff[u] == MOVE && moveDec[u] == UND) moveDec[u] = NO;
                if (eff[u] == MOVE && pathDec[u] == UND) pathDec[u] = NO;
                if (supUnit[u] >= 0 && supDec[u] == UND) supDec[u] = NO;
                if (disDec[u] == UND) disDec[u] = NO;
            }
        }

        // ---- strengths. "max" = the most it could still turn out to be, otherwise the least. ----

        private int Supports(BoardState s, int u, bool max, int exceptPower)
        {
            int c = 0;
            for (int x = supHead[u]; x >= 0; x = supNext[x])
            {
                if (s.unitPower[x] == exceptPower) continue;
                if (supDec[x] == YES || (max && supDec[x] == UND)) c++;
            }
            return c;
        }

        private int Attack(BoardState s, int u, bool max)
        {
            if (max) { if (pathDec[u] == NO) return 0; }
            else if (pathDec[u] != YES) return 0;
            int t = s.provUnit[map.locProv[dest[u]]];
            if (t < 0) return 1 + Supports(s, u, max, -1);
            bool leaves = false;
            if (eff[t] == MOVE && h2h[u] < 0) leaves = max ? moveDec[t] != NO : moveDec[t] == YES;
            if (leaves) return 1 + Supports(s, u, max, -1);
            if (s.unitPower[t] == s.unitPower[u]) return 0; // never dislodge your own unit
            return 1 + Supports(s, u, max, s.unitPower[t]); // its owner's supports don't help dislodge it
        }

        private int Hold(BoardState s, int prov, bool max)
        {
            int t = s.provUnit[prov];
            if (t < 0) return 0;
            if (eff[t] == MOVE)
            {
                if (max) return moveDec[t] == YES ? 0 : 1;
                return moveDec[t] == NO ? 1 : 0;
            }
            return 1 + Supports(s, t, max, -1);
        }

        private int PreventS(BoardState s, int u, bool max)
        {
            if (max) { if (pathDec[u] == NO) return 0; }
            else if (pathDec[u] != YES) return 0;
            int o = h2h[u];
            if (o >= 0)
            {
                // a unit that loses a head-to-head fight has no effect on the province its opponent left
                if (max) { if (moveDec[o] == YES) return 0; }
                else if (moveDec[o] != NO) return 0;
            }
            return 1 + Supports(s, u, max, -1);
        }

        private void EvalMove(BoardState s, int u)
        {
            int dp = map.locProv[dest[u]];
            int aMin = Attack(s, u, false);
            int aMax = Attack(s, u, true);
            bool win = true; bool lose = false;
            int o = h2h[u];
            if (o >= 0)
            {
                if (aMin <= 1 + Supports(s, o, true, -1)) win = false;
                if (aMax <= 1 + Supports(s, o, false, -1)) lose = true;
            }
            else
            {
                if (aMin <= Hold(s, dp, true)) win = false;
                if (aMax <= Hold(s, dp, false)) lose = true;
            }
            for (int x = mvHead[dp]; x >= 0; x = mvNext[x])
            {
                if (x == u) continue;
                if (aMin <= PreventS(s, x, true)) win = false;
                if (aMax <= PreventS(s, x, false)) lose = true;
            }
            if (win) moveDec[u] = YES;
            else if (lose) moveDec[u] = NO;
        }

        private void EvalSupport(BoardState s, int u)
        {
            int sp = map.locProv[s.unitLoc[u]];
            int against = eff[u] == SUPPORT_MOVE ? map.locProv[dest[supUnit[u]]] : -1;
            bool cut = false; bool maybe = false;
            for (int x = mvHead[sp]; x >= 0; x = mvNext[x])
            {
                if (s.unitPower[x] == s.unitPower[u]) continue;            // own units never cut
                if (map.locProv[s.unitLoc[x]] == against) continue;         // the unit under attack cannot cut
                if (pathDec[x] == YES) cut = true;
                else if (pathDec[x] == UND) maybe = true;
            }
            if (cut || disDec[u] == YES) supDec[u] = NO;
            else if (!maybe && disDec[u] == NO) supDec[u] = YES;
        }

        private void EvalDislodge(BoardState s, int u)
        {
            if (eff[u] == MOVE && moveDec[u] == YES) { disDec[u] = NO; return; }
            bool stays = eff[u] != MOVE || moveDec[u] == NO;
            bool anyYes = false; bool anyUnd = false;
            int p = map.locProv[s.unitLoc[u]];
            for (int x = mvHead[p]; x >= 0; x = mvNext[x])
            {
                if (moveDec[x] == YES) anyYes = true;
                else if (moveDec[x] == UND) anyUnd = true;
            }
            if (anyYes) { if (stays) disDec[u] = YES; }
            else if (!anyUnd) disDec[u] = NO;
        }

        /// <summary>
        /// Is there a chain of convoying fleets for mover u? With max=true fleets that might
        /// still be dislodged count; with max=false only fleets known to survive count.
        /// </summary>
        private bool PathExists(BoardState s, int u, bool max)
        {
            int src = map.locProv[s.unitLoc[u]];
            int dst = map.locProv[dest[u]];
            int qh = 0; int qt = 0;
            for (int f = cvHead[u]; f >= 0; f = cvNext[f])
            {
                seen[f] = false;
                if (!FleetUsable(f, max)) continue;
                if (map.SeaAdjacent(src, map.locProv[s.unitLoc[f]])) { seen[f] = true; queue[qt] = f; qt++; }
            }
            while (qh < qt)
            {
                int f = queue[qh]; qh++;
                int fp = map.locProv[s.unitLoc[f]];
                if (map.SeaAdjacent(fp, dst)) return true;
                for (int g = cvHead[u]; g >= 0; g = cvNext[g])
                {
                    if (seen[g] || !FleetUsable(g, max)) continue;
                    if (map.SeaAdjacent(fp, map.locProv[s.unitLoc[g]])) { seen[g] = true; queue[qt] = g; qt++; }
                }
            }
            return false;
        }

        private bool FleetUsable(int f, bool max)
        {
            if (convFail[f]) return false;
            return max ? disDec[f] != YES : disDec[f] == NO;
        }

        /// <summary>
        /// Nothing more can be worked out, so some orders depend on each other in a circle.
        /// Find one such circle that depends on nothing outside itself and settle it:
        /// convoy paradox -> the convoys in it fail (Szykman); ring of moves -> all succeed.
        /// </summary>
        private void ApplyBackupRule(BoardState s)
        {
            int n = s.unitCount;
            int k = 0;
            for (int u = 0; u < n; u++) { nodeOf[u] = -1; if (Undecided(u)) { nodeOf[u] = k; tmp[k] = u; k++; } }
            if (k == 0) return;
            if (reach.Length < k * k) reach = new bool[k * k];
            for (int i = 0; i < k * k; i++) reach[i] = false;

            for (int i = 0; i < k; i++)
            {
                int u = tmp[i];
                int sp = map.locProv[s.unitLoc[u]];
                // being dislodged / having support cut depends on everyone moving in
                for (int x = mvHead[sp]; x >= 0; x = mvNext[x]) Edge(i, x, k);
                if (eff[u] != MOVE) continue;
                if (pathDec[u] == UND) for (int f = cvHead[u]; f >= 0; f = cvNext[f]) Edge(i, f, k);
                for (int x = supHead[u]; x >= 0; x = supNext[x]) Edge(i, x, k);
                int dp = map.locProv[dest[u]];
                int t = s.provUnit[dp];
                if (t >= 0)
                {
                    Edge(i, t, k);
                    for (int x = supHead[t]; x >= 0; x = supNext[x]) Edge(i, x, k);
                }
                for (int x = mvHead[dp]; x >= 0; x = mvNext[x])
                {
                    if (x == u) continue;
                    Edge(i, x, k);
                    for (int y = supHead[x]; y >= 0; y = supNext[y]) Edge(i, y, k);
                    if (h2h[x] >= 0) Edge(i, h2h[x], k);
                }
            }
            // transitive closure
            for (int m = 0; m < k; m++)
                for (int i = 0; i < k; i++)
                {
                    if (!reach[i * k + m]) continue;
                    for (int j = 0; j < k; j++) if (reach[m * k + j]) reach[i * k + j] = true;
                }
            // a circle that depends on nothing outside itself
            int root = -1;
            for (int i = 0; i < k && root < 0; i++)
            {
                if (!reach[i * k + i]) continue;
                bool closed = true;
                for (int j = 0; j < k && closed; j++) if (reach[i * k + j] && !reach[j * k + i]) closed = false;
                if (closed) root = i;
            }
            if (root < 0) { FailEverythingUndecided(n); return; }

            bool progress = false;
            bool onlyMoves = true;
            for (int j = 0; j < k; j++)
            {
                if (!reach[root * k + j]) continue;
                int u = tmp[j];
                if (eff[u] != MOVE) onlyMoves = false;
                if (eff[u] == CONVOY && !convFail[u] && pathDec[convArmy[u]] == UND) { convFail[u] = true; progress = true; }
            }
            if (progress) return;
            for (int j = 0; j < k; j++)
            {
                if (!reach[root * k + j]) continue;
                int u = tmp[j];
                if (onlyMoves) { if (moveDec[u] == UND) moveDec[u] = YES; }
                else
                {
                    // not a situation the standard rules can produce: fail the whole circle
                    if (eff[u] == MOVE && moveDec[u] == UND) moveDec[u] = NO;
                    if (eff[u] == MOVE && pathDec[u] == UND) pathDec[u] = NO;
                    if (supUnit[u] >= 0 && supDec[u] == UND) supDec[u] = NO;
                    if (disDec[u] == UND) disDec[u] = NO;
                }
            }
        }

        private void Edge(int fromNode, int toUnit, int k)
        {
            int j = nodeOf[toUnit];
            if (j >= 0) reach[fromNode * k + j] = true;
        }

        // =====================================================================
        // Convoy geometry (also used by the legal-options generator)
        // =====================================================================

        /// <summary>Could an army get from src to dst over fleets currently at sea (whatever their orders)?</summary>
        public bool PossibleConvoy(BoardState s, int src, int dst)
        {
            return ConvoyReach(s, src, dst, -1);
        }

        /// <summary>Like <see cref="PossibleConvoy"/> but pretending the fleet in province skipProv is not there.</summary>
        public bool ConvoyReach(BoardState s, int src, int dst, int skipProv)
        {
            if (src == dst) return false;
            if (map.provType[src] != MapData.COAST || map.provType[dst] != MapData.COAST) return false;
            for (int p = 0; p < map.provCount; p++) onPath[p] = false;
            int qh = 0; int qt = 0;
            int st = map.seaStart[src]; int c = map.seaCount[src];
            for (int i = 0; i < c; i++)
            {
                int p = map.seaAdj[st + i];
                if (p != skipProv && SeaFleetAt(s, p) && !onPath[p]) { onPath[p] = true; queue[qt] = p; qt++; }
            }
            while (qh < qt)
            {
                int p = queue[qh]; qh++;
                st = map.seaStart[p]; c = map.seaCount[p];
                for (int i = 0; i < c; i++)
                {
                    int q = map.seaAdj[st + i];
                    if (q == dst) return true;
                    if (q != skipProv && SeaFleetAt(s, q) && !onPath[q]) { onPath[q] = true; queue[qt] = q; qt++; }
                }
            }
            return false;
        }

        private bool SeaFleetAt(BoardState s, int prov)
        {
            if (map.provType[prov] != MapData.SEA) return false;
            int u = s.provUnit[prov];
            return u >= 0 && s.unitType[u] == MapData.FLEET;
        }

        /// <summary>
        /// Is "fleet u convoys the army in armyProv to destProv" an order that could possibly
        /// work on this board? The fleet must be at sea and be a necessary link of at least one
        /// chain of fleets from the army to the destination.
        /// </summary>
        public bool ConvoyOrderLegal(BoardState s, int u, int armyProv, int destProv)
        {
            if (armyProv < 0 || armyProv >= map.provCount || destProv < 0 || destProv >= map.provCount) return false;
            if (s.unitType[u] != MapData.FLEET) return false;
            int fp = map.locProv[s.unitLoc[u]];
            if (map.provType[fp] != MapData.SEA) return false;
            int a = s.provUnit[armyProv];
            if (a < 0 || s.unitType[a] != MapData.ARMY) return false;
            if (armyProv == destProv) return false;
            if (map.provType[armyProv] != MapData.COAST || map.provType[destProv] != MapData.COAST) return false;
            return RouteThrough(s, armyProv, destProv, fp);
        }

        /// <summary>
        /// Searches for a chain src - fleet - ... - fleet - dst that includes the fleet in
        /// province "via" and has no shortcuts (so every fleet in it is really needed).
        /// Depth-first search with an explicit stack.
        /// </summary>
        private bool RouteThrough(BoardState s, int src, int dst, int via)
        {
            for (int p = 0; p < map.provCount; p++) onPath[p] = false;
            int depth = 0;
            stackNode[0] = src; stackIter[0] = 0;
            bool viaOnPath = false;
            int steps = 0;
            while (depth >= 0)
            {
                steps++;
                if (steps > 20000)
                {
                    // absurdly tangled fleet positions: fall back to a plain reachability test
                    return ConvoyReachEnds(s, src, via) && ConvoyReachEnds(s, dst, via);
                }
                int node = stackNode[depth];
                int it = stackIter[depth];
                if (it >= map.seaCount[node])
                {
                    if (depth > 0) { onPath[node] = false; if (node == via) viaOnPath = false; }
                    depth--;
                    continue;
                }
                stackIter[depth] = it + 1;
                int q = map.seaAdj[map.seaStart[node] + it];
                if (onPath[q] || !SeaFleetAt(s, q)) continue;
                // no shortcuts: q may touch only the previous link of the chain
                bool chord = false;
                for (int d = 0; d < depth && !chord; d++) if (map.SeaAdjacent(stackNode[d], q)) chord = true;
                if (chord) continue;
                if (map.SeaAdjacent(q, dst))
                {
                    // q completes a chain (and must be its last link)
                    if (viaOnPath || q == via) return true;
                    continue;
                }
                depth++;
                stackNode[depth] = q; stackIter[depth] = 0; onPath[q] = true;
                if (q == via) viaOnPath = true;
            }
            return false;
        }

        // Can the coastal province "end" be linked to the fleet in sea province "via" over fleets at sea?
        private bool ConvoyReachEnds(BoardState s, int end, int via)
        {
            if (map.SeaAdjacent(end, via)) return true;
            for (int p = 0; p < map.provCount; p++) seen[p] = false;
            int qh = 0; int qt = 0;
            queue[qt] = via; qt++; seen[via] = true;
            while (qh < qt)
            {
                int p = queue[qh]; qh++;
                int st = map.seaStart[p]; int c = map.seaCount[p];
                for (int i = 0; i < c; i++)
                {
                    int q = map.seaAdj[st + i];
                    if (q == end) return true;
                    if (!seen[q] && SeaFleetAt(s, q)) { seen[q] = true; queue[qt] = q; qt++; }
                }
            }
            return false;
        }

        // =====================================================================
        // Retreat phase
        // =====================================================================

        /// <summary>May dislodged unit d retreat to loc?</summary>
        public bool RetreatValid(BoardState s, int d, int loc)
        {
            if (loc < 0 || loc >= map.locCount) return false;
            if (!map.CanStand(s.disType[d], loc)) return false;
            int dp = map.locProv[loc];
            int sp = map.locProv[s.disLoc[d]];
            if (s.disType[d] == MapData.ARMY) { if (!map.ArmyAdjacent(sp, dp)) return false; }
            else if (!map.FleetAdjacent(s.disLoc[d], loc)) return false;
            if (s.provUnit[dp] >= 0) return false;   // occupied
            if (s.contested[dp]) return false;       // left empty by a standoff
            if (dp == s.disFrom[d]) return false;    // where the attacker came from
            return true;
        }

        public void ResolveRetreats(BoardState s)
        {
            int n = s.disCount;
            for (int p = 0; p < map.provCount; p++) tmp[p] = 0;
            for (int d = 0; d < n; d++)
            {
                retOk[d] = RetreatValid(s, d, retDest[d]);
                if (retOk[d])
                {
                    if (s.disType[d] == MapData.ARMY) retDest[d] = map.locProv[retDest[d]];
                    tmp[map.locProv[retDest[d]]]++;
                }
            }
            // two units retreating to the same province are both destroyed
            for (int d = 0; d < n; d++)
                if (retOk[d] && tmp[map.locProv[retDest[d]]] != 1) retOk[d] = false;
            for (int d = 0; d < n; d++)
                if (retOk[d]) s.AddUnit(map, s.disType[d], s.disPower[d], retDest[d]);
            AfterRetreats(s);
        }

        private void AfterRetreats(BoardState s)
        {
            s.disCount = 0;
            for (int p = 0; p < map.provCount; p++) s.contested[p] = false;
            if (s.season == BoardState.SPRING)
            {
                s.season = BoardState.FALL; s.phase = BoardState.PHASE_MOVE;
                return;
            }
            // end of the year: centres change hands
            for (int u = 0; u < s.unitCount; u++)
            {
                int p = map.locProv[s.unitLoc[u]];
                if (map.provSc[p]) s.owner[p] = s.unitPower[u];
            }
            int w = FindWinner(s);
            if (w >= 0)
            {
                s.winner = w; s.season = BoardState.WINTER; s.phase = BoardState.PHASE_FINISHED;
                return;
            }
            bool any = false;
            for (int pw = 0; pw < map.powerCount && !any; pw++) if (AdjustDelta(s, pw) != 0) any = true;
            if (any) { s.season = BoardState.WINTER; s.phase = BoardState.PHASE_ADJUST; }
            else { s.year = s.year + 1; s.season = BoardState.SPRING; s.phase = BoardState.PHASE_MOVE; }
        }

        /// <summary>The power that has reached the victory target (most centres wins a tie-free race), or -1.</summary>
        public int FindWinner(BoardState s)
        {
            int best = -1; int bestCount = 0; bool tie = false;
            for (int pw = 0; pw < map.powerCount; pw++)
            {
                int c = s.CountCenters(pw);
                if (c > bestCount) { best = pw; bestCount = c; tie = false; }
                else if (c == bestCount) tie = true;
            }
            if (best >= 0 && !tie && bestCount >= map.victoryCenters) return best;
            return -1;
        }

        // =====================================================================
        // Adjustment (build / disband) phase
        // =====================================================================

        /// <summary>
        /// What this power must do in the adjustment phase: a positive number of builds it may
        /// make (already limited by free build sites), a negative number of units it must
        /// disband, or 0.
        /// </summary>
        public int AdjustDelta(BoardState s, int power)
        {
            int delta = s.CountCenters(power) - s.CountUnits(power);
            if (delta <= 0) return delta;
            int sites = 0;
            for (int p = 0; p < map.provCount; p++) if (IsBuildSite(s, power, p)) sites++;
            return delta < sites ? delta : sites;
        }

        /// <summary>May this power build in province p right now?</summary>
        public bool IsBuildSite(BoardState s, int power, int p)
        {
            if (!map.provSc[p] || s.owner[p] != power || s.provUnit[p] >= 0) return false;
            if (map.buildMode == MapData.BUILD_HOME && map.provHome[p] != power) return false;
            return true;
        }

        public bool BuildValid(BoardState s, int power, int unitType, int loc)
        {
            if (loc < 0 || loc >= map.locCount) return false;
            if (!map.CanStand(unitType, loc)) return false;
            return IsBuildSite(s, power, map.locProv[loc]);
        }

        public void ResolveAdjustments(BoardState s)
        {
            int np = map.powerCount;
            int[] remaining = dist; // reuse scratch; one entry per power (powerCount <= provCount is not guaranteed, so guard)
            if (remaining.Length < np) { dist = new int[np]; remaining = dist; }
            for (int pw = 0; pw < np; pw++) remaining[pw] = s.CountCenters(pw) - s.CountUnits(pw);
            for (int i = 0; i < adjCount; i++)
            {
                adjOk[i] = false;
                int pw = adjPower[i];
                if (pw < 0 || pw >= np) continue;
                int kind = adjKind[i];
                if (kind == ADJ_BUILD_ARMY || kind == ADJ_BUILD_FLEET)
                {
                    int ut = kind == ADJ_BUILD_ARMY ? MapData.ARMY : MapData.FLEET;
                    if (remaining[pw] > 0 && BuildValid(s, pw, ut, adjLoc[i]))
                    {
                        s.AddUnit(map, ut, pw, adjLoc[i]);
                        remaining[pw]--; adjOk[i] = true;
                    }
                }
                else if (kind == ADJ_DISBAND)
                {
                    if (remaining[pw] < 0 && adjLoc[i] >= 0 && adjLoc[i] < map.locCount)
                    {
                        int u = s.provUnit[map.locProv[adjLoc[i]]];
                        if (u >= 0 && s.unitPower[u] == pw) { s.RemoveUnit(map, u); remaining[pw]++; adjOk[i] = true; }
                    }
                }
                else if (kind == ADJ_WAIVE) adjOk[i] = true;
            }
            // powers that did not disband enough lose units by the standard rule
            autoDisbandCount = 0;
            for (int pw = 0; pw < np; pw++)
            {
                while (remaining[pw] < 0)
                {
                    int u = PickForcedDisband(s, pw);
                    if (u < 0) break;
                    autoDisbandLoc[autoDisbandCount] = s.unitLoc[u]; autoDisbandPower[autoDisbandCount] = pw; autoDisbandCount++;
                    s.RemoveUnit(map, u);
                    remaining[pw]++;
                }
            }
            adjCount = 0;
            s.year = s.year + 1; s.season = BoardState.SPRING; s.phase = BoardState.PHASE_MOVE;
        }

        /// <summary>
        /// Chooses the unit a power loses when it fails to order a required disband:
        /// the one farthest from any centre it owns (counting steps over land and sea
        /// alike); on a tie fleets go before armies, then alphabetical by province name.
        /// </summary>
        public int PickForcedDisband(BoardState s, int power)
        {
            int np = map.provCount;
            int[] d = tmp;
            for (int p = 0; p < np; p++) d[p] = -1;
            int qh = 0; int qt = 0;
            for (int p = 0; p < np; p++) if (s.owner[p] == power) { d[p] = 0; queue[qt] = p; qt++; }
            while (qh < qt)
            {
                int p = queue[qh]; qh++;
                int st = map.anyStart[p]; int c = map.anyCount[p];
                for (int i = 0; i < c; i++)
                {
                    int q = map.anyAdj[st + i];
                    if (d[q] < 0) { d[q] = d[p] + 1; queue[qt] = q; qt++; }
                }
            }
            int best = -1; int bestDist = -1;
            for (int u = 0; u < s.unitCount; u++)
            {
                if (s.unitPower[u] != power) continue;
                int p = map.locProv[s.unitLoc[u]];
                int du = d[p] < 0 ? 1000000 : d[p];
                bool better = false;
                if (best < 0 || du > bestDist) better = true;
                else if (du == bestDist)
                {
                    if (s.unitType[u] != s.unitType[best]) better = s.unitType[u] == MapData.FLEET;
                    else better = CompareText(map.provName[p], map.provName[map.locProv[s.unitLoc[best]]]) < 0;
                }
                if (better) { best = u; bestDist = du; }
            }
            return best;
        }

        // Case-insensitive alphabetical comparison without culture surprises.
        private int CompareText(string a, string b)
        {
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
            {
                char x = a[i]; char y = b[i];
                if (x >= 'A' && x <= 'Z') x = (char)(x + 32);
                if (y >= 'A' && y <= 'Z') y = (char)(y + 32);
                if (x != y) return x < y ? -1 : 1;
            }
            if (a.Length == b.Length) return 0;
            return a.Length < b.Length ? -1 : 1;
        }

        // =====================================================================
        // Agreed draw
        // =====================================================================

        public void DeclareDraw(BoardState s)
        {
            s.drawn = true; s.winner = -1; s.phase = BoardState.PHASE_FINISHED;
        }
    }
}
