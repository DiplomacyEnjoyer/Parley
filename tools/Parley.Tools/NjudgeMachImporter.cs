// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Text;
using System.Text.RegularExpressions;

namespace Parley.Tools;

/// <summary>
/// Converts the Renaissance-Italy board of the nJudge e-mail adjudicator (its files
/// "map.machiavelli" and the matching starting position, "seed.9") into a .pmap for
/// the Purse and Dagger ruleset. The files are fetched at setup time and never
/// committed; see THIRD_PARTY.md. Format notes are in the judge's own
/// "rules.machiavelli", appendix 1.
///
/// Not carried over: the optional fortresses (a zero-income fortified place), which the
/// judge's standard scenario does not use either.
/// </summary>
public static class NjudgeMachImporter
{
    public sealed class Result
    {
        public string Pmap = "";
        public List<string> Warnings = new();
        public Dictionary<string, List<string>> Aliases = new();
    }

    private sealed class Prov
    {
        public string Name = "", Id = "", Flags = "";
        public List<string> Abbr = new();
        public bool Water, Coastal;
        public char Home;       // power letter, or '\0'
        public int Income; public bool City, Walled, Port, Single;
    }

    // letter, id, adjective, colour, name (the eight powers of the judge's standard scenario)
    private static readonly (char L, string Id, string Adj, string Colour, string Name)[] Powers =
    {
        ('A', "austria", "Austrian", "#c8553d", "Austria"),
        ('F', "france", "French", "#2f6690", "France"),
        ('L', "florence", "Florentine", "#8d6a9f", "Florence"),
        ('I', "milan", "Milanese", "#3a7d44", "Milan"),
        ('N', "naples", "Neapolitan", "#d9a441", "Naples"),
        ('P', "papacy", "Papal", "#b56576", "Papacy"),
        ('T', "turkey", "Turkish", "#4c9f9a", "Turkey"),
        ('V', "venice", "Venetian", "#7f5539", "Venice"),
    };

    // the income table names the capital; this says which power's home country that is ("" = the city itself)
    private static readonly Dictionary<string, string> IncomeOwner = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Austria"] = "austria", ["Florence"] = "florence", ["Marseilles"] = "france", ["Milan"] = "milan",
        ["Naples"] = "naples", ["Rome"] = "papacy", ["Durazzo"] = "turkey", ["Venice"] = "venice", ["Genoa"] = "",
    };

    public static Result Import(string mapPath, string seedPath, string id = "italy1454")
    {
        var res = new Result();
        var lines = File.ReadAllLines(mapPath);
        var provs = new List<Prov>();
        int i = 0;
        for (; i < lines.Length && !lines[i].StartsWith("-1"); i++)
        {
            var m = Regex.Match(lines[i], @"^(.+?),\s+(\S+)\s+(.*)$");
            if (!m.Success) continue;
            var p = new Prov { Name = m.Groups[1].Value.Trim(), Flags = m.Groups[2].Value };
            p.Abbr = m.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.ToLowerInvariant()).ToList();
            if (p.Abbr.Count == 0 || p.Abbr[0] == "xxx") continue;
            p.Id = p.Abbr[0];
            string f = p.Flags;
            p.Water = f[0] == 'w';
            if (Powers.Any(x => x.L == f[0])) p.Home = f[0];
            p.Income = f.Length > 1 && char.IsDigit(f[1]) ? f[1] - '0' : 0;
            char kind = f.Length > 2 ? f[2] : '.';
            // zero income with a city letter is an (unused) fortress, not a city
            p.City = p.Income > 0;
            p.Walled = p.City && (kind == 'P' || kind == 'f');
            p.Port = p.City && (kind == 'P' || kind == 'p');
            p.Single = f.EndsWith("V") && f.Length > 3;
            if (p.Income == 0 && kind != '.') res.Warnings.Add($"{p.Name}: fortress ignored (not used in the standard scenario)");
            provs.Add(p);
        }
        var byAbbr = new Dictionary<string, Prov>();
        foreach (var p in provs) foreach (var a in p.Abbr) byAbbr[a] = p;
        Prov Find(string token)
        {
            string t = token.ToLowerInvariant().Trim();
            if (byAbbr.TryGetValue(t, out var hit)) return hit;
            return provs.FirstOrDefault(p => p.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                ?? provs.FirstOrDefault(p => p.Name.Replace(" ", "").StartsWith(t, StringComparison.OrdinalIgnoreCase));
        }

        // ---- adjacency ----
        var army = new SortedSet<(string, string)>();
        var fleet = new SortedSet<(string, string)>();
        var coasts = new Dictionary<string, SortedSet<string>>();
        var straits = new List<(string a, string b)>();
        for (i++; i < lines.Length && !lines[i].StartsWith("-1"); i++)
        {
            var m = Regex.Match(lines[i], @"^(\w+)-(\w+):\s*(.*)$");
            if (!m.Success) continue;
            string from = m.Groups[1].Value.ToLowerInvariant(), kind = m.Groups[2].Value.ToLowerInvariant();
            if (from == "xxx" || !byAbbr.ContainsKey(from)) continue;
            from = byAbbr[from].Id;
            var targets = m.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (kind == "mv")
            {
                foreach (var t in targets) { var q = Find(t); if (q != null && q.Id != from) army.Add(Pair(from, q.Id)); }
                continue;
            }
            if (kind == "cc")
            {
                // a strait: the same link as the ordinary one, but a fleet in a third province can close it
                foreach (var t in targets) { var q = Find(t); if (q != null) straits.Add((from, q.Id)); }
                continue;
            }
            // xc (one coast; "xv" is a slip in the file), or nc/sc/ec/wc (a named coast)
            string fromLoc = from;
            if (kind != "xc" && kind != "xv")
            {
                if (!coasts.TryGetValue(from, out var set)) coasts[from] = set = new SortedSet<string>();
                set.Add(kind);
                fromLoc = from + "/" + kind;
            }
            byAbbr[from].Coastal = true;
            foreach (var t in targets)
            {
                string[] parts = t.ToLowerInvariant().Split('/');
                var q = Find(parts[0]);
                if (q == null) { res.Warnings.Add("unknown place " + t); continue; }
                q.Coastal = true;
                string toLoc = q.Id;
                if (parts.Length > 1 && parts[1] != "cc") toLoc = q.Id + "/" + parts[1];
                if (q.Id != from) fleet.Add((fromLoc, toLoc));
            }
        }
        // a link written from the one-coast side names the other side's coast; from the coast side it does not: join them
        var fleetLinks = new SortedSet<(string, string)>();
        foreach (var (a, b) in fleet)
        {
            string x = a, y = b;
            string ya = y.Split('/')[0];
            if (!y.Contains('/') && coasts.ContainsKey(ya))
            {
                // find the coast of y that lists x's province
                var hit = fleet.FirstOrDefault(l => l.Item1.StartsWith(ya + "/") && l.Item2.Split('/')[0] == x.Split('/')[0]);
                if (hit.Item1 != null) y = hit.Item1; else { res.Warnings.Add($"cannot tell which coast of {ya} touches {x}"); continue; }
            }
            fleetLinks.Add(Pair(x, y));
        }

        // ---- tables ----
        var famine = new List<string>(); var plague = new List<string>(); var income = new List<string>();
        string mode = "";
        for (i++; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.StartsWith("Famine:")) { mode = "f"; continue; }
            if (l.StartsWith("Plague:")) { mode = "p"; continue; }
            if (l.StartsWith("Storms:")) { mode = ""; continue; }
            if (l.StartsWith("Variable Income:")) { mode = "i"; continue; }
            if (l.StartsWith("#") || l.Trim() == "") { if (mode == "i") mode = ""; continue; }
            var row = Regex.Match(l, @"^\s*(\d+):\s*(.*)$");
            if ((mode == "f" || mode == "p") && row.Success)
            {
                var cells = row.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ids = cells.Select(c => c.StartsWith("--") ? "-" : (Find(c)?.Id ?? "-")).ToList();
                foreach (var c in cells) if (!c.StartsWith("--") && Find(c) == null) res.Warnings.Add("unknown province in a table: " + c);
                while (ids.Count < 11) ids.Add("-");
                (mode == "f" ? famine : plague).Add("x " + (mode == "f" ? "famine " : "plague ") + row.Groups[1].Value + " " + string.Join(' ', ids.Take(11)));
            }
            else if (mode == "i")
            {
                var w = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (w.Length < 8 || !IncomeOwner.TryGetValue(w[0], out var who)) continue;
                string target = who != "" ? who : "city:" + (Find(w[0])?.Id ?? "?");
                income.Add("x income " + target + " " + string.Join(' ', w.Skip(1).Take(7)));
            }
        }

        // ---- starting units ----
        var units = new List<string>();
        foreach (var l in File.ReadAllLines(seedPath))
        {
            var m = Regex.Match(l, @"^(\S)\S?\s+([AFG])\s+(.+)$");
            if (!m.Success) continue;
            char pl = m.Groups[1].Value[0];
            string power = pl == '&' ? "auto" : Powers.FirstOrDefault(x => x.L == pl).Id;
            var p = Find(m.Groups[3].Value.Trim());
            if (power == null || p == null) { res.Warnings.Add("starting unit not understood: " + l); continue; }
            string loc = p.Id;
            if (m.Groups[2].Value == "F" && coasts.TryGetValue(p.Id, out var cs)) loc = p.Id + "/" + cs.First();
            units.Add($"unit {power} {m.Groups[2].Value} {loc}");
        }

        // ---- write ----
        var sb = new StringBuilder();
        sb.AppendLine("parleymap 1");
        sb.AppendLine("# Converted by 'parley import-njudge-mach' from the nJudge adjudicator's map.machiavelli.");
        sb.AppendLine("# Third-party board data: see THIRD_PARTY.md. Do not commit.");
        sb.AppendLine("id " + id);
        sb.AppendLine("name Italy 1454");
        sb.AppendLine("credit Board data from the nJudge e-mail adjudicator (map.machiavelli)");
        sb.AppendLine("rules purse famine loans assassins adjacency varincome");
        sb.AppendLine("seasons Spring Summer Fall Winter");
        sb.AppendLine("convoy coastal");
        sb.AppendLine("start 1454");
        sb.AppendLine("victory 15");
        foreach (var pw in Powers) sb.AppendLine($"power {pw.Id} {pw.Adj} {pw.Colour} {pw.Name}");
        sb.AppendLine("power auto Autonomous #8a8a8a Autonomous");
        sb.AppendLine("x autonomous auto");
        foreach (var p in provs)
        {
            string type = p.Water ? "s" : (p.Coastal ? "c" : "l");
            string home = p.Water ? "-" : (p.Home != '\0' ? Powers.First(x => x.L == p.Home).Id : "*");
            sb.AppendLine($"prov {p.Id} {type} {home} {p.Name}");
            res.Aliases[p.Id] = p.Abbr.Skip(1).Append(p.Name.ToLowerInvariant()).ToList();
        }
        foreach (var p in provs)
            if (p.City) sb.AppendLine($"city {p.Id} {(p.Walled ? "walled" : "open")} {p.Income}{(p.Port ? " port" : "")}{(p.Single ? " single" : "")}");
        foreach (var (pid, set) in coasts) foreach (var c in set) sb.AppendLine($"coast {pid} {c}");
        foreach (var g in army.GroupBy(e => e.Item1)) sb.AppendLine($"army {g.Key}: {string.Join(' ', g.Select(e => e.Item2))}");
        foreach (var g in fleetLinks.GroupBy(e => e.Item1)) sb.AppendLine($"fleet {g.Key}: {string.Join(' ', g.Select(e => e.Item2))}");
        foreach (var u in units) sb.AppendLine(u);
        foreach (var l in famine) sb.AppendLine(l);
        foreach (var l in plague) sb.AppendLine(l);
        foreach (var l in income) sb.AppendLine(l);
        // straits: "x strait <guard> <a> <b>": a fleet in <guard> closes the passage between a and b
        foreach (var (a, b) in straits.Select(s => Pair(s.a, s.b)).Distinct())
        {
            string guard = (a == "ets" || b == "ets") ? "pio" : "mes";
            if (byAbbr.ContainsKey(guard)) sb.AppendLine($"x strait {byAbbr[guard].Id} {a} {b}");
        }
        res.Pmap = sb.ToString();
        return res;
    }

    private static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
