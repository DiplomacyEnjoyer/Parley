// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// "Purse and Dagger": the Renaissance ruleset. Money, bribes, walled cities and
    /// sieges, rebellions, assassins, famine and plague, on top of the shared referee.
    ///
    /// The mechanics follow the rules that the e-mail judges published for their
    /// Renaissance-Italy game; every place where those rules were unclear, or where this
    /// engine deliberately differs, is listed in docs/machiavelli-rulings.md. No rulebook
    /// text and no code from other implementations is used.
    ///
    /// A year: three campaigns (each a sealed sheet of money orders, expenses and unit
    /// orders, then retreats), then income and an adjustment phase in which every unit
    /// must be paid for. The game starts with that adjustment phase.
    ///
    /// When a campaign's sheets are opened, things happen in this order:
    ///   1. repayments to the bank; gifts between players; new loans; the bank collects
    ///   2. expenses: relief, pacifying, bribes against counter-bribes, rebellions, assassins
    ///   3. unit orders are read (so a unit bought in step 2 obeys its buyer) and resolved
    ///   4. sieges, rebellions put down or liberated, control, famine deaths, plague
    ///   5. retreats if any; then who is out, who holds which home country, victory
    ///
    /// What it keeps in the board's spare room:
    ///   unitFlag   the unit's class (CLASS_*)
    ///   provFlag   F_FAMINE, F_REBEL, F_SIEGE (on a province: the unit there has besieged
    ///              the city for one campaign; on a city entry only F_REBEL is used)
    ///   owner      who controls each province and each walled city
    ///   ext        options, then per power its treasury, marks and loans, then who holds
    ///              each home country, the assassination tokens, and dice kept for later
    /// </summary>
    public class PurseRules : UdonSharpBehaviour
    {
        public const int OPT_FAMINE = 1;
        public const int OPT_PLAGUE = 2;
        public const int OPT_LOANS = 4;
        public const int OPT_ASSASSINS = 8;
        public const int OPT_SPECIAL = 16;    // special unit classes may be built
        public const int OPT_ADJACENCY = 32;  // a bribe needs one of your units next to its target
        public const int OPT_VARINCOME = 64;  // variable income is rolled (off: a fixed middle value)

        public const int F_FAMINE = 1;
        public const int F_REBEL = 2;
        public const int F_SIEGE = 4;

        public const int CLASS_NORMAL = 0;
        public const int CLASS_MILITIA = 1;   // strength 1, costs double, bribes cost double
        public const int CLASS_ELITE = 2;     // strength 2, costs double
        public const int CLASS_PRO = 3;       // strength 2, costs triple, bribes cost double

        public const int EX_RELIEF = 1;       // target: province
        public const int EX_PACIFY = 2;       // target: province
        public const int EX_COUNTER = 3;      // target: place of a unit
        public const int EX_DISBAND = 4;      // target: place of a unit
        public const int EX_BUY = 5;          // target: place of a unit
        public const int EX_AUTONOMOUS = 6;   // target: place of a garrison
        public const int EX_REBEL = 7;        // target: province
        public const int EX_ASSASSINATE = 8;  // target: power

        public const int UNIT_COST = 3;
        public const int LOAN_LIMIT = 25;
        public const int MAX_LOANS = 3;
        public const int MAX_EXPENSES = 4;
        private const int PB = 2 + 3 * MAX_LOANS; // numbers kept per power
        private const int P_FLAGS = 1;
        private const int PF_BARRED = 1;      // defaulted on a loan: no more credit
        private const int PF_OUT = 2;         // eliminated
        private const string DIGITS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const string EX_LETTERS = "?fpcdbgra";

        public MapData map;
        public Adjudicator adj;
        public GameCodec codec;
        public Dice dice;
        public string error = "";

        /// <summary>The power that owns garrisons belonging to nobody (-1 if the board has none).</summary>
        public int autoPower;
        /// <summary>Options a new game starts with (OPT_*); change before the game starts.</summary>
        public int startOptions;
        /// <summary>Home countries needed to win, besides the board's number of cities.</summary>
        public int victoryHomes;

        private int np;
        // where things are in BoardState.ext
        private int xHome; private int xChit; private int xDie;
        public int extSize;
        // tables from the board file
        private int[] famTable; private int[] plagueTable;
        public int incCount; public int[] incPower; public int[] incCity; public int[] incDice; public int[] incValue;
        private int straitCount; private int[] straitGuard; private int[] straitA; private int[] straitB;

        // ---- the sheets of the phase being collected ----
        private string[] sheetText;
        // money orders (filled when a sheet is read for real)
        private int[] payBank; private int[] give; private int[] chitGive; private int[] borrow;
        public int[] exCount; public int[] exType; public int[] exTarget; public int[] exAmt; public bool[] exOk;
        private int adCount; private int[] adPower; private int[] adBuild; private int[] adType; private int[] adClass; private int[] adLoc;
        /// <summary>Per power: its ruler was assassinated in the campaign being resolved.</summary>
        public bool[] killed;

        // scratch
        private int[] best; private int[] bestCount; private int[] counter; private bool[] mark; private bool[] heldBy; private bool[] newSiege;
        private int[] tmpPow; private string[] items;

        // =====================================================================
        // Setting up
        // =====================================================================

        public bool Init(MapData m, Adjudicator a, GameCodec c, Dice d)
        {
            map = m; adj = a; codec = c; dice = d; error = "";
            np = m.powerCount;
            adj.autoAdvance = false; // this ruleset keeps its own calendar
            autoPower = -1; victoryHomes = 2;
            famTable = new int[121]; plagueTable = new int[121];
            for (int i = 0; i < 121; i++) { famTable[i] = -1; plagueTable[i] = -1; }
            incPower = new int[m.extLineCount + 1]; incCity = new int[m.extLineCount + 1]; incDice = new int[m.extLineCount + 1]; incValue = new int[(m.extLineCount + 1) * 6];
            straitGuard = new int[m.extLineCount + 1]; straitA = new int[m.extLineCount + 1]; straitB = new int[m.extLineCount + 1];
            incCount = 0; straitCount = 0;
            for (int i = 0; i < m.extLineCount; i++)
            {
                string[] w = m.extLine[i].Split(' ');
                string k = w[0];
                if (k == "autonomous" && w.Length >= 2) autoPower = m.FindPower(w[1]);
                else if (k == "homes" && w.Length >= 2) victoryHomes = m.ParseInt(w[1], 2);
                else if ((k == "famine" || k == "plague") && w.Length >= 3)
                {
                    int row = m.ParseInt(w[1], -1);
                    if (row < 2 || row > 12) { error = "bad table row: " + m.extLine[i]; return false; }
                    for (int j = 2; j < w.Length && j < 13; j++)
                    {
                        if (w[j] == "-") continue;
                        int pv = m.FindProv(w[j]);
                        if (pv < 0 || pv >= m.landCount) { error = "unknown province in a table: " + w[j]; return false; }
                        if (k == "famine") famTable[(row - 2) * 11 + j - 2] = pv; else plagueTable[(row - 2) * 11 + j - 2] = pv;
                    }
                }
                else if (k == "income" && w.Length >= 9)
                {
                    int e = incCount;
                    incPower[e] = -1; incCity[e] = -1;
                    if (w[1].Length > 5 && w[1].Substring(0, 5) == "city:") incCity[e] = m.FindProv(w[1].Substring(5));
                    else incPower[e] = m.FindPower(w[1]);
                    if (incPower[e] < 0 && incCity[e] < 0) { error = "unknown power or city in an income line: " + w[1]; return false; }
                    incDice[e] = m.ParseInt(w[2], 1);
                    if (incDice[e] < 1 || incDice[e] > 2) { error = "an income line rolls one or two dice"; return false; }
                    for (int j = 0; j < 6; j++) incValue[e * 6 + j] = m.ParseInt(w[3 + j], 0);
                    incCount++;
                }
                else if (k == "strait" && w.Length >= 4)
                {
                    int g = m.FindProv(w[1]); int sa = m.FindProv(w[2]); int sb = m.FindProv(w[3]);
                    if (g < 0 || sa < 0 || sb < 0) { error = "unknown province in a strait line"; return false; }
                    straitGuard[straitCount] = g; straitA[straitCount] = sa; straitB[straitCount] = sb; straitCount++;
                }
            }
            startOptions = 0;
            string[] opt = m.rulesArgs.Split(' ');
            for (int i = 0; i < opt.Length; i++)
            {
                if (opt[i] == "famine") startOptions |= OPT_FAMINE;
                else if (opt[i] == "plague") startOptions |= OPT_PLAGUE;
                else if (opt[i] == "loans") startOptions |= OPT_LOANS;
                else if (opt[i] == "assassins") startOptions |= OPT_ASSASSINS;
                else if (opt[i] == "special") startOptions |= OPT_SPECIAL;
                else if (opt[i] == "adjacency") startOptions |= OPT_ADJACENCY;
                else if (opt[i] == "varincome") startOptions |= OPT_VARINCOME;
            }
            xHome = 2 + np * PB; xChit = xHome + np; xDie = xChit + np * np;
            extSize = xDie + incCount * 2;

            sheetText = new string[np];
            payBank = new int[np]; give = new int[np * np]; chitGive = new int[np * np]; borrow = new int[np * 2];
            exCount = new int[np]; exType = new int[np * MAX_EXPENSES]; exTarget = new int[np * MAX_EXPENSES]; exAmt = new int[np * MAX_EXPENSES]; exOk = new bool[np * MAX_EXPENSES];
            int cap = m.provCount * 2 + 8;
            adPower = new int[cap]; adBuild = new int[cap]; adType = new int[cap]; adClass = new int[cap]; adLoc = new int[cap];
            killed = new bool[np];
            best = new int[m.locCount]; bestCount = new int[m.locCount]; counter = new int[m.locCount];
            mark = new bool[m.provCount]; heldBy = new bool[m.provCount]; newSiege = new bool[m.provCount];
            tmpPow = new int[np];
            ClearOrders();
            return true;
        }

        public string Title() { return "Purse and Dagger"; }

        /// <summary>Makes room in a board for this ruleset's numbers.</summary>
        public void Attach(BoardState s) { if (s.extCount != extSize) s.SetExtSize(extSize); }

        /// <summary>
        /// The opening position: the board's units and owners, one assassination token
        /// against every rival, the first income (without dice: nobody has sealed
        /// anything yet), and the game opens in the adjustment phase before the first
        /// spring, where every power pays for the units it wants to keep.
        /// </summary>
        public void SetupStart(BoardState s)
        {
            Attach(s);
            s.SetupStart(map);
            s.ext[0] = startOptions;
            for (int p = 0; p < np; p++)
            {
                bool hasHome = false;
                for (int q = 0; q < map.landCount; q++) if (map.provHome[q] == p) hasHome = true;
                s.ext[xHome + p] = hasHome && p != autoPower ? p + 1 : 0;
                if (!Real(p) || !hasHome) { if (p != autoPower) s.ext[2 + p * PB + P_FLAGS] = PF_OUT; continue; }
            }
            for (int p = 0; p < np; p++)
                for (int q = 0; q < np; q++)
                    if (p != q && Alive(s, p) && Alive(s, q)) s.ext[xChit + p * np + q] = 1;
            UpdateControl(s);
            s.year = map.startYear - 1; s.season = map.seasonCount; s.phase = BoardState.PHASE_ADJUST;
            Income(s, null);
        }

        // =====================================================================
        // Reading the board
        // =====================================================================

        private bool Real(int p) { return p >= 0 && p < np && p != autoPower; }

        public bool Playable(int p) { return Real(p); }

        public bool Alive(BoardState s, int p) { return Real(p) && (s.ext[2 + p * PB + P_FLAGS] & PF_OUT) == 0; }

        public bool IsOut(BoardState s, int p) { return !Alive(s, p); }

        public int Options(BoardState s) { return s.ext[0]; }
        public bool Has(BoardState s, int option) { return (s.ext[0] & option) != 0; }

        public int Treasury(BoardState s, int p) { return s.ext[2 + p * PB]; }
        /// <summary>For scenario makers and tests: sets a treasury outright.</summary>
        public void SetTreasury(BoardState s, int p, int ducats) { s.ext[2 + p * PB] = ducats < 0 ? 0 : ducats; }
        /// <summary>For the lobby and tests: sets the options (OPT_*) of a game.</summary>
        public void SetOptions(BoardState s, int options) { s.ext[0] = options; }
        private void AddMoney(BoardState s, int p, int amount) { s.ext[2 + p * PB] = s.ext[2 + p * PB] + amount; }
        public bool Barred(BoardState s, int p) { return (s.ext[2 + p * PB + P_FLAGS] & PF_BARRED) != 0; }

        public int LoanPrincipal(BoardState s, int p, int i) { return s.ext[2 + p * PB + 2 + i * 3]; }
        public int LoanInterest(BoardState s, int p, int i) { return s.ext[2 + p * PB + 3 + i * 3]; }
        /// <summary>The campaign in which loan i falls due (see <see cref="CampaignNumber"/>), or -1 if there is no such loan.</summary>
        public int LoanDue(BoardState s, int p, int i) { return s.ext[2 + p * PB + 4 + i * 3] - 1; }

        /// <summary>What this power still owes the bank, interest included.</summary>
        public int Debt(BoardState s, int p)
        {
            int t = 0;
            for (int i = 0; i < MAX_LOANS; i++) t += LoanPrincipal(s, p, i) + LoanInterest(s, p, i);
            return t;
        }

        private int PrincipalOwed(BoardState s, int p)
        {
            int t = 0;
            for (int i = 0; i < MAX_LOANS; i++) t += LoanPrincipal(s, p, i);
            return t;
        }

        /// <summary>Campaigns counted from the game's first spring (0, 1, 2 = the first year's three).</summary>
        public int CampaignNumber(BoardState s)
        {
            int season = s.season < map.seasonCount ? s.season : map.seasonCount - 1;
            return (s.year - map.startYear) * map.seasonCount + season;
        }

        /// <summary>The power that holds home country h (the country that power h started with), or -1.</summary>
        public int HomeHolder(BoardState s, int h) { return s.ext[xHome + h] - 1; }

        public int HomesHeld(BoardState s, int p)
        {
            int c = 0;
            for (int h = 0; h < np; h++) if (HomeHolder(s, h) == p) c++;
            return c;
        }

        /// <summary>How many assassination tokens "holder" has against "target".</summary>
        public int Chits(BoardState s, int holder, int target) { return s.ext[xChit + holder * np + target]; }

        /// <summary>Who controls the city of land province q (-1: nobody, or there is no city).</summary>
        public int CityHolder(BoardState s, int q)
        {
            if (map.cityKind[q] == MapData.CITY_NONE) return -1;
            int c = map.provCity[q];
            return c >= 0 ? s.owner[c] : s.owner[q];
        }

        public int CitiesHeld(BoardState s, int p)
        {
            int n = 0;
            for (int q = 0; q < map.landCount; q++) if (CityHolder(s, q) == p) n++;
            return n;
        }

        public bool CityRebels(BoardState s, int q)
        {
            int c = map.provCity[q];
            if (c >= 0) return (s.provFlag[c] & F_REBEL) != 0;
            return map.cityKind[q] != MapData.CITY_NONE && (s.provFlag[q] & F_REBEL) != 0;
        }

        public int ClassStrength(int unitClass) { return unitClass == CLASS_ELITE || unitClass == CLASS_PRO ? 2 : 1; }

        /// <summary>What a unit of this class costs to build, and again each year to keep.</summary>
        public int ClassCost(int unitClass)
        {
            if (unitClass == CLASS_PRO) return UNIT_COST * 3;
            if (unitClass == CLASS_NORMAL) return UNIT_COST;
            return UNIT_COST * 2;
        }

        /// <summary>The unit standing at this place (a province, a coast or a city entry), or -1.</summary>
        public int UnitAt(BoardState s, int loc)
        {
            if (loc < 0 || loc >= map.locCount) return -1;
            return s.provUnit[map.locProv[loc]];
        }

        /// <summary>The land province a place belongs to (for a city entry: the province around it).</summary>
        public int LandOf(int loc)
        {
            int p = map.locProv[loc];
            return map.cityProv[p] >= 0 ? map.cityProv[p] : p;
        }

        /// <summary>
        /// The least a bribe of this kind against the unit at loc must be (before any
        /// counter-bribe), or 0 if no such bribe is possible for this power.
        /// </summary>
        public int MinBribe(BoardState s, int power, int type, int loc)
        {
            int u = UnitAt(s, loc);
            if (u < 0) return 0;
            int owner = s.unitPower[u];
            bool garrison = s.unitType[u] == MapData.GARRISON;
            int cost = 0;
            if (type == EX_DISBAND)
            {
                if (owner == power) return 0;
                cost = owner == autoPower ? 6 : 12;
            }
            else if (type == EX_BUY)
            {
                if (owner == power) return 0;
                if (s.unitFlag[u] != CLASS_NORMAL && HasSpecial(s, power)) return 0;
                cost = owner == autoPower ? 9 : 18;
            }
            else if (type == EX_AUTONOMOUS)
            {
                if (!garrison || owner == power || owner == autoPower || autoPower < 0) return 0;
                cost = 9;
            }
            else return 0;
            if (garrison && map.cityIncome[LandOf(s.unitLoc[u])] > 1) cost *= 2;
            if (s.unitFlag[u] == CLASS_MILITIA || s.unitFlag[u] == CLASS_PRO) cost *= 2;
            return cost;
        }

        public bool HasSpecial(BoardState s, int power)
        {
            for (int u = 0; u < s.unitCount; u++) if (s.unitPower[u] == power && s.unitFlag[u] != CLASS_NORMAL) return true;
            return false;
        }

        /// <summary>What starting a rebellion in this province costs this power, or 0 if it cannot.</summary>
        public int RebellionCost(BoardState s, int power, int q)
        {
            if (q < 0 || q >= map.landCount || map.provType[q] == MapData.SEA) return 0;
            int holder = s.owner[q];
            if (!Real(holder) || holder == power || !Alive(s, holder)) return 0;
            if ((s.provFlag[q] & F_REBEL) != 0) return 0;
            int u = s.provUnit[q];
            if (u >= 0 && s.unitPower[u] != holder) return 0;             // somebody else's troops would put it down at once
            if (map.provSingle[q] && u >= 0) return 0;                    // one space, and it is taken
            int h = map.provHome[q];
            return h >= 0 && HomeHolder(s, h) == holder ? 15 : 9;
        }

        /// <summary>
        /// Does this power have a unit in or next to the land province q? (What a bribe
        /// needs when the adjacency option is on.) Garrisons count only if withGarrisons.
        /// </summary>
        public bool Near(BoardState s, int power, int q, bool withGarrisons)
        {
            for (int u = 0; u < s.unitCount; u++)
            {
                if (s.unitPower[u] != power) continue;
                if (s.unitType[u] == MapData.GARRISON && !withGarrisons) continue;
                int at = LandOf(s.unitLoc[u]);
                if (at == q) return true;
                int st = map.anyStart[at]; int c = map.anyCount[at];
                for (int i = 0; i < c; i++) if (map.anyAdj[st + i] == q) return true;
            }
            return false;
        }

        public bool NeedsOrders(BoardState s, int power)
        {
            if (!Alive(s, power)) return false;
            if (s.phase == BoardState.PHASE_MOVE) return true;            // even without units there is money to spend
            if (s.phase == BoardState.PHASE_RETREAT) return s.CountDislodged(power) > 0;
            if (s.phase == BoardState.PHASE_ADJUST) return s.CountUnits(power) > 0 || Treasury(s, power) >= UNIT_COST;
            return false;
        }

        /// <summary>Dice are rolled only when a campaign's sheets are opened, when every power has a turn at stake.</summary>
        public bool UsesDice(BoardState s)
        {
            return s.phase == BoardState.PHASE_MOVE && (s.ext[0] & (OPT_FAMINE | OPT_PLAGUE | OPT_ASSASSINS | OPT_VARINCOME | OPT_LOANS)) != 0;
        }

        // =====================================================================
        // Sheets
        //
        //   sheet   = unit orders (the codec's format) [ "!" item { "," item } ]
        //   campaign items   P<n>           pay n ducats to the bank
        //                    G<power><n>    give n ducats to a power
        //                    T<power><to>   hand my assassination token against <power> to <to>
        //                    B<1|2><n>      borrow n ducats for one or two years
        //                    E<kind><target><n>   an expense of n ducats (at most four, in order);
        //                                   kind: f relief, p pacify, c counter-bribe, d disband, b buy,
        //                                   g garrison to autonomous, r rebellion, a assassinate
        //   adjustment items D<place>       disband the unit there (do not pay for it)
        //                    N<type><class><place>   build
        //   <power> is one character, <place>/<target> two (as in game codes); numbers are decimal.
        //   Everybody's sheets together (for the archive) are joined with ".".
        // =====================================================================

        public void ClearOrders()
        {
            adj.ClearOrders();
            for (int p = 0; p < np; p++) sheetText[p] = "";
            ClearMoney();
        }

        private void ClearMoney()
        {
            for (int p = 0; p < np; p++) { payBank[p] = 0; exCount[p] = 0; killed[p] = false; borrow[p * 2] = 0; borrow[p * 2 + 1] = 0; }
            for (int i = 0; i < np * np; i++) { give[i] = 0; chitGive[i] = 0; }
            adCount = 0;
        }

        public bool AddOrders(string text, BoardState s, int power)
        {
            error = "";
            string t = text == null ? "" : text;
            if (power >= 0)
            {
                if (power >= np) { error = "no such power"; return false; }
                if (!ReadSheet(t, s, power, false)) return false;
                sheetText[power] = t;
                return true;
            }
            string[] each = t.Split('.');
            if (t != "" && each.Length != np) { error = "bad orders"; return false; }
            for (int p = 0; p < np && t != ""; p++)
            {
                if (!ReadSheet(each[p], s, p, false)) return false;
                sheetText[p] = each[p];
            }
            return true;
        }

        public string OrdersText(BoardState s, int power)
        {
            if (power >= 0) return sheetText[power];
            string r = ""; bool any = false;
            for (int p = 0; p < np; p++) { if (p > 0) r += "."; r += sheetText[p]; if (sheetText[p] != "") any = true; }
            return any ? r : "";
        }

        private int Val(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            if (c >= 'a' && c <= 'z') return c - 'a' + 36;
            return -1;
        }

        private int Two(string t, int pos)
        {
            if (pos + 2 > t.Length) return -1;
            int a = Val(t[pos]); int b = Val(t[pos + 1]);
            if (a < 0 || b < 0) return -1;
            return a * 62 + b;
        }

        private int Amount(string t, int pos)
        {
            if (pos >= t.Length || t.Length - pos > 4) return -1;
            return map.ParseInt(t.Substring(pos), -1);
        }

        /// <summary>The character that stands for a power on a sheet.</summary>
        public string PowerChar(int power) { return DIGITS.Substring(power, 1); }
        /// <summary>The two characters that stand for a place on a sheet.</summary>
        public string PlaceChars(int loc) { return DIGITS.Substring(loc / 62, 1) + DIGITS.Substring(loc % 62, 1); }
        /// <summary>The sheet text of one expense.</summary>
        public string ExpenseItem(int type, int target, int amount)
        {
            string t = type == EX_ASSASSINATE ? PowerChar(target) + "0" : PlaceChars(target);
            return "E" + EX_LETTERS.Substring(type, 1) + t + amount.ToString();
        }

        // Reads one power's sheet. With apply=false it only checks that the text is
        // well-formed; with apply=true the money orders are entered (unit orders are
        // entered separately, after bribes: see ResolveCampaign).
        private bool ReadSheet(string text, BoardState s, int p, bool apply)
        {
            int bang = text.IndexOf('!');
            string units = bang < 0 ? text : text.Substring(0, bang);
            string money = bang < 0 ? "" : text.Substring(bang + 1);
            if (!apply && !codec.DecodeOrders(units, s, adj, p)) { error = codec.error; return false; }
            if (money == "") return true;
            if (s.phase == BoardState.PHASE_RETREAT) { error = "bad orders"; return false; }
            items = money.Split(',');
            for (int i = 0; i < items.Length; i++)
            {
                string it = items[i];
                if (it.Length < 2) { error = "bad orders"; return false; }
                char k = it[0];
                if (s.phase == BoardState.PHASE_MOVE)
                {
                    if (k == 'P')
                    {
                        int n = Amount(it, 1);
                        if (n < 0) { error = "bad orders"; return false; }
                        if (apply) payBank[p] = n;
                    }
                    else if (k == 'G' || k == 'T')
                    {
                        int q = Val(it[1]);
                        int n = k == 'G' ? Amount(it, 2) : (it.Length == 3 ? Val(it[2]) : -1);
                        if (q < 0 || q >= np || n < 0 || (k == 'T' && n >= np)) { error = "bad orders"; return false; }
                        if (apply && k == 'G') give[p * np + q] = n;
                        if (apply && k == 'T') chitGive[p * np + q] = n + 1;
                    }
                    else if (k == 'B')
                    {
                        int years = it[1] - '0'; int n = Amount(it, 2);
                        if (years < 1 || years > 2 || n < 0) { error = "bad orders"; return false; }
                        if (apply) borrow[p * 2 + years - 1] = n;
                    }
                    else if (k == 'E')
                    {
                        int type = EX_LETTERS.IndexOf(it[1]);
                        int target = type == EX_ASSASSINATE ? (it.Length > 3 ? Val(it[2]) : -1) : Two(it, 2);
                        int n = Amount(it, 4);
                        if (type < 1 || target < 0 || n < 0) { error = "bad orders"; return false; }
                        if (type == EX_ASSASSINATE) { if (target >= np) { error = "bad orders"; return false; } }
                        else if (target >= map.locCount) { error = "bad orders"; return false; }
                        if (apply && exCount[p] < MAX_EXPENSES)
                        {
                            int e = p * MAX_EXPENSES + exCount[p];
                            exType[e] = type; exTarget[e] = target; exAmt[e] = n; exOk[e] = false;
                            exCount[p] = exCount[p] + 1;
                        }
                    }
                    else { error = "bad orders"; return false; }
                }
                else
                {
                    if (k == 'D')
                    {
                        int loc = Two(it, 1);
                        if (loc < 0 || loc >= map.locCount || it.Length != 3) { error = "bad orders"; return false; }
                        if (apply) AddAdjust(p, 0, 0, 0, loc);
                    }
                    else if (k == 'N')
                    {
                        int type = it.Length == 5 ? it[1] - '0' : -1; int cls = it.Length == 5 ? it[2] - '0' : -1; int loc = Two(it, 3);
                        if (type < 0 || type >= map.unitTypeCount || cls < 0 || cls > CLASS_PRO || loc < 0 || loc >= map.locCount) { error = "bad orders"; return false; }
                        if (apply) AddAdjust(p, 1, type, cls, loc);
                    }
                    else { error = "bad orders"; return false; }
                }
            }
            return true;
        }

        private void AddAdjust(int p, int build, int type, int cls, int loc)
        {
            if (adCount >= adPower.Length) return;
            adPower[adCount] = p; adBuild[adCount] = build; adType[adCount] = type; adClass[adCount] = cls; adLoc[adCount] = loc;
            adCount++;
        }

        // =====================================================================
        // Resolving a phase
        // =====================================================================

        public void Resolve(BoardState s, Announcement ann, int[] status)
        {
            if (s.phase == BoardState.PHASE_MOVE) ResolveCampaign(s, ann, status);
            else if (s.phase == BoardState.PHASE_RETREAT)
            {
                adj.ClearOrders();
                for (int p = 0; p < np; p++) codec.DecodeOrders(sheetText[p], s, adj, p);
                ann.Capture(s, adj, status);
                adj.ResolveRetreats(s);
                ann.Finish(adj);
                EndCampaign(s, ann);
                ann.Group();
            }
            else if (s.phase == BoardState.PHASE_ADJUST) ResolveAdjust(s, ann, status);
            for (int p = 0; p < np; p++) sheetText[p] = "";
        }

        private string Name(int p) { return p >= 0 && p < np ? map.powerName[p] : "Nobody"; }
        private string Place(int loc) { return map.LocName(loc); }
        private string UnitName(BoardState s, int u)
        {
            int t = s.unitType[u];
            return (t == MapData.FLEET ? "fleet" : (t == MapData.GARRISON ? "garrison" : "army")) + " in " + map.provName[map.locProv[s.unitLoc[u]]];
        }

        private void ResolveCampaign(BoardState s, Announcement ann, int[] status)
        {
            // ---- 1 and 2: money and expenses. Notes are collected and added after the unit orders. ----
            ClearMoney();
            for (int p = 0; p < np; p++) if (Alive(s, p)) ReadSheet(sheetText[p], s, p, true);
            noteCount = 0;
            Bank(s);
            Expenses(s);
            UpdateControl(s); // a bought unit brings its province with it before it marches off

            // ---- 3: unit orders, read now so that a unit obeys whoever owns it after the bribes ----
            adj.ClearOrders();
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p) || killed[p]) continue;
                string text = sheetText[p];
                int bang = text.IndexOf('!');
                codec.DecodeOrders(bang < 0 ? text : text.Substring(0, bang), s, adj, p);
            }
            PrepareUnits(s);
            ann.Capture(s, adj, status);
            adj.ResolveMovement(s);
            ann.Finish(adj);
            for (int i = 0; i < noteCount; i++) ann.Note(notePower[i], noteText[i]);
            noteCount = 0;

            // ---- 4: what the campaign did to cities, rebels and control ----
            AfterMoves(s, ann);
            UpdateControl(s);
            if (s.season == 0) Starve(s, ann);
            if (s.season == 0 && Has(s, OPT_PLAGUE)) Plague(s, ann);
            if (s.season == map.seasonCount - 1) RollForNextYear(s, ann);
            UpdateControl(s);

            // ---- 5 ----
            if (s.disCount > 0 && s.phase == BoardState.PHASE_RETREAT)
            {
                // the gates of a rebelling city stay shut to retreating troops
                for (int q = 0; q < map.landCount; q++)
                {
                    if (map.provCity[q] >= 0 && CityRebels(s, q)) s.contested[map.provCity[q]] = true;
                    if (map.provSingle[q] && (s.provFlag[q] & F_REBEL) != 0) s.contested[q] = true;
                }
            }
            else
            {
                s.disCount = 0; s.phase = BoardState.PHASE_MOVE;
                EndCampaign(s, ann);
            }
            ann.Group();
        }

        // notes made before the announcement exists for this phase
        private int noteCount; private int[] notePower; private string[] noteText;

        private void Say(int power, string text)
        {
            if (notePower == null) { notePower = new int[64]; noteText = new string[64]; }
            if (noteCount >= notePower.Length)
            {
                int[] a = new int[noteCount * 2]; string[] b = new string[noteCount * 2];
                for (int i = 0; i < noteCount; i++) { a[i] = notePower[i]; b[i] = noteText[i]; }
                notePower = a; noteText = b;
            }
            notePower[noteCount] = power; noteText[noteCount] = text; noteCount++;
        }

        // ---------------------------------------------------------------- the bank

        private void Bank(BoardState s)
        {
            int now = CampaignNumber(s);
            // a) repayments, oldest debt first, interest before principal
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p) || payBank[p] <= 0) continue;
                int pay = payBank[p] < Treasury(s, p) ? payBank[p] : Treasury(s, p);
                int paid = PayLoans(s, p, pay, -1);
                if (paid > 0) Say(p, "Repays " + paid.ToString() + " ducats to the bank");
            }
            // b) tokens and gifts between players; all gifts leave before any arrives
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                for (int q = 0; q < np; q++)
                {
                    int to = chitGive[p * np + q] - 1;
                    if (to < 0 || !Alive(s, to) || to == p || s.ext[xChit + p * np + q] <= 0 || to == q) continue;
                    s.ext[xChit + p * np + q] = s.ext[xChit + p * np + q] - 1;
                    s.ext[xChit + to * np + q] = s.ext[xChit + to * np + q] + 1;
                }
            }
            for (int q = 0; q < np; q++) tmpPow[q] = 0;
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                for (int q = 0; q < np; q++)
                {
                    int n = give[p * np + q];
                    if (n <= 0 || q == p || !Alive(s, q)) continue;
                    if (Debt(s, p) > 0) { Say(p, "Cannot give money while in debt to the bank"); continue; }
                    if (n > Treasury(s, p)) n = Treasury(s, p);
                    if (n <= 0) continue;
                    AddMoney(s, p, -n); tmpPow[q] += n;
                    Say(p, "Gives " + n.ToString() + " ducats to " + Name(q));
                }
            }
            for (int q = 0; q < np; q++) if (tmpPow[q] > 0) AddMoney(s, q, tmpPow[q]);
            // c) new loans
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                for (int y = 1; y <= 2; y++)
                {
                    int n = borrow[p * 2 + y - 1];
                    if (n <= 0) continue;
                    int slot = -1;
                    for (int i = 0; i < MAX_LOANS && slot < 0; i++) if (LoanDue(s, p, i) < 0) slot = i;
                    if (!Has(s, OPT_LOANS) || Barred(s, p) || slot < 0 || PrincipalOwed(s, p) + n > LOAN_LIMIT) { Say(p, "The bank refuses a loan of " + n.ToString() + " ducats"); continue; }
                    int interest = y == 1 ? (n + 4) / 5 : (n + 1) / 2; // 20% or 50%, rounded up
                    int b = 2 + p * PB + 2 + slot * 3;
                    s.ext[b] = n; s.ext[b + 1] = interest; s.ext[b + 2] = now + y * map.seasonCount + 1;
                    AddMoney(s, p, n);
                    Say(p, "Borrows " + n.ToString() + " ducats for " + (y == 1 ? "one year" : "two years") + " (" + (n + interest).ToString() + " to repay)");
                }
            }
            // d) the bank collects what is due; a ruler who cannot pay meets the bankers' assassin
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                int due = 0;
                for (int i = 0; i < MAX_LOANS; i++) if (LoanDue(s, p, i) >= 0 && LoanDue(s, p, i) <= now) due += LoanPrincipal(s, p, i) + LoanInterest(s, p, i);
                if (due == 0) continue;
                if (due <= Treasury(s, p)) { PayLoans(s, p, due, now); Say(p, "The bank collects " + due.ToString() + " ducats"); continue; }
                s.ext[2 + p * PB] = 0;
                for (int i = 0; i < MAX_LOANS * 3; i++) s.ext[2 + p * PB + 2 + i] = 0;
                s.ext[2 + p * PB + P_FLAGS] = s.ext[2 + p * PB + P_FLAGS] | PF_BARRED;
                exCount[p] = 0; // nothing left to spend
                Say(-1, Name(p) + " cannot repay the bank. The bankers take the treasury and have the ruler assassinated");
                Assassinated(s, p);
            }
        }

        // Pays up to "money" ducats towards p's loans (only those due by "dueBy" if that is
        // 0 or more), earliest first, interest before principal. Returns what was paid.
        private int PayLoans(BoardState s, int p, int money, int dueBy)
        {
            int paid = 0;
            while (money > 0)
            {
                int pick = -1;
                for (int i = 0; i < MAX_LOANS; i++)
                {
                    int d = LoanDue(s, p, i);
                    if (d < 0 || (dueBy >= 0 && d > dueBy)) continue;
                    if (pick < 0 || d < LoanDue(s, p, pick)) pick = i;
                }
                if (pick < 0) break;
                int b = 2 + p * PB + 2 + pick * 3;
                int part = s.ext[b + 1] < money ? s.ext[b + 1] : money;
                s.ext[b + 1] = s.ext[b + 1] - part; money -= part; paid += part;
                part = s.ext[b] < money ? s.ext[b] : money;
                s.ext[b] = s.ext[b] - part; money -= part; paid += part;
                if (s.ext[b] == 0 && s.ext[b + 1] == 0) s.ext[b + 2] = 0; else break;
            }
            AddMoney(s, p, -paid);
            return paid;
        }

        // ---------------------------------------------------------------- expenses

        private bool IsBribe(int type) { return type == EX_DISBAND || type == EX_BUY || type == EX_AUTONOMOUS; }

        private void Expenses(BoardState s)
        {
            // Pass 1: everybody pays, whether or not the expense will work
            for (int p = 0; p < np; p++)
            {
                for (int i = 0; i < exCount[p]; i++)
                {
                    int e = p * MAX_EXPENSES + i;
                    int n = exAmt[e];
                    if (n > Treasury(s, p)) n = Treasury(s, p);
                    if (exType[e] == EX_ASSASSINATE) { n = (n / 12) * 12; if (n > 36) n = 36; }
                    else if (IsBribe(exType[e]) || exType[e] == EX_COUNTER) n = (n / 3) * 3;
                    AddMoney(s, p, -n);
                    exAmt[e] = n;
                    exOk[e] = n > 0 && Allowed(s, p, exType[e], exTarget[e]);
                    if (exType[e] == EX_RELIEF && n < 3) exOk[e] = false;
                    if (exType[e] == EX_PACIFY && n < 12) exOk[e] = false;
                }
            }
            // Pass 2: per unit, the largest bribe and the sum of the counter-bribes
            for (int l = 0; l < map.locCount; l++) { best[l] = 0; bestCount[l] = 0; counter[l] = 0; }
            for (int p = 0; p < np; p++)
            {
                for (int i = 0; i < exCount[p]; i++)
                {
                    int e = p * MAX_EXPENSES + i;
                    if (!exOk[e]) continue;
                    int at = exTarget[e];
                    if (exType[e] == EX_COUNTER) counter[at] += exAmt[e];
                    else if (IsBribe(exType[e]))
                    {
                        if (exAmt[e] > best[at]) { best[at] = exAmt[e]; bestCount[at] = 1; }
                        else if (exAmt[e] == best[at]) bestCount[at]++;
                    }
                }
            }
            // Pass 3: which bribes work (decided before any is carried out)
            for (int p = 0; p < np; p++)
            {
                for (int i = 0; i < exCount[p]; i++)
                {
                    int e = p * MAX_EXPENSES + i;
                    if (!exOk[e] || !IsBribe(exType[e])) continue;
                    int at = exTarget[e];
                    int need = MinBribe(s, p, exType[e], at);
                    exOk[e] = need > 0 && exAmt[e] == best[at] && bestCount[at] == 1 && exAmt[e] - counter[at] >= need;
                }
            }
            // Pass 4: carry out, in sheet order
            for (int p = 0; p < np; p++)
            {
                for (int i = 0; i < exCount[p]; i++)
                {
                    int e = p * MAX_EXPENSES + i;
                    int type = exType[e]; int at = exTarget[e]; string paid = exAmt[e].ToString() + " ducats: ";
                    if (type == EX_ASSASSINATE) continue;
                    if (type == EX_COUNTER) { Say(p, paid + "counter-bribe for " + Place(at) + (exOk[e] ? "" : " (wasted)")); continue; }
                    if (!exOk[e]) { Say(p, paid + ExpenseWord(type) + " " + Place(at) + " fails"); continue; }
                    int q = LandOf(at);
                    if (type == EX_RELIEF) { s.provFlag[q] = s.provFlag[q] & ~F_FAMINE; Say(p, paid + "famine relief for " + map.provName[q]); }
                    else if (type == EX_PACIFY) { Calm(s, q); Say(p, paid + "the rebellion in " + map.provName[q] + " is pacified"); }
                    else if (type == EX_REBEL)
                    {
                        if (exAmt[e] >= RebellionCost(s, p, q) && RebellionCost(s, p, q) > 0) { Rebel(s, q); Say(p, paid + map.provName[q] + " rises in rebellion"); }
                        else Say(p, paid + "no rebellion in " + map.provName[q]);
                    }
                    else
                    {
                        int u = UnitAt(s, at);
                        if (u < 0) continue;
                        string what = UnitName(s, u);
                        if (type == EX_DISBAND) { s.RemoveUnit(map, u); Say(p, paid + "the " + what + " is bribed to disband"); }
                        else if (type == EX_AUTONOMOUS) { s.unitPower[u] = autoPower; Say(p, paid + "the " + what + " declares itself autonomous"); }
                        else
                        {
                            int was = s.unitPower[u];
                            s.unitPower[u] = p;
                            if (s.owner[q] == was) Calm(s, q); // bought from under the ruler the rebels hated
                            Say(p, paid + "the " + what + " is bought from " + Name(was));
                        }
                    }
                }
            }
            // Pass 5: assassins
            for (int p = 0; p < np; p++)
            {
                for (int i = 0; i < exCount[p]; i++)
                {
                    int e = p * MAX_EXPENSES + i;
                    if (exType[e] != EX_ASSASSINATE) continue;
                    int v = exTarget[e];
                    string paid = exAmt[e].ToString() + " ducats: ";
                    if (!exOk[e] || s.ext[xChit + p * np + v] <= 0) { Say(p, paid + "no assassin can be sent against " + Name(v)); continue; }
                    s.ext[xChit + p * np + v] = s.ext[xChit + p * np + v] - 1;
                    int roll = dice.Roll(6, "kill." + p.ToString() + "." + v.ToString() + "." + i.ToString());
                    if (roll > exAmt[e] / 12) { Say(p, paid + "the attempt on the ruler of " + Name(v) + " fails (rolled " + roll.ToString() + ")"); continue; }
                    Say(p, paid + "the ruler of " + Name(v) + " is assassinated (rolled " + roll.ToString() + ")");
                    if (!killed[v]) Assassinated(s, v);
                }
            }
            // garrisons of a murdered ruler that were under siege give up, unless the besieger's ruler died too
            for (int q = 0; q < map.landCount; q++)
            {
                int c = map.provCity[q];
                if (c < 0 || (s.provFlag[q] & F_SIEGE) == 0) continue;
                int g = s.provUnit[c]; int b = s.provUnit[q];
                if (g < 0 || b < 0 || !Real(s.unitPower[g]) || !killed[s.unitPower[g]] || killed[s.unitPower[b]]) continue;
                Say(-1, "The besieged garrison of " + map.provName[q] + " surrenders on the news");
                s.RemoveUnit(map, g);
            }
        }

        private string ExpenseWord(int type)
        {
            if (type == EX_RELIEF) return "famine relief for";
            if (type == EX_PACIFY) return "pacifying";
            if (type == EX_DISBAND) return "a bribe to disband the unit in";
            if (type == EX_BUY) return "a bribe to buy the unit in";
            if (type == EX_AUTONOMOUS) return "a bribe to turn the garrison of";
            if (type == EX_REBEL) return "stirring up rebellion in";
            return "an expense in";
        }

        // Is this expense one the power may make at all (target exists, in reach)?
        private bool Allowed(BoardState s, int p, int type, int target)
        {
            if (type == EX_ASSASSINATE)
                return Has(s, OPT_ASSASSINS) && target != p && Alive(s, target) && s.ext[xChit + p * np + target] > 0;
            if (target < 0 || target >= map.locCount) return false;
            int q = LandOf(target);
            if (q >= map.landCount || map.provType[q] == MapData.SEA) { if (type != EX_COUNTER && !IsBribe(type)) return false; }
            bool near = !Has(s, OPT_ADJACENCY);
            if (type == EX_RELIEF) return (s.provFlag[q] & F_FAMINE) != 0 && (near || Near(s, p, q, true));
            if (type == EX_PACIFY) return ((s.provFlag[q] & F_REBEL) != 0 || CityRebels(s, q)) && (near || Near(s, p, q, true));
            if (type == EX_REBEL) return RebellionCost(s, p, q) > 0 && (near || Near(s, p, q, false));
            int u = UnitAt(s, target);
            if (u < 0) return false;
            if (type == EX_COUNTER) return near || s.unitPower[u] == p || Near(s, p, LandOf(s.unitLoc[u]), false);
            return MinBribe(s, p, type, target) > 0 && (near || Near(s, p, LandOf(s.unitLoc[u]), false));
        }

        private void Calm(BoardState s, int q)
        {
            s.provFlag[q] = s.provFlag[q] & ~F_REBEL;
            if (map.provCity[q] >= 0) s.provFlag[map.provCity[q]] = s.provFlag[map.provCity[q]] & ~F_REBEL;
        }

        // Rebels take the countryside and, if nobody garrisons it, the city.
        private void Rebel(BoardState s, int q)
        {
            int c = map.provCity[q];
            if (map.provSingle[q]) { if (s.provUnit[q] < 0) s.provFlag[q] = s.provFlag[q] | F_REBEL; return; }
            s.provFlag[q] = s.provFlag[q] | F_REBEL;
            if (c >= 0 && s.provUnit[c] < 0) s.provFlag[c] = s.provFlag[c] | F_REBEL;
        }

        // The ruler of v dies: this campaign its units only hold, and some of its lands rise.
        private void Assassinated(BoardState s, int v)
        {
            killed[v] = true;
            for (int q = 0; q < map.landCount; q++)
            {
                if (s.owner[q] != v || map.provType[q] == MapData.SEA) continue;
                int h = map.provHome[q];
                bool home = h >= 0 && HomeHolder(s, h) == v;
                bool garrisoned = s.provUnit[q] >= 0 || (map.provCity[q] >= 0 && s.provUnit[map.provCity[q]] >= 0);
                int limit = home ? (garrisoned ? 1 : 2) : (garrisoned ? 3 : 5);
                int roll = dice.Roll(6, "rise." + v.ToString() + "." + map.provId[q]);
                if (roll > limit) continue;
                int u = s.provUnit[q];
                if (u >= 0 && s.unitPower[u] != v) continue;
                if (map.provSingle[q] && u >= 0) continue;
                if ((s.provFlag[q] & F_REBEL) != 0) continue;
                Rebel(s, q);
                Say(-1, map.provName[q] + " rises in rebellion against " + Name(v));
            }
        }

        // ---------------------------------------------------------------- units

        // Is the fleet passage between sea-or-coast provinces a and b closed to this power?
        private bool StraitClosed(BoardState s, int power, int a, int b)
        {
            for (int i = 0; i < straitCount; i++)
            {
                if (!((straitA[i] == a && straitB[i] == b) || (straitA[i] == b && straitB[i] == a))) continue;
                int g = s.provUnit[straitGuard[i]];
                if (g >= 0 && s.unitType[g] == MapData.FLEET && s.unitPower[g] != power) return true;
            }
            return false;
        }

        // Applies what this ruleset says about the orders before the referee sees them.
        private void PrepareUnits(BoardState s)
        {
            for (int q = 0; q < map.provCount; q++) { heldBy[q] = false; mark[q] = false; }
            // which rebelling provinces have movers from exactly one outside power?
            for (int q = 0; q < map.landCount; q++)
            {
                if ((s.provFlag[q] & F_REBEL) == 0) continue;
                int who = -1; bool several = false;
                for (int u = 0; u < s.unitCount; u++)
                {
                    if (!EntersProvince(s, u, q) || s.unitPower[u] == s.owner[q]) continue;
                    if (who >= 0 && who != s.unitPower[u]) several = true;
                    who = s.unitPower[u];
                }
                mark[q] = who >= 0 && !several;
            }
            for (int u = 0; u < s.unitCount; u++)
            {
                int p = s.unitPower[u]; int loc = s.unitLoc[u]; int q = map.locProv[loc];
                int land = LandOf(loc);
                adj.uStr[u] = ClassStrength(s.unitFlag[u]);
                if (p == autoPower || killed[p]) { adj.SetHold(u); continue; }
                int type = s.unitType[u]; int o = adj.oType[u];
                bool besieger = type != MapData.GARRISON && (s.provFlag[q] & F_SIEGE) != 0;
                int city = type == MapData.GARRISON ? -1 : map.provCity[q];
                int g = city >= 0 ? s.provUnit[city] : -1;
                bool target = city >= 0 && ((g >= 0 && s.unitPower[g] != p) || (g < 0 && CityRebels(s, q) && s.owner[q] == p));
                if (besieger && !target) { s.provFlag[q] = s.provFlag[q] & ~F_SIEGE; besieger = false; } // nothing left to besiege
                if (o == Adjudicator.BESIEGE)
                {
                    if (!target || (type == MapData.FLEET && !map.cityPort[q])) adj.SetHold(u);
                }
                else if (o == Adjudicator.LIFT_SIEGE) { if (!besieger) adj.SetHold(u); }
                else if (besieger && o != Adjudicator.HOLD) adj.SetHold(u);   // a siege once begun pins the besieger
                else if (o == Adjudicator.CONVERT)
                {
                    if (type == MapData.GARRISON)
                    {
                        // a garrison under siege cannot come out
                        int outside = map.cityProv[q] >= 0 ? s.provUnit[map.cityProv[q]] : -1;
                        if (outside >= 0 && s.unitPower[outside] != p && (s.provFlag[map.cityProv[q]] & F_SIEGE) != 0) adj.SetHold(u);
                    }
                    else if (city >= 0 && CityRebels(s, q)) adj.SetHold(u);  // the gates are shut
                }
                else if (o == Adjudicator.MOVE && adj.oA[u] >= 0 && adj.oA[u] < map.locCount)
                {
                    int d = map.locProv[adj.oA[u]];
                    if (type == MapData.FLEET && StraitClosed(s, p, q, d)) adj.SetHold(u);
                    // where city and province are one space, rebels hold the only space there is against their ruler
                    else if (d < map.landCount && map.provSingle[d] && (s.provFlag[d] & F_REBEL) != 0 && s.owner[d] == p) adj.SetHold(u);
                }
                o = adj.oType[u];
                if (o == Adjudicator.HOLD && type != MapData.GARRISON) heldBy[land] = true;
                // rebels help an outsider in, if only one outside power is coming
                int into = Destination(s, u);
                if (into >= 0 && mark[into] && p != s.owner[into]) adj.uBonus[u] = 1;
            }
        }

        // The land province unit u is trying to enter (by moving, or by coming out of its city), or -1.
        private int Destination(BoardState s, int u)
        {
            int o = adj.oType[u];
            if (o == Adjudicator.MOVE && adj.oA[u] >= 0 && adj.oA[u] < map.locCount)
            {
                int d = map.locProv[adj.oA[u]];
                return d < map.landCount ? d : -1;
            }
            if (o == Adjudicator.CONVERT && s.unitType[u] == MapData.GARRISON)
            {
                int d = map.cityProv[map.locProv[s.unitLoc[u]]];
                return d;
            }
            return -1;
        }

        private bool EntersProvince(BoardState s, int u, int q)
        {
            if (s.unitPower[u] == autoPower || killed[s.unitPower[u]]) return false;
            return Destination(s, u) == q;
        }

        // After the referee has moved the units: sieges, on-the-spot conversions, rebels.
        // adj.res* still describe the units as they stood before the moves.
        private void AfterMoves(BoardState s, Announcement ann)
        {
            for (int q = 0; q < map.provCount; q++) newSiege[q] = false;
            for (int u = 0; u < adj.resCount; u++)
            {
                if (adj.rDislodged[u]) continue;
                int q = map.locProv[adj.resLoc[u]];
                int o = adj.oType[u];
                if (o == Adjudicator.BESIEGE)
                {
                    if ((s.provFlag[q] & F_SIEGE) == 0) { newSiege[q] = true; continue; }
                    int c = map.provCity[q];
                    int g = c >= 0 ? s.provUnit[c] : -1;
                    if (g >= 0) { ann.Note(-1, "The siege of " + map.provName[q] + " ends: the " + Name(s.unitPower[g]) + " garrison is destroyed"); s.RemoveUnit(map, g); }
                    else ann.Note(-1, "The rebellion in the city of " + map.provName[q] + " is put down");
                    Calm(s, q);
                }
            }
            for (int q = 0; q < map.landCount; q++)
            {
                if (newSiege[q]) s.provFlag[q] = s.provFlag[q] | F_SIEGE;
                else s.provFlag[q] = s.provFlag[q] & ~F_SIEGE;
            }
            // where city and province are one space, a conversion is a change on the spot:
            // it works if the unit is still there, and (for a garrison coming out) if nobody attacked
            for (int u = 0; u < adj.resCount; u++)
            {
                if (adj.oType[u] != Adjudicator.CONVERT || adj.rDislodged[u]) continue;
                int q = map.locProv[adj.resLoc[u]];
                if (!map.provSingle[q] || killed[adj.resPower[u]]) continue;
                int now = s.provUnit[q];
                if (now < 0 || s.unitPower[now] != adj.resPower[u] || s.unitType[now] != adj.resType[u]) continue;
                int to = adj.oB[u]; int d = adj.oA[u];
                if (to == s.unitType[now] || to < 0 || to >= map.unitTypeCount || d < 0 || d >= map.locCount || map.locProv[d] != q || !map.CanStand(to, d)) continue;
                if ((to == MapData.FLEET || s.unitType[now] == MapData.FLEET) && !map.cityPort[q]) continue;
                bool attacked = false;
                for (int x = 0; x < adj.resCount; x++)
                    if (x != u && adj.oType[x] == Adjudicator.MOVE && adj.oA[x] >= 0 && adj.oA[x] < map.locCount && map.locProv[adj.oA[x]] == q && adj.rStatus[x] != Adjudicator.R_VOID) attacked = true;
                if (s.unitType[now] == MapData.GARRISON && attacked) { ann.Note(adj.resPower[u], "The garrison of " + map.provName[q] + " cannot come out while the city is attacked"); continue; }
                s.unitType[now] = to; s.unitLoc[now] = to == MapData.FLEET ? d : q;
            }
            // rebels: an outsider's arrival frees the province; the ruler's troops standing still put it down
            for (int q = 0; q < map.landCount; q++)
            {
                bool province = (s.provFlag[q] & F_REBEL) != 0;
                if (!province && !CityRebels(s, q)) continue;
                int u = s.provUnit[q];
                if (u < 0 || s.unitType[u] == MapData.GARRISON) continue;
                if (s.unitPower[u] != s.owner[q]) { Calm(s, q); ann.Note(-1, "The rebels of " + map.provName[q] + " welcome the troops of " + Name(s.unitPower[u])); }
                else if (province && heldBy[q] && StoodStill(s, q))
                {
                    s.provFlag[q] = s.provFlag[q] & ~F_REBEL;
                    ann.Note(-1, "The rebellion in " + map.provName[q] + " is put down");
                }
            }
        }

        // Did the unit that was in land province q before the moves stay there, undislodged?
        private bool StoodStill(BoardState s, int q)
        {
            for (int u = 0; u < adj.resCount; u++)
                if (map.locProv[adj.resLoc[u]] == q && adj.resType[u] != MapData.GARRISON) return !adj.rDislodged[u] && !adj.rMoved[u];
            return false;
        }

        /// <summary>
        /// Control follows the troops: a unit in a province controls it, and its city too
        /// unless somebody's garrison holds the city; a garrison controls its city, and
        /// the province as well when no unit stands outside. Empty places keep their owner.
        /// </summary>
        public void UpdateControl(BoardState s)
        {
            for (int q = 0; q < map.landCount; q++)
            {
                if (map.provType[q] == MapData.SEA) continue;
                int c = map.provCity[q];
                int u = s.provUnit[q];
                int g = c >= 0 ? s.provUnit[c] : -1;
                if (u >= 0) s.owner[q] = s.unitPower[u];
                else if (g >= 0) s.owner[q] = s.unitPower[g];
                if (c >= 0)
                {
                    if (g >= 0) s.owner[c] = s.unitPower[g];
                    else if (u >= 0) s.owner[c] = s.unitPower[u];
                }
                // a siege needs a besieger
                if (u < 0 || s.unitType[u] == MapData.GARRISON) s.provFlag[q] = s.provFlag[q] & ~F_SIEGE;
            }
        }

        // Famine: at the end of the spring campaign's moves, everything standing in a starving province dies.
        private void Starve(BoardState s, Announcement ann)
        {
            for (int q = 0; q < map.landCount; q++)
            {
                if ((s.provFlag[q] & F_FAMINE) == 0) continue;
                s.provFlag[q] = s.provFlag[q] & ~F_FAMINE;
                Wipe(s, q, ann, "starves");
            }
        }

        private void Wipe(BoardState s, int q, Announcement ann, string how)
        {
            int u = s.provUnit[q];
            if (u >= 0) { ann.Note(s.unitPower[u], "The " + UnitName(s, u) + " " + how); s.RemoveUnit(map, u); }
            int c = map.provCity[q];
            u = c >= 0 ? s.provUnit[c] : -1;
            if (u >= 0) { ann.Note(s.unitPower[u], "The " + UnitName(s, u) + " " + how); s.RemoveUnit(map, u); }
        }

        // Two dice decide how bad the year is, then which row and which column of the table are struck.
        private void Strike(BoardState s, int[] table, string label, bool famine, Announcement ann)
        {
            int sev = dice.Roll(6, label + ".year.a") + dice.Roll(6, label + ".year.b");
            bool row = sev == 4 || sev == 6 || sev >= 8;
            bool col = sev == 5 || sev == 7 || sev >= 8;
            int r = row ? dice.Roll(6, label + ".row.a") + dice.Roll(6, label + ".row.b") : -1;
            int c = col ? dice.Roll(6, label + ".col.a") + dice.Roll(6, label + ".col.b") : -1;
            string where = "";
            for (int i = 0; i < 121; i++)
            {
                int q = table[i];
                if (q < 0) continue;
                if (!((r >= 0 && i / 11 == r - 2) || (c >= 0 && i % 11 == c - 2))) continue;
                where += (where == "" ? "" : ", ") + map.provName[q];
                if (famine) s.provFlag[q] = s.provFlag[q] | F_FAMINE;
                else Wipe(s, q, ann, "dies of the plague");
            }
            string what = famine ? "Famine" : "Plague";
            if (where == "") ann.Note(-1, famine ? "The harvest is good: no famine next year" : "No plague this year");
            else ann.Note(-1, what + (famine ? " will strike next year: " : " strikes: ") + where);
        }

        private void Plague(BoardState s, Announcement ann) { Strike(s, plagueTable, "plague", false, ann); }

        // When the fall campaign's sheets are opened, the dice for the coming year are
        // rolled: famine, and each variable income. (They are rolled here, not in the
        // adjustment phase, because only here has every power sealed a turn it would lose
        // by trying to re-roll; see docs/secret-orders-and-dice.md.)
        private void RollForNextYear(BoardState s, Announcement ann)
        {
            if (Has(s, OPT_FAMINE)) Strike(s, famTable, "famine", true, ann);
            if (!Has(s, OPT_VARINCOME)) return;
            for (int e = 0; e < incCount; e++)
                for (int k = 0; k < incDice[e]; k++)
                    s.ext[xDie + e * 2 + k] = dice.Roll(6, "income." + e.ToString() + "." + k.ToString());
        }

        // =====================================================================
        // End of a campaign, income, adjustments
        // =====================================================================

        private void EndCampaign(BoardState s, Announcement ann)
        {
            UpdateControl(s);
            // Who is out: a power that controls no city in any home country it held when the campaign began.
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                bool any = false;
                for (int q = 0; q < map.landCount && !any; q++)
                {
                    int h = map.provHome[q];
                    if (h >= 0 && HomeHolder(s, h) == p && CityHolder(s, q) == p) any = true;
                }
                tmpPow[p] = any ? 1 : 0;
            }
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p) || tmpPow[p] == 1) continue;
                s.ext[2 + p * PB + P_FLAGS] = s.ext[2 + p * PB + P_FLAGS] | PF_OUT;
                for (int u = s.unitCount - 1; u >= 0; u--) if (s.unitPower[u] == p) s.RemoveUnit(map, u);
                for (int d = s.disCount - 1; d >= 0; d--) if (s.disPower[d] == p) s.disPower[d] = -1;
                for (int q = 0; q < map.provCount; q++) if (s.owner[q] == p) { s.owner[q] = -1; s.provFlag[q] = s.provFlag[q] & ~F_REBEL; }
                for (int h = 0; h < np; h++) if (HomeHolder(s, h) == p) s.ext[xHome + h] = 0;
                ann.Note(-1, Name(p) + " holds no city in its home country and is out of the game");
            }
            UpdateControl(s);
            // Home countries change hands as a whole: to a power that controls every province and city of one.
            for (int h = 0; h < np; h++)
            {
                int who = -2;
                for (int q = 0; q < map.landCount && who != -1; q++)
                {
                    if (map.provHome[q] != h) continue;
                    int o = s.owner[q];
                    if (map.cityKind[q] != MapData.CITY_NONE && CityHolder(s, q) != o) o = -1;
                    if (!Alive(s, o)) o = -1;
                    if (who == -2) who = o; else if (who != o) who = -1;
                }
                if (who >= 0 && who != HomeHolder(s, h))
                {
                    s.ext[xHome + h] = who + 1;
                    ann.Note(-1, Name(who) + " now holds the whole of " + Name(h) + "'s home country");
                }
            }
            // Victory: enough cities and enough home countries; if several qualify, the one with most cities.
            int bestP = -1; int bestN = 0; bool tie = false;
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                int n = CitiesHeld(s, p);
                if (n < map.victoryCenters || HomesHeld(s, p) < victoryHomes) continue;
                if (n > bestN) { bestP = p; bestN = n; tie = false; } else if (n == bestN) tie = true;
            }
            int left = 0; int last = -1;
            for (int p = 0; p < np; p++) if (Alive(s, p)) { left++; last = p; }
            if (left == 1) { bestP = last; tie = false; }
            if (bestP >= 0 && !tie)
            {
                s.winner = bestP; s.phase = BoardState.PHASE_FINISHED; s.disCount = 0;
                return;
            }
            if (s.season < map.seasonCount - 1) { adj.NextSeason(s); return; }
            Income(s, ann);
            s.season = map.seasonCount; s.phase = BoardState.PHASE_ADJUST;
        }

        /// <summary>What province and city income power p would collect now (variable income not included).</summary>
        public int FixedIncome(BoardState s, int p)
        {
            int t = 0;
            for (int q = 0; q < map.landCount; q++)
            {
                if (map.provType[q] == MapData.SEA)
                {
                    int f = s.provUnit[q];
                    if (f >= 0 && s.unitPower[f] == p) t++;
                    continue;
                }
                bool famine = (s.provFlag[q] & F_FAMINE) != 0;
                if (s.owner[q] == p && !famine && (s.provFlag[q] & F_REBEL) == 0) t++;
                if (map.cityKind[q] == MapData.CITY_NONE || CityHolder(s, q) != p) continue;
                int c = map.provCity[q];
                bool garrisoned = c >= 0 ? s.provUnit[c] >= 0 : (map.provSingle[q] && s.provUnit[q] >= 0 && s.unitType[s.provUnit[q]] == MapData.GARRISON);
                bool open = c < 0 && !map.provSingle[q];
                bool rebels = open ? (s.provFlag[q] & F_REBEL) != 0 : CityRebels(s, q);
                if (garrisoned || (!famine && !rebels)) t += map.cityIncome[q];
            }
            return t;
        }

        private void Income(BoardState s, Announcement ann)
        {
            int fixedRoll = (s.year + 1) % 2 == 1 ? 4 : 3; // without dice: 4 for an odd year, 3 for an even one
            for (int p = 0; p < np; p++)
            {
                if (!Alive(s, p)) continue;
                int t = FixedIncome(s, p);
                int v = 0;
                for (int e = 0; e < incCount; e++)
                {
                    int who = incPower[e] >= 0 ? HomeHolder(s, incPower[e]) : CityHolder(s, incCity[e]);
                    if (who != p) continue;
                    for (int k = 0; k < incDice[e]; k++)
                    {
                        int roll = s.ext[xDie + e * 2 + k];
                        if (roll < 1 || roll > 6) roll = fixedRoll;
                        v += incValue[e * 6 + roll - 1];
                    }
                }
                AddMoney(s, p, t + v);
                if (ann != null) ann.Note(p, "Income: " + t.ToString() + " ducats from provinces, cities and seas, " + v.ToString() + " variable; treasury " + Treasury(s, p).ToString());
            }
            for (int i = 0; i < incCount * 2; i++) s.ext[xDie + i] = 0;
        }

        /// <summary>
        /// May this power build this unit here in the current adjustment phase? loc is a
        /// province (army), a province or coast (fleet), or a city entry (garrison).
        /// Does not look at money or at what else is being built.
        /// </summary>
        public bool BuildOk(BoardState s, int p, int type, int cls, int loc)
        {
            if (loc < 0 || loc >= map.locCount || !map.CanStand(type, loc)) return false;
            int at = map.locProv[loc];
            int q = LandOf(loc);
            if (q >= map.landCount || map.provType[q] == MapData.SEA || map.cityKind[q] == MapData.CITY_NONE) return false;
            int h = map.provHome[q];
            if (h < 0 || HomeHolder(s, h) != p) return false;                 // only in a home country you hold
            if ((s.provFlag[q] & F_FAMINE) != 0) return false;
            if (s.provUnit[at] >= 0) return false;
            if (type == MapData.GARRISON)
            {
                if (CityHolder(s, q) != p || CityRebels(s, q)) return false;
                if (map.provSingle[q] && s.provUnit[q] >= 0) return false;
            }
            else
            {
                if (s.owner[q] != p || (s.provFlag[q] & F_REBEL) != 0) return false;
                if (type == MapData.FLEET && !map.cityPort[q]) return false;
            }
            if (cls != CLASS_NORMAL && (!Has(s, OPT_SPECIAL) || HasSpecial(s, p))) return false;
            return true;
        }

        private void ResolveAdjust(BoardState s, Announcement ann, int[] status)
        {
            ClearMoney();
            for (int p = 0; p < np; p++) if (Alive(s, p)) ReadSheet(sheetText[p], s, p, true);
            ann.Begin(s, status);
            for (int q = 0; q < map.provCount; q++) { mark[q] = false; newSiege[q] = false; } // mark: built here; newSiege: army or fleet dismissed here
            // 1. units their owner dismisses
            for (int i = 0; i < adCount; i++)
            {
                if (adBuild[i] != 0) continue;
                int u = UnitAt(s, adLoc[i]);
                if (u < 0 || s.unitPower[u] != adPower[i]) { ann.Line(adPower[i], -1, adLoc[i], Announcement.K_REMOVE, adLoc[i], -1, Announcement.O_REFUSED); continue; }
                if (s.unitType[u] != MapData.GARRISON) newSiege[LandOf(adLoc[i])] = true;
                ann.Line(adPower[i], s.unitType[u], s.unitLoc[u], Announcement.K_REMOVE, s.unitLoc[u], -1, Announcement.O_OK);
                s.RemoveUnit(map, u);
            }
            // 2. every unit that stays must be paid for; when the money runs out the rest go home
            for (int q = 0; q < map.provCount; q++)
            {
                int u = s.provUnit[q];
                if (u < 0) continue;
                int p = s.unitPower[u];
                if (!Alive(s, p)) continue;
                int cost = ClassCost(s.unitFlag[u]);
                if (Treasury(s, p) >= cost) { AddMoney(s, p, -cost); continue; }
                ann.Line(p, s.unitType[u], s.unitLoc[u], Announcement.K_AUTO_REMOVE, s.unitLoc[u], -1, Announcement.O_OK);
                s.RemoveUnit(map, u);
            }
            // 3. new units, in the order written
            for (int i = 0; i < adCount; i++)
            {
                if (adBuild[i] == 0) continue;
                int p = adPower[i]; int loc = adLoc[i]; int type = adType[i];
                if (type == MapData.ARMY && loc < map.locCount) loc = map.locProv[loc];
                bool ok = BuildOk(s, p, type, adClass[i], loc);
                int q = ok ? LandOf(loc) : -1;
                if (ok && (mark[q] || (type != MapData.GARRISON && newSiege[q]))) ok = false; // one new unit per province; no swapping army for fleet on the spot
                if (ok && Treasury(s, p) < ClassCost(adClass[i])) ok = false;
                int line = ann.Line(p, type, -1, Announcement.K_BUILD, loc, -1, ok ? Announcement.O_OK : Announcement.O_REFUSED);
                if (!ok) continue;
                AddMoney(s, p, -ClassCost(adClass[i]));
                int nu = s.AddUnit(map, type, p, loc);
                if (nu >= 0) s.unitFlag[nu] = adClass[i];
                mark[q] = true;
            }
            for (int p = 0; p < np; p++) if (Alive(s, p)) ann.Note(p, "Treasury after paying the troops: " + Treasury(s, p).ToString() + " ducats");
            UpdateControl(s);
            adCount = 0;
            s.season = map.seasonCount;
            adj.NextSeason(s);
            ann.Group();
        }
    }
}
