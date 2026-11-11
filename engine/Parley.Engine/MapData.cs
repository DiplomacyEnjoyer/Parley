// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// A game board loaded from a ".pmap" text file: provinces, coasts, who can move
    /// where, supply centres, powers, starting units and victory condition.
    ///
    /// Nothing about any particular map is hardcoded. The number of players is simply
    /// the number of "power" lines in the file.
    ///
    /// UdonSharp subset only: flat arrays, no generics, no exceptions, no recursion.
    ///
    /// Terminology
    ///   province  a named space on the board (index 0..provCount-1)
    ///   loc       a place a unit can stand. Every province has a "main" loc whose index
    ///             equals the province index. Provinces with several separate coasts
    ///             have extra coast locs (index >= provCount) that only fleets use.
    /// </summary>
    public class MapData : UdonSharpBehaviour
    {
        public const int LAND = 0;   // inland: armies only
        public const int COAST = 1;  // coastal land: armies and fleets
        public const int SEA = 2;    // water: fleets only
        public const int CITY = 3;   // a walled city inside a province: only garrisons stand here

        public const int ARMY = 0;
        public const int FLEET = 1;
        public const int GARRISON = 2; // only in rulesets that have walled cities

        public const int CITY_NONE = 0;
        public const int CITY_OPEN = 1;   // a city without walls: it has an income but cannot hold a garrison
        public const int CITY_WALLED = 2; // a fortified city: a garrison can stand in it

        public const int BUILD_HOME = 0; // build only in owned home centres (standard rule)
        public const int BUILD_ANY = 1;  // build in any owned centre (common variant rule)

        public const int FORMAT_VERSION = 1;

        private const int M1 = 1000003;
        private const int M2 = 998244;
        private const string HASH_DIGITS = "abcdefghijklmnopqrstuvwxyz234567";

        // ---- identity ----
        public string mapId = "";
        public string mapName = "";
        public string credit = "";
        public string license = "";
        public string hash = "";      // short fingerprint of everything that affects the rules
        public string error = "";     // set when Load fails

        // ---- rules carried by the map ----
        public int startYear = 1;
        public int victoryCenters = 0;
        public int buildMode = 0;

        // ---- the rules that go with this board ----
        // Load fills these with the standard game's values; a Ruleset (see Ruleset.cs) may
        // overwrite them. Every engine part reads them from here, because every part has the map.
        /// <summary>Which ruleset this board is played with ("standard" unless the file says otherwise).</summary>
        public string rulesId = "standard";
        /// <summary>The rest of the file's "rules" line: defaults for that ruleset's options.</summary>
        public string rulesArgs = "";
        /// <summary>Movement seasons per year (2 in the standard game: spring and fall).</summary>
        public int seasonCount = 2;
        /// <summary>One letter per season, plus one for the adjustment time after the last season ("SFW").</summary>
        public string seasonLetters = "SFW";
        public string[] seasonNames;
        /// <summary>Per season: do supply centres change hands when it ends?</summary>
        public bool[] seasonCapture;
        /// <summary>Unit types in play (2: army and fleet; 3 adds the garrison).</summary>
        public int unitTypeCount = 2;
        public string unitLetters = "AF";
        /// <summary>True if fleets standing in coastal provinces may convoy too.</summary>
        public bool coastConvoy;
        /// <summary>True if positions carry the extra numbers a ruleset keeps (unit and province marks, treasuries).</summary>
        public bool extended;

        // ---- provinces ----
        /// <summary>
        /// All provinces. Ordinary provinces come first (0..landCount-1); after them one
        /// extra entry per walled city (type CITY), which is where that city's garrison stands.
        /// </summary>
        public int provCount;
        /// <summary>Number of ordinary provinces (everything that is not a city entry).</summary>
        public int landCount;
        public string[] provId;
        public string[] provName;
        public int[] provType;
        public bool[] provSc;
        public int[] provHome;        // power whose home centre this is, or -1
        public int[] provCoastStart;  // first extra coast loc of this province (if any)
        public int[] provCoastCount;  // number of extra coast locs (0 for normal provinces)
        public int scCount;

        // ---- cities (all CITY_NONE / -1 on boards without them) ----
        public int[] cityKind;        // per province: CITY_NONE, CITY_OPEN or CITY_WALLED
        public int[] cityIncome;      // per province: what the city pays each year
        public bool[] cityPort;       // per province: fleets can be built, garrisoned and besiege here
        /// <summary>Per province: city and province are one space, so only one unit fits (it may be a garrison).</summary>
        public bool[] provSingle;
        public int[] provCity;        // per province: index of its city entry, or -1
        public int[] cityProv;        // per city entry: the province around it; -1 for ordinary provinces
        /// <summary>Lines starting with "x": tables and settings that only a particular ruleset reads.</summary>
        public string[] extLine;
        public int extLineCount;

        // ---- locs ----
        public int locCount;
        public int[] locProv;
        public string[] locCoast;     // "" for main locs, e.g. "nc" for a coast loc
        public string[] locId;        // "spa" or "spa/nc"

        // ---- adjacency (start/count index into the flat array) ----
        public int[] armyStart; public int[] armyCount; public int[] armyAdj;   // province -> provinces
        public int[] fleetStart; public int[] fleetCount; public int[] fleetAdj; // loc -> locs
        public int[] seaStart; public int[] seaCount; public int[] seaAdj;       // province -> provinces touching by water (convoys)
        public int[] anyStart; public int[] anyCount; public int[] anyAdj;       // province -> provinces touching at all

        // ---- powers ----
        public int powerCount;
        public string[] powerId;
        public string[] powerName;
        public string[] powerAdjective;
        public string[] powerColor;

        // ---- starting position ----
        public int startUnitCount;
        public int[] startUnitPower;
        public int[] startUnitType;
        public int[] startUnitLoc;
        public int[] startOwner;      // per province: owning power at game start, or -1

        // ---- layout hints for boards and clients (not used by the rules) ----
        public int[] provX;
        public int[] provY;
        public string[] provShape;    // "x,y x,y ..." polygon, or ""
        public int layoutWidth;
        public int layoutHeight;

        // scratch
        private string[] tok;
        private int tokCount;

        // =====================================================================
        // Loading
        // =====================================================================

        /// <summary>Parses a .pmap file. Returns false and sets <see cref="error"/> on failure.</summary>
        public bool Load(string text)
        {
            error = "";
            if (text == null) { error = "no map text"; return false; }
            string[] lines = text.Split('\n');
            int n = lines.Length;
            tok = new string[64];

            // ---- pass 1: count things so arrays can be sized ----
            int nProv = 0; int nPow = 0; int nCoast = 0; int nUnit = 0; int nArmyEdge = 0; int nFleetEdge = 0;
            int nCity = 0; int nExt = 0;
            bool sawHeader = false;
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0) continue;
                string k = tok[0];
                if (k == "parleymap")
                {
                    sawHeader = true;
                    if (tokCount < 2 || ParseInt(tok[1], -1) != FORMAT_VERSION) { error = "unsupported map format version"; return false; }
                }
                else if (k == "prov") nProv++;
                else if (k == "city") { if (tokCount >= 3 && tok[2] == "walled" && !HasToken("single", 3)) nCity++; }
                else if (k == "x") nExt++;
                else if (k == "power") nPow++;
                else if (k == "coast") nCoast++;
                else if (k == "unit") nUnit++;
                else if (k == "army") nArmyEdge += tokCount - 2;
                else if (k == "fleet") nFleetEdge += tokCount - 2;
            }
            if (!sawHeader) { error = "missing 'parleymap 1' header line"; return false; }
            if (nProv == 0) { error = "map has no provinces"; return false; }
            if (nPow == 0) { error = "map has no powers"; return false; }

            int nLand = nProv;
            nProv = nLand + nCity;      // from here on nProv counts the city entries too
            landCount = nLand;
            provCount = nProv;
            locCount = nProv + nCoast;
            powerCount = nPow;
            provId = new string[nProv]; provName = new string[nProv]; provType = new int[nProv];
            provSc = new bool[nProv]; provHome = new int[nProv];
            provCoastStart = new int[nProv]; provCoastCount = new int[nProv];
            provX = new int[nProv]; provY = new int[nProv]; provShape = new string[nProv];
            startOwner = new int[nProv];
            cityKind = new int[nProv]; cityIncome = new int[nProv]; cityPort = new bool[nProv]; provSingle = new bool[nProv];
            provCity = new int[nProv]; cityProv = new int[nProv];
            extLine = new string[nExt]; extLineCount = 0;
            rulesId = "standard"; rulesArgs = ""; coastConvoy = false; extended = false;
            seasonCount = 2; seasonLetters = "SFW"; unitTypeCount = 2; unitLetters = "AF";
            seasonNames = new string[3]; seasonNames[0] = "Spring"; seasonNames[1] = "Fall"; seasonNames[2] = "Winter";
            seasonCapture = new bool[2]; seasonCapture[0] = false; seasonCapture[1] = true;
            bool customSeasons = false;
            for (int i = 0; i < nProv; i++) { cityKind[i] = CITY_NONE; cityIncome[i] = 0; cityPort[i] = false; provSingle[i] = false; provCity[i] = -1; cityProv[i] = -1; }
            locProv = new int[locCount]; locCoast = new string[locCount]; locId = new string[locCount];
            powerId = new string[nPow]; powerName = new string[nPow]; powerAdjective = new string[nPow]; powerColor = new string[nPow];
            startUnitPower = new int[nUnit]; startUnitType = new int[nUnit]; startUnitLoc = new int[nUnit];
            startUnitCount = 0;
            mapId = ""; mapName = ""; credit = ""; license = "";
            startYear = 1; victoryCenters = 0; buildMode = BUILD_HOME;
            layoutWidth = 0; layoutHeight = 0;

            // ---- pass 2: powers ----
            int pw = 0;
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0 || tok[0] != "power") continue;
                // power <id> <adjective> <color> <name...>
                if (tokCount < 5) { error = "line " + (i + 1).ToString() + ": power needs id, adjective, color, name"; return false; }
                powerId[pw] = tok[1].ToLower();
                powerAdjective[pw] = tok[2].Replace('_', ' ');
                powerColor[pw] = tok[3];
                powerName[pw] = JoinFrom(4);
                pw++;
            }
            for (int i = 0; i < nPow; i++)
                for (int j = 0; j < i; j++)
                    if (powerId[i] == powerId[j]) { error = "duplicate power id " + powerId[i]; return false; }

            // ---- pass 3: provinces and headers ----
            int pv = 0;
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0) continue;
                string k = tok[0];
                if (k == "prov")
                {
                    // prov <id> <l|c|s> <-|*|powerId> <name...>
                    if (tokCount < 5) { error = "line " + (i + 1).ToString() + ": prov needs id, type, centre, name"; return false; }
                    provId[pv] = tok[1].ToLower();
                    string t = tok[2];
                    if (t == "l") provType[pv] = LAND;
                    else if (t == "c") provType[pv] = COAST;
                    else if (t == "s") provType[pv] = SEA;
                    else { error = "line " + (i + 1).ToString() + ": province type must be l, c or s"; return false; }
                    provHome[pv] = -1;
                    startOwner[pv] = -1;
                    provSc[pv] = false;
                    if (tok[3] == "*") provSc[pv] = true;
                    else if (tok[3] != "-")
                    {
                        int hp = FindPower(tok[3]);
                        if (hp < 0) { error = "line " + (i + 1).ToString() + ": unknown power " + tok[3]; return false; }
                        provSc[pv] = true; provHome[pv] = hp; startOwner[pv] = hp;
                    }
                    provName[pv] = JoinFrom(4);
                    provShape[pv] = "";
                    provX[pv] = -1; provY[pv] = -1;
                    locProv[pv] = pv; locCoast[pv] = ""; locId[pv] = provId[pv];
                    pv++;
                }
                else if (k == "id" && tokCount >= 2) mapId = tok[1];
                else if (k == "name") mapName = JoinFrom(1);
                else if (k == "credit") credit = JoinFrom(1);
                else if (k == "license") license = JoinFrom(1);
                else if (k == "start" && tokCount >= 2) startYear = ParseInt(tok[1], 1);
                else if (k == "victory" && tokCount >= 2) victoryCenters = ParseInt(tok[1], 0);
                else if (k == "build" && tokCount >= 2) buildMode = tok[1] == "any" ? BUILD_ANY : BUILD_HOME;
                else if (k == "size" && tokCount >= 3) { layoutWidth = ParseInt(tok[1], 0); layoutHeight = ParseInt(tok[2], 0); }
                else if (k == "rules" && tokCount >= 2) { rulesId = tok[1].ToLower(); rulesArgs = JoinFrom(2); extended = rulesId != "standard"; }
                else if (k == "convoy" && tokCount >= 2) coastConvoy = tok[1] == "coastal";
                else if (k == "x") { extLine[extLineCount] = JoinFrom(1); extLineCount++; }
                else if (k == "seasons")
                {
                    // seasons <Name> <Name> ... <NameOfAdjustmentTime>   (a '*' after a name: centres change hands when it ends)
                    if (tokCount < 3 || tokCount > 10) { error = "line " + (i + 1).ToString() + ": seasons needs one to eight season names and a name for the adjustment time"; return false; }
                    seasonCount = tokCount - 2;
                    seasonNames = new string[seasonCount + 1]; seasonCapture = new bool[seasonCount];
                    seasonLetters = "";
                    for (int t = 1; t < tokCount; t++)
                    {
                        string nm = tok[t];
                        bool star = nm.Length > 1 && nm[nm.Length - 1] == '*';
                        if (star) nm = nm.Substring(0, nm.Length - 1);
                        seasonNames[t - 1] = nm.Replace('_', ' ');
                        if (t - 1 < seasonCount) seasonCapture[t - 1] = star;
                        // the letter: the first letter of the name that no earlier season uses
                        string up = nm.ToUpper(); string letter = "";
                        for (int c = 0; c < up.Length && letter == ""; c++)
                            if (up[c] >= 'A' && up[c] <= 'Z' && seasonLetters.IndexOf(up[c]) < 0) letter = up.Substring(c, 1);
                        if (letter == "") { error = "line " + (i + 1).ToString() + ": cannot find a distinct letter for season " + nm; return false; }
                        seasonLetters += letter;
                    }
                    customSeasons = true;
                }
            }
            // ---- pass 3b: cities ----
            int cn = nLand;
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0 || tok[0] != "city") continue;
                // city <prov> <open|walled> <income> [port] [single]
                if (tokCount < 4) { error = "line " + (i + 1).ToString() + ": city needs province, open or walled, income"; return false; }
                int cp = -1;
                string cid = tok[1].ToLower();
                for (int j = 0; j < nLand; j++) if (provId[j] == cid) cp = j;
                if (cp < 0 || provType[cp] == SEA) { error = "line " + (i + 1).ToString() + ": no land province " + tok[1] + " for this city"; return false; }
                if (cityKind[cp] != CITY_NONE) { error = "line " + (i + 1).ToString() + ": " + tok[1] + " already has a city"; return false; }
                if (tok[2] != "open" && tok[2] != "walled") { error = "line " + (i + 1).ToString() + ": a city is open or walled"; return false; }
                cityKind[cp] = tok[2] == "walled" ? CITY_WALLED : CITY_OPEN;
                cityIncome[cp] = ParseInt(tok[3], 0);
                cityPort[cp] = HasToken("port", 4);
                provSingle[cp] = HasToken("single", 4);
                if (cityKind[cp] == CITY_WALLED && !provSingle[cp])
                {
                    provId[cn] = provId[cp] + "_c"; provName[cn] = provName[cp]; provType[cn] = CITY;
                    provSc[cn] = true; provHome[cn] = provHome[cp]; startOwner[cn] = startOwner[cp];
                    provShape[cn] = ""; provX[cn] = -1; provY[cn] = -1;
                    locProv[cn] = cn; locCoast[cn] = ""; locId[cn] = provId[cn];
                    cityKind[cn] = CITY_WALLED; cityIncome[cn] = cityIncome[cp]; cityPort[cn] = cityPort[cp];
                    provCity[cp] = cn; cityProv[cn] = cp;
                    cn++;
                }
            }
            if (cn > nLand || rulesId != "standard") { unitTypeCount = 3; unitLetters = "AFG"; }
            for (int i = 0; i < nProv; i++)
                for (int j = 0; j < i; j++)
                    if (provId[i] == provId[j]) { error = "duplicate province id " + provId[i]; return false; }

            // ---- pass 4: coasts (grouped so each province's coasts are contiguous) ----
            int nextLoc = nProv;
            for (int p = 0; p < nProv; p++) { provCoastStart[p] = 0; provCoastCount[p] = 0; }
            // 4a: count per province
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0 || tok[0] != "coast") continue;
                if (tokCount < 3) { error = "line " + (i + 1).ToString() + ": coast needs province and coast id"; return false; }
                int cp = FindProv(tok[1]);
                if (cp < 0) { error = "line " + (i + 1).ToString() + ": unknown province " + tok[1]; return false; }
                if (provType[cp] != COAST) { error = "line " + (i + 1).ToString() + ": only coastal provinces can have named coasts"; return false; }
                provCoastCount[cp]++;
            }
            for (int p = 0; p < nProv; p++)
            {
                provCoastStart[p] = nextLoc;
                nextLoc += provCoastCount[p];
                provCoastCount[p] = 0; // refilled below
            }
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0 || tok[0] != "coast") continue;
                int cp = FindProv(tok[1]);
                int l = provCoastStart[cp] + provCoastCount[cp];
                provCoastCount[cp]++;
                locProv[l] = cp;
                locCoast[l] = tok[2].ToLower();
                locId[l] = provId[cp] + "/" + locCoast[l];
            }

            // ---- pass 5: adjacency, units, layout ----
            int[] ea = new int[nArmyEdge + 1]; int[] eb = new int[nArmyEdge + 1]; int ne = 0;
            int[] fa = new int[nFleetEdge + 1]; int[] fb = new int[nFleetEdge + 1]; int nf = 0;
            for (int i = 0; i < n; i++)
            {
                Tokenize(lines[i]);
                if (tokCount == 0) continue;
                string k = tok[0];
                if (k == "army")
                {
                    // army <prov>: <prov> <prov> ...   (two-way)
                    int a = FindProv(StripColon(tok[1]));
                    if (a < 0) { error = "line " + (i + 1).ToString() + ": unknown province " + tok[1]; return false; }
                    for (int t = 2; t < tokCount; t++)
                    {
                        int b = FindProv(tok[t]);
                        if (b < 0) { error = "line " + (i + 1).ToString() + ": unknown province " + tok[t]; return false; }
                        if (provType[a] == SEA || provType[b] == SEA) { error = "line " + (i + 1).ToString() + ": armies cannot enter sea province"; return false; }
                        if (a == b) continue;
                        ea[ne] = a; eb[ne] = b; ne++;
                    }
                }
                else if (k == "fleet")
                {
                    // fleet <loc>: <loc> <loc> ...     (two-way; loc is "prov" or "prov/coast")
                    int a = FindLoc(StripColon(tok[1]));
                    if (a < 0) { error = "line " + (i + 1).ToString() + ": unknown place " + tok[1]; return false; }
                    if (!FleetCanStand(a)) { error = "line " + (i + 1).ToString() + ": a fleet cannot be at " + tok[1] + " (inland, or a coast must be named)"; return false; }
                    for (int t = 2; t < tokCount; t++)
                    {
                        int b = FindLoc(tok[t]);
                        if (b < 0) { error = "line " + (i + 1).ToString() + ": unknown place " + tok[t]; return false; }
                        if (!FleetCanStand(b)) { error = "line " + (i + 1).ToString() + ": a fleet cannot be at " + tok[t] + " (inland, or a coast must be named)"; return false; }
                        if (locProv[a] == locProv[b]) continue;
                        fa[nf] = a; fb[nf] = b; nf++;
                    }
                }
                else if (k == "unit")
                {
                    // unit <power> <A|F> <loc>
                    if (tokCount < 4) { error = "line " + (i + 1).ToString() + ": unit needs power, type, place"; return false; }
                    int up = FindPower(tok[1]);
                    int ul = FindLoc(tok[3]);
                    if (up < 0) { error = "line " + (i + 1).ToString() + ": unknown power " + tok[1]; return false; }
                    if (ul < 0) { error = "line " + (i + 1).ToString() + ": unknown place " + tok[3]; return false; }
                    int ut = (tok[2] == "F" || tok[2] == "f") ? FLEET : ((tok[2] == "G" || tok[2] == "g") ? GARRISON : ARMY);
                    if (ut == GARRISON && ul < landCount && provCity[ul] >= 0) ul = provCity[ul]; // a garrison stands in the city
                    if (!CanStand(ut, ul)) { error = "line " + (i + 1).ToString() + ": that unit type cannot be at " + tok[3]; return false; }
                    startUnitPower[startUnitCount] = up; startUnitType[startUnitCount] = ut; startUnitLoc[startUnitCount] = ul;
                    startUnitCount++;
                }
                else if (k == "owner")
                {
                    // owner <power|none> <prov> ...    (start ownership that differs from home centres)
                    if (tokCount < 3) continue;
                    int op = tok[1] == "none" ? -1 : FindPower(tok[1]);
                    if (op < 0 && tok[1] != "none") { error = "line " + (i + 1).ToString() + ": unknown power " + tok[1]; return false; }
                    for (int t = 2; t < tokCount; t++)
                    {
                        int b = FindProv(tok[t]);
                        if (b < 0 || !provSc[b]) { error = "line " + (i + 1).ToString() + ": " + tok[t] + " is not a supply centre"; return false; }
                        startOwner[b] = op;
                        if (provCity[b] >= 0) startOwner[provCity[b]] = op;
                    }
                }
                else if (k == "xy")
                {
                    if (tokCount < 4) continue;
                    int b = FindProv(tok[1]);
                    if (b < 0) { error = "line " + (i + 1).ToString() + ": unknown province " + tok[1]; return false; }
                    provX[b] = ParseInt(tok[2], 0); provY[b] = ParseInt(tok[3], 0);
                }
                else if (k == "shape")
                {
                    if (tokCount < 3) continue;
                    int b = FindProv(tok[1]);
                    if (b < 0) { error = "line " + (i + 1).ToString() + ": unknown province " + tok[1]; return false; }
                    provShape[b] = JoinFrom(2);
                }
            }

            // ---- build adjacency tables ----
            armyStart = new int[nProv]; armyCount = new int[nProv];
            for (int e = 0; e < ne; e++) { armyCount[ea[e]]++; armyCount[eb[e]]++; }
            int tot = 0;
            for (int p = 0; p < nProv; p++) { armyStart[p] = tot; tot += armyCount[p]; armyCount[p] = 0; }
            armyAdj = new int[tot + 1];
            for (int e = 0; e < ne; e++)
            {
                AddUnique(armyAdj, armyStart, armyCount, ea[e], eb[e]);
                AddUnique(armyAdj, armyStart, armyCount, eb[e], ea[e]);
            }

            fleetStart = new int[locCount]; fleetCount = new int[locCount];
            for (int e = 0; e < nf; e++) { fleetCount[fa[e]]++; fleetCount[fb[e]]++; }
            tot = 0;
            for (int l = 0; l < locCount; l++) { fleetStart[l] = tot; tot += fleetCount[l]; fleetCount[l] = 0; }
            fleetAdj = new int[tot + 1];
            for (int e = 0; e < nf; e++)
            {
                AddUnique(fleetAdj, fleetStart, fleetCount, fa[e], fb[e]);
                AddUnique(fleetAdj, fleetStart, fleetCount, fb[e], fa[e]);
            }

            // province-level water adjacency (for convoys)
            seaStart = new int[nProv]; seaCount = new int[nProv];
            for (int e = 0; e < nf; e++) { seaCount[locProv[fa[e]]]++; seaCount[locProv[fb[e]]]++; }
            tot = 0;
            for (int p = 0; p < nProv; p++) { seaStart[p] = tot; tot += seaCount[p]; seaCount[p] = 0; }
            seaAdj = new int[tot + 1];
            for (int e = 0; e < nf; e++)
            {
                AddUnique(seaAdj, seaStart, seaCount, locProv[fa[e]], locProv[fb[e]]);
                AddUnique(seaAdj, seaStart, seaCount, locProv[fb[e]], locProv[fa[e]]);
            }

            // province-level "touches at all" adjacency (used to measure distance for forced disbands)
            anyStart = new int[nProv]; anyCount = new int[nProv];
            for (int p = 0; p < nProv; p++) anyCount[p] = armyCount[p] + seaCount[p];
            tot = 0;
            for (int p = 0; p < nProv; p++) { anyStart[p] = tot; tot += anyCount[p]; anyCount[p] = 0; }
            anyAdj = new int[tot + 1];
            for (int p = 0; p < nProv; p++)
            {
                for (int a = 0; a < armyCount[p]; a++) AddUnique(anyAdj, anyStart, anyCount, p, armyAdj[armyStart[p] + a]);
                for (int a = 0; a < seaCount[p]; a++) AddUnique(anyAdj, anyStart, anyCount, p, seaAdj[seaStart[p] + a]);
            }

            // ---- derived values and sanity checks ----
            scCount = 0;
            for (int p = 0; p < nProv; p++) if (provSc[p]) scCount++;
            if (victoryCenters <= 0) victoryCenters = scCount / 2 + 1;
            mapId = CleanId(mapId);
            if (mapId == "") mapId = "map";
            if (locCount * unitTypeCount > 3800) { error = "map too large (more than " + (3800 / unitTypeCount).ToString() + " places)"; return false; }
            if (powerCount > 3000) { error = "too many powers"; return false; }
            if (mapName == "") mapName = mapId;
            for (int p = 0; p < nProv; p++)
            {
                if (provType[p] == COAST && provCoastCount[p] > 0 && fleetCount[p] > 0)
                {
                    error = provId[p] + " has named coasts, so fleet links must name a coast (e.g. " + locId[provCoastStart[p]] + ")";
                    return false;
                }
            }
            for (int u = 0; u < startUnitCount; u++)
                for (int v = 0; v < u; v++)
                    if (locProv[startUnitLoc[u]] == locProv[startUnitLoc[v]]) { error = "two starting units in " + provId[locProv[startUnitLoc[u]]]; return false; }

            hash = ComputeHash(customSeasons);
            tok = null;
            return true;
        }

        // Is the word among the tokens of the current line, from position "first" on?
        private bool HasToken(string word, int first)
        {
            for (int i = first; i < tokCount; i++) if (tok[i] == word) return true;
            return false;
        }

        private void AddUnique(int[] adj, int[] start, int[] count, int from, int to)
        {
            int s = start[from]; int c = count[from];
            for (int i = 0; i < c; i++) if (adj[s + i] == to) return;
            adj[s + c] = to;
            count[from] = c + 1;
        }

        /// <summary>Splits a line into whitespace-separated tokens (comments after '#' removed).</summary>
        private void Tokenize(string line)
        {
            tokCount = 0;
            if (line == null) return;
            int len = line.Length;
            int start = -1;
            for (int i = 0; i <= len; i++)
            {
                char c = i < len ? line[i] : ' ';
                if (c == '#' && start < 0 && (tokCount == 0 || !IsColorContext())) { break; }
                bool ws = c == ' ' || c == '\t' || c == '\r' || c == '\n';
                if (ws)
                {
                    if (start >= 0)
                    {
                        if (tokCount < tok.Length) { tok[tokCount] = line.Substring(start, i - start); tokCount++; }
                        start = -1;
                    }
                }
                else if (start < 0) start = i;
            }
        }

        // A '#' starts a comment, except in the colour field of a power line ("power id Adj #rrggbb Name").
        private bool IsColorContext()
        {
            return tok[0] == "power" && tokCount == 3;
        }

        private string JoinFrom(int first)
        {
            string s = "";
            for (int i = first; i < tokCount; i++)
            {
                if (i > first) s += " ";
                s += tok[i];
            }
            return s;
        }

        // Map ids appear inside game codes, so only lower-case letters and digits are kept.
        private string CleanId(string s)
        {
            string r = "";
            string low = s.ToLower();
            for (int i = 0; i < low.Length; i++)
            {
                char c = low[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) r += low.Substring(i, 1);
            }
            return r;
        }

        private string StripColon(string s)
        {
            if (s.Length > 0 && s[s.Length - 1] == ':') return s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>Parses a non-negative or negative whole number without using exceptions.</summary>
        public int ParseInt(string s, int fallback)
        {
            if (s == null || s.Length == 0) return fallback;
            int i = 0; bool neg = false;
            if (s[0] == '-') { neg = true; i = 1; }
            if (i >= s.Length) return fallback;
            int v = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return fallback;
                if (v > 100000000) return fallback;
                v = v * 10 + (c - '0');
            }
            return neg ? -v : v;
        }

        // Fingerprint of the rule-relevant content. Uses only small modular arithmetic
        // so it behaves identically everywhere (no reliance on integer overflow).
        private string ComputeHash(bool customSeasons)
        {
            int h1 = 7; int h2 = 13;
            for (int p = 0; p < provCount; p++)
            {
                int v = provType[p] + (provSc[p] ? 3 : 0) + (provHome[p] + 1) * 7 + provCoastCount[p] * 131 + (startOwner[p] + 1) * 977;
                string id = provId[p];
                for (int c = 0; c < id.Length; c++) v = (v * 37 + id[c]) % M1;
                h1 = (h1 * 31 + v) % M1; h2 = (h2 * 29 + v + p) % M2;
                for (int a = 0; a < armyCount[p]; a++) { h1 = (h1 * 31 + armyAdj[armyStart[p] + a] + 1) % M1; h2 = (h2 * 29 + armyAdj[armyStart[p] + a] + 5) % M2; }
            }
            for (int l = 0; l < locCount; l++)
            {
                h1 = (h1 * 31 + 211) % M1;
                for (int a = 0; a < fleetCount[l]; a++) { h1 = (h1 * 31 + fleetAdj[fleetStart[l] + a] + 1) % M1; h2 = (h2 * 29 + fleetAdj[fleetStart[l] + a] + 3) % M2; }
            }
            for (int u = 0; u < startUnitCount; u++)
            {
                int v = startUnitPower[u] * 5 + startUnitType[u] + startUnitLoc[u] * 11;
                h1 = (h1 * 31 + v) % M1; h2 = (h2 * 29 + v) % M2;
            }
            h1 = (h1 * 31 + powerCount * 101 + victoryCenters * 7 + buildMode) % M1;
            h2 = (h2 * 29 + startYear % 9973) % M2;
            // Everything below is only mixed in when a board uses it, so the fingerprints of
            // plain standard boards stay what they always were.
            if (rulesId != "standard" || customSeasons || coastConvoy || landCount != provCount || extLineCount > 0)
            {
                string t = rulesId + "|" + rulesArgs + "|" + seasonLetters + (coastConvoy ? "|cc" : "");
                for (int i = 0; i < seasonCount; i++) t += seasonCapture[i] ? "1" : "0";
                for (int i = 0; i < extLineCount; i++) t += "|" + extLine[i];
                for (int i = 0; i < t.Length; i++) { h1 = (h1 * 31 + t[i]) % M1; h2 = (h2 * 29 + t[i] + i % 7) % M2; }
                for (int p = 0; p < landCount; p++)
                {
                    int v = cityKind[p] + cityIncome[p] * 3 + (cityPort[p] ? 40 : 0) + (provSingle[p] ? 80 : 0) + (provCity[p] + 1) * 160;
                    h1 = (h1 * 31 + v) % M1; h2 = (h2 * 29 + v + 1) % M2;
                }
            }
            string digits = HASH_DIGITS;
            string s = "";
            for (int i = 0; i < 3; i++) { s += digits.Substring(h1 % 32, 1); h1 = h1 / 32; }
            for (int i = 0; i < 3; i++) { s += digits.Substring(h2 % 32, 1); h2 = h2 / 32; }
            return s;
        }

        // =====================================================================
        // Lookups
        // =====================================================================

        public int FindPower(string id)
        {
            if (id == null) return -1;
            string k = id.ToLower();
            for (int i = 0; i < powerCount; i++) if (powerId[i] == k) return i;
            return -1;
        }

        public int FindProv(string id)
        {
            if (id == null) return -1;
            string k = id.ToLower();
            for (int i = 0; i < provCount; i++) if (provId[i] == k) return i;
            return -1;
        }

        /// <summary>Finds a loc by "prov" or "prov/coast" id.</summary>
        public int FindLoc(string id)
        {
            if (id == null) return -1;
            string k = id.ToLower();
            int slash = k.IndexOf('/');
            if (slash < 0) return FindProv(k);
            int p = FindProv(k.Substring(0, slash));
            if (p < 0) return -1;
            string c = k.Substring(slash + 1);
            for (int i = 0; i < provCoastCount[p]; i++)
                if (locCoast[provCoastStart[p] + i] == c) return provCoastStart[p] + i;
            return -1;
        }

        public bool ArmyAdjacent(int provA, int provB)
        {
            int s = armyStart[provA]; int c = armyCount[provA];
            for (int i = 0; i < c; i++) if (armyAdj[s + i] == provB) return true;
            return false;
        }

        public bool FleetAdjacent(int locA, int locB)
        {
            int s = fleetStart[locA]; int c = fleetCount[locA];
            for (int i = 0; i < c; i++) if (fleetAdj[s + i] == locB) return true;
            return false;
        }

        /// <summary>True when the two provinces touch by water (any coasts).</summary>
        public bool SeaAdjacent(int provA, int provB)
        {
            int s = seaStart[provA]; int c = seaCount[provA];
            for (int i = 0; i < c; i++) if (seaAdj[s + i] == provB) return true;
            return false;
        }

        /// <summary>True when a fleet standing at loc can move to some coast of province prov.</summary>
        public bool FleetCanReachProv(int loc, int prov)
        {
            int s = fleetStart[loc]; int c = fleetCount[loc];
            for (int i = 0; i < c; i++) if (locProv[fleetAdj[s + i]] == prov) return true;
            return false;
        }

        public bool FleetCanStand(int loc)
        {
            int p = locProv[loc];
            if (provType[p] == LAND || provType[p] == CITY) return false;
            if (provType[p] == SEA) return loc == p;
            if (provCoastCount[p] > 0) return loc != p; // must be on a named coast
            return true;
        }

        public bool CanStand(int unitType, int loc)
        {
            if (loc < 0 || loc >= locCount) return false;
            if (unitType == ARMY) return loc < provCount && provType[loc] != SEA && provType[loc] != CITY;
            if (unitType == GARRISON) return loc < provCount && (provType[loc] == CITY || (provSingle[loc] && cityKind[loc] == CITY_WALLED));
            return FleetCanStand(loc);
        }

        /// <summary>Human-readable place name, e.g. "Spain (nc)".</summary>
        public string LocName(int loc)
        {
            if (loc < 0 || loc >= locCount) return "?";
            if (loc < provCount) return provType[loc] == CITY ? provName[loc] + " (city)" : provName[loc];
            return provName[locProv[loc]] + " (" + locCoast[loc] + ")";
        }
    }
}
