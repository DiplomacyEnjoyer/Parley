// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Turns a board position (and a phase's orders) into short plain text and back.
    ///
    /// A "game code" is one line such as
    ///     P1.ring3.oqahgf.S1M.0L0d1Q-...-....0120301....Xk3P
    /// and contains everything needed to continue the game from that exact point on
    /// any client that has the same map: which map (id + fingerprint), the date and
    /// phase, every unit, every centre's owner, pending retreats, and a checksum that
    /// catches copy/paste damage.
    ///
    /// Only letters, digits, '.' and '-' are used, so codes survive being pasted into
    /// chat programs (no characters that Discord or Slack treat as formatting).
    ///
    /// Layout:  P1 . mapId . mapHash . phase . units . owners . retreats . status . check
    ///   phase     season letter (S/F/W) + year + phase letter (M/R/A/X)
    ///   units     per power, '-' between powers; each unit is 2 characters = loc*2 + type
    ///   owners    one character per supply centre, in map order ('0' = nobody)
    ///   retreats  dislodged units, then '-' and the provinces closed by standoffs
    ///   status    empty, "D" (agreed draw) or "W" + winning power
    /// </summary>
    public class GameCodec : UdonSharpBehaviour
    {
        public const string MAGIC = "P1";
        private const string DIGITS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const int M1 = 1000003;
        private const int M2 = 999983;

        public MapData map;
        /// <summary>Why the last decode failed ("" when it worked).</summary>
        public string error = "";

        private int powerWidth; // characters used for a power number
        private string[] parts;
        private int partCount;
        private string[] groupText;

        public void Init(MapData m)
        {
            map = m;
            powerWidth = m.powerCount < 61 ? 1 : 2;
            parts = new string[16];
            groupText = new string[m.powerCount];
        }

        // =====================================================================
        // Small helpers
        // =====================================================================

        private string Enc1(int v) { return DIGITS.Substring(v, 1); }

        private string Enc2(int v) { return DIGITS.Substring(v / 62, 1) + DIGITS.Substring(v % 62, 1); }

        private string EncPower(int power)
        {
            int v = power + 1;
            return powerWidth == 1 ? Enc1(v) : Enc2(v);
        }

        private int Val(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            if (c >= 'a' && c <= 'z') return c - 'a' + 36;
            return -1;
        }

        /// <summary>Reads a 2-character number at pos, or -1.</summary>
        private int Dec2(string s, int pos)
        {
            if (pos + 2 > s.Length) return -1;
            int a = Val(s[pos]); int b = Val(s[pos + 1]);
            if (a < 0 || b < 0) return -1;
            return a * 62 + b;
        }

        /// <summary>Reads a power number at pos (returns power index, -1 for "nobody", -2 for bad text).</summary>
        private int DecPower(string s, int pos)
        {
            int v;
            if (powerWidth == 1)
            {
                if (pos >= s.Length) return -2;
                v = Val(s[pos]);
            }
            else v = Dec2(s, pos);
            if (v < 0 || v > map.powerCount) return -2;
            return v - 1;
        }

        /// <summary>Four-character checksum of a piece of text.</summary>
        public string Checksum(string text)
        {
            int h1 = 17; int h2 = 91;
            for (int i = 0; i < text.Length; i++)
            {
                int c = text[i];
                h1 = (h1 * 31 + c) % M1;
                h2 = (h2 * 17 + c + i % 13) % M2;
            }
            return Enc2(h1 % 3844) + Enc2(h2 % 3844);
        }

        private void SplitInto(string text, char sep)
        {
            partCount = 0;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == sep)
                {
                    if (partCount < parts.Length) { parts[partCount] = text.Substring(start, i - start); partCount++; }
                    else { partCount++; }
                    start = i + 1;
                }
            }
        }

        private int ParseYear(string s)
        {
            if (s.Length == 0 || s.Length > 6) return -1;
            int v = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return -1;
                v = v * 10 + (s[i] - '0');
            }
            return v;
        }

        // =====================================================================
        // Board position
        // =====================================================================

        /// <summary>The complete, self-contained game code for a position.</summary>
        public string EncodeState(BoardState s)
        {
            string body = MAGIC + "." + map.mapId + "." + map.hash + "." + EncodeBody(s);
            return body + "." + Checksum(body);
        }

        /// <summary>Just the position part (used inside history archives, which name the map once).</summary>
        public string EncodeBody(BoardState s)
        {
            string season = s.season == BoardState.SPRING ? "S" : (s.season == BoardState.FALL ? "F" : "W");
            string ph = s.phase == BoardState.PHASE_MOVE ? "M" : (s.phase == BoardState.PHASE_RETREAT ? "R" : (s.phase == BoardState.PHASE_ADJUST ? "A" : "X"));
            string r = season + s.year.ToString() + ph + ".";
            // Units are written in board order (not in the order they happen to be stored), so
            // the same position always produces exactly the same text.
            for (int pw = 0; pw < map.powerCount; pw++) groupText[pw] = "";
            for (int p = 0; p < map.provCount; p++)
            {
                int u = s.provUnit[p];
                if (u >= 0) groupText[s.unitPower[u]] += Enc2(s.unitLoc[u] * 2 + s.unitType[u]);
            }
            for (int pw = 0; pw < map.powerCount; pw++)
            {
                if (pw > 0) r += "-";
                r += groupText[pw];
            }
            r += ".";
            for (int p = 0; p < map.provCount; p++)
                if (map.provSc[p]) r += EncPower(s.owner[p]);
            r += ".";
            if (s.phase == BoardState.PHASE_RETREAT)
            {
                // dislodged units, lowest place first (again so the text is always the same)
                int lastLoc = -1;
                for (int k = 0; k < s.disCount; k++)
                {
                    int best = -1;
                    for (int d = 0; d < s.disCount; d++)
                        if (s.disLoc[d] > lastLoc && (best < 0 || s.disLoc[d] < s.disLoc[best])) best = d;
                    if (best < 0) break;
                    lastLoc = s.disLoc[best];
                    r += Enc2(s.disLoc[best] * 2 + s.disType[best]) + EncPower(s.disPower[best]) + Enc2(s.disFrom[best] + 1);
                }
                r += "-";
                for (int p = 0; p < map.provCount; p++) if (s.contested[p]) r += Enc2(p);
            }
            r += ".";
            if (s.phase == BoardState.PHASE_FINISHED) r += s.drawn ? "D" : ("W" + EncPower(s.winner));
            return r;
        }

        /// <summary>Which map does this code need? Returns "id.hash", or "" if the text is not a game code.</summary>
        public string PeekMap(string code)
        {
            if (code == null) return "";
            SplitInto(code.Trim(), '.');
            if (partCount < 4 || parts[0] != MAGIC) return "";
            return parts[1] + "." + parts[2];
        }

        /// <summary>Loads a full game code into s. Returns false (and sets error) if anything is wrong.</summary>
        public bool DecodeState(string code, BoardState s)
        {
            error = "";
            if (code == null) { error = "empty code"; return false; }
            string t = code.Trim();
            int lastDot = t.LastIndexOf('.');
            if (lastDot < 0) { error = "this is not a game code"; return false; }
            string body = t.Substring(0, lastDot);
            string check = t.Substring(lastDot + 1);
            SplitInto(body, '.');
            if (parts[0] != MAGIC)
            {
                bool otherVersion = parts[0].Length >= 2 && parts[0][0] == 'P' && parts[0][1] >= '0' && parts[0][1] <= '9';
                error = otherVersion ? "this code is from a different version of the game" : "this is not a game code";
                return false;
            }
            if (partCount != 8) { error = "the code is damaged or incomplete"; return false; }
            if (Checksum(body) != check) { error = "the code is damaged or incomplete (checksum does not match)"; return false; }
            if (parts[1] != map.mapId) { error = "this code is for the map '" + parts[1] + "', not '" + map.mapId + "'"; return false; }
            if (parts[2] != map.hash) { error = "this code is for a different version of the map '" + map.mapId + "'"; return false; }
            return DecodeSections(parts[3], parts[4], parts[5], parts[6], parts[7], s);
        }

        /// <summary>Loads a position written by <see cref="EncodeBody"/>.</summary>
        public bool DecodeBody(string body, BoardState s)
        {
            error = "";
            if (body == null) { error = "empty position"; return false; }
            SplitInto(body, '.');
            if (partCount != 5) { error = "position has the wrong number of sections"; return false; }
            return DecodeSections(parts[0], parts[1], parts[2], parts[3], parts[4], s);
        }

        private bool DecodeSections(string phase, string units, string owners, string retreats, string status, BoardState s)
        {
            s.Clear();
            // ---- phase ----
            if (phase.Length < 3) { error = "bad phase"; return false; }
            char sc = phase[0]; char pc = phase[phase.Length - 1];
            if (sc == 'S') s.season = BoardState.SPRING; else if (sc == 'F') s.season = BoardState.FALL; else if (sc == 'W') s.season = BoardState.WINTER;
            else { error = "bad season"; return false; }
            if (pc == 'M') s.phase = BoardState.PHASE_MOVE; else if (pc == 'R') s.phase = BoardState.PHASE_RETREAT;
            else if (pc == 'A') s.phase = BoardState.PHASE_ADJUST; else if (pc == 'X') s.phase = BoardState.PHASE_FINISHED;
            else { error = "bad phase"; return false; }
            s.year = ParseYear(phase.Substring(1, phase.Length - 2));
            if (s.year < 0) { error = "bad year"; return false; }

            // ---- units ----
            int pw = 0;
            int i = 0;
            while (i < units.Length)
            {
                if (units[i] == '-') { pw++; i++; continue; }
                int v = Dec2(units, i);
                if (v < 0 || pw >= map.powerCount) { error = "bad unit list"; return false; }
                if (s.AddUnit(map, v % 2, pw, v / 2) < 0) { error = "unit in an impossible place"; return false; }
                i += 2;
            }

            // ---- owners ----
            int pos = 0;
            for (int p = 0; p < map.provCount; p++)
            {
                if (!map.provSc[p]) continue;
                int o = DecPower(owners, pos);
                if (o == -2) { error = "bad owner list"; return false; }
                s.owner[p] = o;
                pos += powerWidth;
            }
            if (pos != owners.Length) { error = "bad owner list"; return false; }

            // ---- retreats ----
            if (s.phase == BoardState.PHASE_RETREAT)
            {
                int dash = retreats.IndexOf('-');
                if (dash < 0) { error = "bad retreat list"; return false; }
                int step = 4 + powerWidth;
                i = 0;
                while (i < dash)
                {
                    int v = Dec2(retreats, i);
                    int dp = DecPower(retreats, i + 2);
                    int from = Dec2(retreats, i + 2 + powerWidth);
                    if (v < 0 || dp < 0 || from < 0 || from > map.provCount || i + step > dash) { error = "bad retreat list"; return false; }
                    if (!map.CanStand(v % 2, v / 2)) { error = "bad retreat list"; return false; }
                    int d = s.disCount;
                    if (d >= s.disLoc.Length) { error = "bad retreat list"; return false; }
                    s.disType[d] = v % 2; s.disLoc[d] = v / 2; s.disPower[d] = dp; s.disFrom[d] = from - 1;
                    s.disCount = d + 1;
                    i += step;
                }
                i = dash + 1;
                while (i < retreats.Length)
                {
                    int p = Dec2(retreats, i);
                    if (p < 0 || p >= map.provCount) { error = "bad standoff list"; return false; }
                    s.contested[p] = true;
                    i += 2;
                }
            }
            else if (retreats.Length != 0) { error = "unexpected retreat data"; return false; }

            // ---- status ----
            if (s.phase == BoardState.PHASE_FINISHED)
            {
                if (status == "D") s.drawn = true;
                else if (status.Length == 1 + powerWidth && status[0] == 'W')
                {
                    int w = DecPower(status, 1);
                    if (w < 0) { error = "bad winner"; return false; }
                    s.winner = w;
                }
                else { error = "bad game result"; return false; }
            }
            else if (status.Length != 0) { error = "unexpected game result"; return false; }
            return true;
        }

        // =====================================================================
        // Orders (for history, and for sending a player's orders over the network)
        // =====================================================================

        /// <summary>
        /// The orders currently entered in the adjudicator for the phase the board is in.
        /// Pass a power to get only that power's orders, or -1 for everybody's.
        /// Holds, disbanding retreats and "no build" are the default and are left out.
        /// </summary>
        public string EncodeOrders(BoardState s, Adjudicator adj, int power)
        {
            string r = "";
            if (s.phase == BoardState.PHASE_MOVE)
            {
                for (int u = 0; u < s.unitCount; u++)
                {
                    if (power >= 0 && s.unitPower[u] != power) continue;
                    int t = adj.oType[u];
                    if (t == Adjudicator.HOLD) continue;
                    int a = adj.oA[u]; int b = adj.oB[u];
                    string who = Enc2(s.unitLoc[u]);
                    if (t == Adjudicator.MOVE) { if (a >= 0) r += who + (adj.oVia[u] ? "v" : "m") + Enc2(a); }
                    else if (t == Adjudicator.SUPPORT_HOLD) { if (a >= 0) r += who + "s" + Enc2(a); }
                    else if (t == Adjudicator.SUPPORT_MOVE) { if (a >= 0 && b >= 0) r += who + "t" + Enc2(a) + Enc2(b); }
                    else if (t == Adjudicator.CONVOY) { if (a >= 0 && b >= 0) r += who + "c" + Enc2(a) + Enc2(b); }
                }
            }
            else if (s.phase == BoardState.PHASE_RETREAT)
            {
                for (int d = 0; d < s.disCount; d++)
                {
                    if (power >= 0 && s.disPower[d] != power) continue;
                    if (adj.retDest[d] >= 0) r += Enc2(s.disLoc[d]) + Enc2(adj.retDest[d]);
                }
            }
            else if (s.phase == BoardState.PHASE_ADJUST)
            {
                for (int i = 0; i < adj.adjCount; i++)
                {
                    if (power >= 0 && adj.adjPower[i] != power) continue;
                    int k = adj.adjKind[i];
                    string kc = k == Adjudicator.ADJ_BUILD_ARMY ? "a" : (k == Adjudicator.ADJ_BUILD_FLEET ? "f" : (k == Adjudicator.ADJ_DISBAND ? "d" : "w"));
                    int loc = adj.adjLoc[i];
                    if (k != Adjudicator.ADJ_WAIVE && loc < 0) continue;
                    r += kc + Enc2(loc < 0 ? 0 : loc) + EncPower(adj.adjPower[i]);
                }
            }
            return r;
        }

        /// <summary>
        /// Enters encoded orders into the adjudicator for the phase the board is in (on top of
        /// whatever is already entered; call adj.ClearOrders() first for a clean slate).
        /// If onlyPower is 0 or more, orders for other powers' units are ignored - this is how
        /// one player's submission is accepted without letting it touch anybody else's units.
        /// </summary>
        public bool DecodeOrders(string text, BoardState s, Adjudicator adj, int onlyPower)
        {
            error = "";
            if (text == null) return true;
            int i = 0; int n = text.Length;
            if (s.phase == BoardState.PHASE_MOVE)
            {
                while (i < n)
                {
                    int loc = Dec2(text, i);
                    if (loc < 0 || loc >= map.locCount || i + 3 > n) { error = "bad orders"; return false; }
                    char k = text[i + 2];
                    int need = (k == 'm' || k == 'v' || k == 's') ? 2 : ((k == 't' || k == 'c') ? 4 : -1);
                    if (need < 0 || i + 3 + need > n) { error = "bad orders"; return false; }
                    int a = Dec2(text, i + 3);
                    int b = need == 4 ? Dec2(text, i + 5) : 0;
                    if (a < 0 || b < 0) { error = "bad orders"; return false; }
                    i += 3 + need;
                    int u = s.provUnit[map.locProv[loc]];
                    if (u < 0 || s.unitLoc[u] != loc) continue;               // no such unit any more
                    if (onlyPower >= 0 && s.unitPower[u] != onlyPower) continue; // not yours
                    if (k == 'm' || k == 'v') { if (a < map.locCount) adj.SetMove(u, a, k == 'v'); }
                    else if (k == 's') { if (a < map.provCount) adj.SetSupportHold(u, a); }
                    else if (k == 't') { if (a < map.provCount && b < map.locCount) adj.SetSupportMove(u, a, b); }
                    else { if (a < map.provCount && b < map.provCount) adj.SetConvoy(u, a, b); }
                }
            }
            else if (s.phase == BoardState.PHASE_RETREAT)
            {
                while (i < n)
                {
                    int loc = Dec2(text, i); int dst = Dec2(text, i + 2);
                    if (loc < 0 || dst < 0) { error = "bad orders"; return false; }
                    i += 4;
                    for (int d = 0; d < s.disCount; d++)
                    {
                        if (s.disLoc[d] != loc) continue;
                        if (onlyPower >= 0 && s.disPower[d] != onlyPower) continue;
                        if (dst < map.locCount) adj.SetRetreat(d, dst);
                    }
                }
            }
            else if (s.phase == BoardState.PHASE_ADJUST)
            {
                int step = 3 + powerWidth;
                while (i < n)
                {
                    if (i + step > n) { error = "bad orders"; return false; }
                    char k = text[i];
                    int loc = Dec2(text, i + 1);
                    int pw = DecPower(text, i + 3);
                    if (loc < 0 || pw < 0) { error = "bad orders"; return false; }
                    i += step;
                    if (onlyPower >= 0 && pw != onlyPower) continue;
                    if (k == 'a') { if (loc < map.locCount) adj.AddAdjust(pw, Adjudicator.ADJ_BUILD_ARMY, loc); }
                    else if (k == 'f') { if (loc < map.locCount) adj.AddAdjust(pw, Adjudicator.ADJ_BUILD_FLEET, loc); }
                    else if (k == 'd') { if (loc < map.locCount) adj.AddAdjust(pw, Adjudicator.ADJ_DISBAND, loc); }
                    else if (k == 'w') adj.AddAdjust(pw, Adjudicator.ADJ_WAIVE, -1);
                    else { error = "bad orders"; return false; }
                }
            }
            return true;
        }
    }
}
