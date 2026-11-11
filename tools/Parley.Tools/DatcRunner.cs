// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Runs the DATC (Diplomacy Adjudicator Test Cases) file in tests/datc against the engine.
/// The cases are written for the classic board, whose connection data is loaded from a
/// DPjudge-format file fetched into third_party/ (see scripts/fetch-reference-maps.sh).
/// </summary>
public static class DatcRunner
{
    public sealed class CaseResult
    {
        public string Id = "", Title = "";
        public List<string> Failures = new();
        public bool Passed => Failures.Count == 0;
    }

    private sealed class Block { public string Kind = ""; public List<string> Lines = new(); }

    public static string ReferenceMapPath()
    {
        var env = Environment.GetEnvironmentVariable("PARLEY_REFERENCE_MAP");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(Game.RepoRoot(), "third_party", "maps", "dpjudge", "standard.map");
    }

    public static string CasesPath() => Path.Combine(Game.RepoRoot(), "tests", "datc", "datc_v3.0.txt");

    public static List<CaseResult> RunAll(string only = null)
    {
        var refMap = ReferenceMapPath();
        if (!File.Exists(refMap))
            throw new FileNotFoundException("Reference map not found. Run scripts/fetch-reference-maps.sh first (needs internet). Looked for: " + refMap);
        var imp = DpjudgeImporter.Import(refMap, "reference");
        var results = new List<CaseResult>();
        CaseResult cur = null; List<Block> blocks = null;
        void Finish()
        {
            if (cur == null) return;
            if (only == null || cur.Id == only) { RunCase(imp, cur, blocks); results.Add(cur); }
        }
        foreach (var raw in File.ReadAllLines(CasesPath()))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("#") || line.Trim().Length == 0) continue;
            if (line.StartsWith("CASE "))
            {
                Finish();
                var rest = line.Substring(5).Trim();
                int sp = rest.IndexOf(' ');
                cur = new CaseResult { Id = sp < 0 ? rest : rest.Substring(0, sp), Title = sp < 0 ? "" : rest.Substring(sp + 1) };
                blocks = new List<Block>();
            }
            else if (!char.IsWhiteSpace(line[0])) blocks?.Add(new Block { Kind = line.Trim() });
            else if (blocks != null && blocks.Count > 0) blocks[^1].Lines.Add(line.Trim());
        }
        Finish();
        return results;
    }

    private static (string power, string text) SplitPower(string line)
    {
        int c = line.IndexOf(':');
        return (line.Substring(0, c).Trim(), line.Substring(c + 1).Trim());
    }

    private static void RunCase(DpjudgeImporter.Result imp, CaseResult res, List<Block> blocks)
    {
        var g = Game.FromPmap(imp.Pmap, imp.Aliases);
        var n = g.Notation;
        // spellings the DATC uses that the reference file does not list
        n.AddAlias("Tyrolia", "tyr"); n.AddAlias("Rhur", "ruh"); n.AddAlias("Mid Atlantic Ocean", "mao");
        n.AddAlias("Gulf of Lyon", "lyo"); n.AddAlias("St Petersburg", "stp"); n.AddAlias("Helgoland Bight", "hel");
        n.AddAlias("Livonia", "lvn"); n.AddAlias("Norwegian Sea", "nwg"); n.AddAlias("North Atlantic Ocean", "nao");
        var s = g.State; var adj = g.Adj; var map = g.Map;
        s.Clear(); s.year = 1901;
        Block last = null;
        try
        {
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case "SETUP":
                        foreach (var line in b.Lines)
                        {
                            if (line.StartsWith("own "))
                            {
                                var (pw, list) = SplitPower(line.Substring(4));
                                foreach (var x in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                    s.owner[Need(n.ParsePlace(x).Prov, x)] = Need(map.FindPower(pw), pw);
                            }
                            else
                            {
                                var (pw, text) = SplitPower(line);
                                var o = n.Parse(text);
                                PlaceUnit(g, Need(map.FindPower(pw), pw), o, line);
                            }
                        }
                        break;
                    case "MOVE":
                        {
                            s.season = BoardState.SPRING; s.phase = BoardState.PHASE_MOVE;
                            var parsed = b.Lines.Select(l => (pw: Need(map.FindPower(SplitPower(l).power), l), o: n.Parse(SplitPower(l).text), line: l)).ToList();
                            foreach (var (pw, o, line) in parsed)
                                if (o.Unit.Prov >= 0 && s.provUnit[o.Unit.Prov] < 0) PlaceUnit(g, pw, o, line);
                            adj.ClearOrders();
                            foreach (var (pw, o, line) in parsed) n.ApplyMovement(s, adj, pw, o);
                            adj.ResolveMovement(s);
                            break;
                        }
                    case "RETREAT":
                        if (s.phase != BoardState.PHASE_RETREAT) { res.Failures.Add("expected a retreat phase but there was none"); break; }
                        foreach (var l in b.Lines)
                        {
                            var (pw, text) = SplitPower(l);
                            n.ApplyRetreat(s, adj, Need(map.FindPower(pw), pw), n.Parse(text));
                        }
                        adj.ResolveRetreats(s);
                        break;
                    case "ADJUST":
                        s.season = BoardState.WINTER; s.phase = BoardState.PHASE_ADJUST;
                        adj.ClearOrders();
                        foreach (var l in b.Lines)
                        {
                            var (pw, text) = SplitPower(l);
                            n.ApplyAdjust(s, adj, Need(map.FindPower(pw), pw), n.Parse(text));
                        }
                        adj.ResolveAdjustments(s);
                        break;
                    case "EXPECT":
                        foreach (var l in b.Lines) Check(g, res, last, l);
                        break;
                    default:
                        res.Failures.Add("unknown block " + b.Kind);
                        break;
                }
                if (b.Kind != "EXPECT") last = b;
            }
        }
        catch (Exception ex)
        {
            res.Failures.Add("error: " + ex.Message);
        }
    }

    private static int Need(int v, string what)
    {
        if (v < 0) throw new InvalidDataException("cannot understand '" + what + "'");
        return v;
    }

    private static void PlaceUnit(Game g, int power, ParsedOrder o, string line)
    {
        if (o.UnitType == '?') throw new InvalidDataException("unit type missing in: " + line);
        int loc = g.Notation.UnitLoc(o.UnitType, o.Unit);
        if (loc < 0) throw new InvalidDataException("cannot place unit for: " + line);
        if (g.State.AddUnit(g.Map, o.UnitType == 'F' ? MapData.FLEET : MapData.ARMY, power, loc) < 0)
            throw new InvalidDataException("cannot place unit for: " + line);
    }

    private static HashSet<string> ProvSet(Game g, string list)
    {
        var set = new HashSet<string>();
        foreach (var x in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (x == "-") continue;
            set.Add(g.Map.provId[Need(g.Notation.ParsePlace(x).Prov, x)]);
        }
        return set;
    }

    private static string Show(IEnumerable<string> set)
    {
        var l = set.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return l.Count == 0 ? "(none)" : string.Join(", ", l);
    }

    private static void Check(Game g, CaseResult res, Block last, string line)
    {
        var map = g.Map; var s = g.State; var adj = g.Adj;
        int c = line.IndexOf(':');
        string key = line.Substring(0, c).Trim(); string val = line.Substring(c + 1).Trim();
        switch (key)
        {
            case "moved":
            case "dislodged":
                {
                    var want = ProvSet(g, val);
                    var got = new HashSet<string>();
                    for (int u = 0; u < adj.resCount; u++)
                        if (key == "moved" ? adj.rMoved[u] : adj.rDislodged[u]) got.Add(map.provId[map.locProv[adj.resLoc[u]]]);
                    if (!want.SetEquals(got)) res.Failures.Add($"{key}: expected {Show(want)} but got {Show(got)}");
                    break;
                }
            case "can-retreat":
            case "cannot-retreat":
                {
                    var parts = val.Split('>');
                    int from = Need(g.Notation.ParsePlace(parts[0]).Prov, parts[0]);
                    var destRef = g.Notation.ParsePlace(parts[1]);
                    Need(destRef.Prov, parts[1]);
                    int d = -1;
                    for (int i = 0; i < s.disCount; i++) if (map.locProv[s.disLoc[i]] == from) d = i;
                    bool can = false;
                    if (d >= 0)
                    {
                        int loc = destRef.Coast != null ? map.FindLoc(map.provId[destRef.Prov] + "/" + destRef.Coast) : destRef.Prov;
                        can = adj.RetreatValid(s, d, loc);
                    }
                    else if (key == "can-retreat") res.Failures.Add($"{key}: no dislodged unit at {parts[0].Trim()}");
                    if (d >= 0 && can != (key == "can-retreat")) res.Failures.Add($"{key} {val}: engine says {(can ? "allowed" : "not allowed")}");
                    break;
                }
            case "has":
                {
                    if (!UnitList(g).Contains(NormUnit(g, val))) res.Failures.Add($"has: expected {val}; board has {Show(UnitList(g))}");
                    break;
                }
            case "units":
                {
                    var want = new HashSet<string>();
                    foreach (var x in val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (x != "-") want.Add(NormUnit(g, x));
                    var got = UnitList(g);
                    if (!want.SetEquals(got)) res.Failures.Add($"units: expected {Show(want)} but got {Show(got)}");
                    break;
                }
            default:
                res.Failures.Add("unknown expectation " + key);
                break;
        }
    }

    private static string NormUnit(Game g, string text)
    {
        // "Power A place"
        var w = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        int pw = Need(g.Map.FindPower(w[0]), w[0]);
        int loc = Need(g.Map.FindLoc(w[2]), w[2]);
        return g.Map.powerId[pw] + " " + w[1].ToUpperInvariant() + " " + g.Map.locId[loc];
    }

    private static HashSet<string> UnitList(Game g)
    {
        var set = new HashSet<string>();
        for (int u = 0; u < g.State.unitCount; u++)
            set.Add(g.Map.powerId[g.State.unitPower[u]] + " " + Notation.TypeLetter(g.State.unitType[u]) + " " + g.Map.locId[g.State.unitLoc[u]]);
        return set;
    }
}
