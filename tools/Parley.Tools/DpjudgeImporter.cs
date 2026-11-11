// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Text;

namespace Parley.Tools;

/// <summary>
/// Converts a map in the "DPjudge" text format (the *.map files used by the DPjudge
/// and by the Python "diplomacy" package) into Parley's .pmap format.
///
/// Written from the format's own documentation and sample files; no code is copied
/// from those projects.
///
/// Format notes that matter here:
///   LAND/COAST/WATER/PORT/SHUT  NAME  ABUTS  a b c ...
///   - an entry in UPPER case can be entered by armies and fleets (as terrain allows)
///   - an entry in lower case can be entered by armies only
///   - an entry in Mixed case can be entered by fleets only
///   - "SPA/NC" style names are fleet coasts; the lower-case "spa" line is the army view
///   - SHUT provinces are impassable and are dropped
/// </summary>
public static class DpjudgeImporter
{
    public sealed class Result
    {
        public string Pmap = "";
        public List<string> Warnings = new();
        /// <summary>province id -> alternative names found in the file (useful for order parsers)</summary>
        public Dictionary<string, List<string>> Aliases = new();
    }

    private sealed class Power
    {
        public string Name = "", Adjective = "";
        public List<string> Homes = new();
        public List<string> Owns = new();
        public List<(char type, string loc)> Units = new();
        public bool Dummy;
    }

    private sealed class Ctx
    {
        public int StartYear = 1901;
        public int Victory = 0;
        public bool BuildAny;
        public List<Power> Powers = new();
        public HashSet<string> Unowned = new();
        public Dictionary<string, string> Names = new();           // UPPER key -> full name
        public Dictionary<string, List<string>> Alias = new();     // UPPER key -> aliases
        public Dictionary<string, (string type, List<string> abuts)> Locs = new(); // original-case key
        public Power Current;
        public List<string> Warnings = new();
    }

    internal static readonly string[] Palette =
    {
        "#c8553d", "#2f6690", "#3a7d44", "#8d6a9f", "#d9a441", "#5f6c7b", "#b56576", "#4c9f9a", "#7f5539",
        "#6a994e", "#bc4749", "#577590", "#f3722c", "#43aa8b", "#9d4edd", "#90be6d", "#277da1", "#f94144",
        "#f8961e", "#4d908e", "#7209b7", "#bb9457", "#588157", "#e56b6f"
    };

    public static Result Import(string path, string id = null, string name = null)
    {
        var ctx = new Ctx();
        Load(path, ctx, 0);
        return Build(ctx, id ?? Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), name);
    }

    private static void Load(string path, Ctx ctx, int depth)
    {
        if (depth > 8) { ctx.Warnings.Add("include depth exceeded at " + path); return; }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length == 0) continue;

            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                var full = line.Substring(0, eq).Trim();
                var al = line.Substring(eq + 1).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (al.Length == 0) continue;
                var key = al[0].ToUpperInvariant();
                ctx.Names[key] = full;
                ctx.Alias[key] = al.Skip(1).ToList();
                continue;
            }

            var w = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var up = w[0].ToUpperInvariant();
            switch (up)
            {
                case "BEGIN":
                    foreach (var x in w) if (int.TryParse(x, out int y)) { ctx.StartYear = y; break; }
                    break;
                case "VICTORY":
                    foreach (var x in w) if (int.TryParse(x, out int v)) { ctx.Victory = v; break; }
                    break;
                case "RULE":
                case "RULES":
                    foreach (var x in w.Skip(1))
                    {
                        if (x.ToUpperInvariant() == "BUILD_ANY") ctx.BuildAny = true;
                        else ctx.Warnings.Add("rule not supported, ignored: " + x);
                    }
                    break;
                case "MAP":
                case "USE":
                case "USES":
                    foreach (var x in w.Skip(1))
                    {
                        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                        var inc = Path.Combine(dir, x.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? x : x + ".map");
                        if (File.Exists(inc)) Load(inc, ctx, depth + 1);
                        else ctx.Warnings.Add("included map not found: " + x);
                    }
                    break;
                case "UNPLAYED":
                    {
                        var names = w.Skip(1).Select(x => x.Trim(',').ToUpperInvariant()).Where(x => x.Length > 0).ToList();
                        if (names.Count == 0) { if (ctx.Current != null) ctx.Powers.Remove(ctx.Current); }
                        else if (names[0] == "ALL")
                        {
                            var keep = names.Count > 1 && names[1] == "EXCEPT" ? names.Skip(2).ToHashSet() : new HashSet<string>();
                            ctx.Powers.RemoveAll(p => !keep.Contains(p.Name));
                        }
                        else ctx.Powers.RemoveAll(p => names.Contains(p.Name));
                        break;
                    }
                case "UNOWNED":
                case "NEUTRAL":
                case "CENTERS":
                    foreach (var x in w.Skip(1)) ctx.Unowned.Add(x.ToUpperInvariant());
                    break;
                case "DUMMY":
                case "DUMMIES":
                    if (w.Length == 1) { if (ctx.Current != null) ctx.Current.Dummy = true; }
                    else foreach (var p in ctx.Powers) if (w.Skip(1).Any(x => x.Trim(',').ToUpperInvariant() == p.Name)) p.Dummy = true;
                    break;
                case "A":
                case "F":
                    if (ctx.Current != null && w.Length >= 2) ctx.Current.Units.Add((up[0], w[1].ToUpperInvariant()));
                    break;
                case "OWNS":
                    if (ctx.Current != null) ctx.Current.Owns.AddRange(w.Skip(1).Select(x => x.ToUpperInvariant()));
                    break;
                case "HOME":
                case "HOMES":
                    if (ctx.Current != null) ctx.Current.Homes = w.Skip(1).Select(x => x.ToUpperInvariant()).ToList();
                    break;
                case "UNITS":
                    if (ctx.Current != null) ctx.Current.Units.Clear();
                    break;
                case "DROP":
                    foreach (var x in w.Skip(1))
                        foreach (var k in ctx.Locs.Keys.Where(k => k.ToUpperInvariant().Split('/')[0] == x.ToUpperInvariant()).ToList()) ctx.Locs.Remove(k);
                    break;
                case "LAND":
                case "COAST":
                case "WATER":
                case "PORT":
                case "SHUT":
                case "AMEND":
                    {
                        if (w.Length < 2) break;
                        var key = w[1];
                        // a redefinition replaces any earlier spelling of the same place
                        foreach (var k in ctx.Locs.Keys.Where(k => k != key && k.ToUpperInvariant() == key.ToUpperInvariant()).ToList()) ctx.Locs.Remove(k);
                        List<string> abuts;
                        string type = up;
                        if (up == "AMEND")
                        {
                            if (!ctx.Locs.TryGetValue(key, out var old)) { ctx.Warnings.Add("AMEND of unknown place " + key); break; }
                            abuts = old.abuts; type = old.type;
                        }
                        else abuts = new List<string>();
                        foreach (var d in w.Skip(3))
                        {
                            if (d.StartsWith("-")) abuts.RemoveAll(a => a.ToUpperInvariant().StartsWith(d.Substring(1).ToUpperInvariant()));
                            else abuts.Add(d);
                        }
                        ctx.Locs[key] = (type, abuts);
                        break;
                    }
                default:
                    if (w.Length >= 2 && w[1].StartsWith("("))
                    {
                        // POWER (ADJECTIVE[:letter]) home centres...
                        var pname = up;
                        var adj = w[1].Trim('(', ')');
                        int colon = adj.IndexOf(':');
                        if (colon >= 0) adj = adj.Substring(0, colon);
                        adj = adj.Replace('+', ' ');
                        var p = ctx.Powers.FirstOrDefault(q => q.Name == pname);
                        if (p == null) { p = new Power { Name = pname }; ctx.Powers.Add(p); }
                        p.Adjective = adj;
                        p.Homes = w.Skip(2).Select(x => x.ToUpperInvariant()).ToList();
                        p.Units.Clear();
                        ctx.Current = p;
                    }
                    else if (ctx.Powers.Any(q => q.Name == up) && w.Length == 1)
                    {
                        ctx.Current = ctx.Powers.First(q => q.Name == up);
                    }
                    else ctx.Warnings.Add("line not understood, ignored: " + line);
                    break;
            }
        }
    }

    internal static string TitleCase(string s)
    {
        var sb = new StringBuilder();
        bool start = true;
        foreach (char c in s)
        {
            if (c == '-' || c == ' ' || c == '_') { sb.Append(' '); start = true; }
            else { sb.Append(start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)); start = false; }
        }
        return sb.ToString();
    }

    private static Result Build(Ctx ctx, string id, string name)
    {
        var res = new Result();
        res.Warnings.AddRange(ctx.Warnings);

        // ---- provinces ----
        var provType = new SortedDictionary<string, string>(StringComparer.Ordinal); // base UPPER -> LAND/COAST/WATER
        var provCoasts = new Dictionary<string, SortedSet<string>>();
        var shut = new HashSet<string>();
        foreach (var (key, val) in ctx.Locs)
        {
            var parts = key.ToUpperInvariant().Split('/');
            if (val.type == "SHUT") { shut.Add(parts[0]); continue; }
        }
        foreach (var (key, val) in ctx.Locs)
        {
            var parts = key.ToUpperInvariant().Split('/');
            var b = parts[0];
            if (shut.Contains(b)) continue;
            var t = val.type == "PORT" ? "COAST" : val.type;
            if (parts.Length > 1)
            {
                if (!provCoasts.TryGetValue(b, out var set)) provCoasts[b] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(parts[1].ToLowerInvariant());
                if (!provType.ContainsKey(b)) provType[b] = "COAST";
            }
            else provType[b] = t;
        }

        string Pid(string upperBase) => upperBase.ToLowerInvariant();
        var armyEdges = new SortedSet<(string, string)>();
        var fleetEdges = new SortedSet<(string, string)>();
        foreach (var (key, val) in ctx.Locs)
        {
            var kp = key.ToUpperInvariant().Split('/');
            var kb = kp[0];
            if (!provType.ContainsKey(kb)) continue;
            string kCoast = kp.Length > 1 ? kp[1].ToLowerInvariant() : "";
            bool keyIsLower = key == key.ToLowerInvariant();
            foreach (var e in val.abuts)
            {
                var ep = e.ToUpperInvariant().Split('/');
                var eb = ep[0];
                if (eb == kb) continue;
                if (!provType.ContainsKey(eb)) { if (!shut.Contains(eb)) res.Warnings.Add($"{key} abuts unknown place {e}"); continue; }
                string eCoast = ep.Length > 1 ? ep[1].ToLowerInvariant() : "";
                bool entryLower = char.IsLower(e[0]);
                bool entryMixed = !entryLower && e.Skip(1).Any(char.IsLower);

                // armies: any non-water pair, unless the entry is marked fleet-only
                if (kCoast == "" && !entryMixed && provType[kb] != "WATER" && provType[eb] != "WATER")
                {
                    var a = Pid(kb); var b2 = Pid(eb);
                    armyEdges.Add(string.CompareOrdinal(a, b2) < 0 ? (a, b2) : (b2, a));
                }
                // fleets: from a real fleet position to a real fleet position, unless marked army-only
                if (!keyIsLower || !provCoasts.ContainsKey(kb))
                {
                    if (entryLower) continue;
                    if (provType[kb] == "LAND" || provType[eb] == "LAND") continue;
                    if (provCoasts.ContainsKey(kb) && kCoast == "") continue;
                    if (provCoasts.ContainsKey(eb) && eCoast == "") continue;
                    if (!provCoasts.ContainsKey(eb)) eCoast = "";
                    var a = Pid(kb) + (kCoast == "" ? "" : "/" + kCoast);
                    var b2 = Pid(eb) + (eCoast == "" ? "" : "/" + eCoast);
                    fleetEdges.Add(string.CompareOrdinal(a, b2) < 0 ? (a, b2) : (b2, a));
                }
            }
        }

        // ---- powers and centres ----
        var played = ctx.Powers.Where(p => !p.Dummy).ToList();
        foreach (var p in ctx.Powers.Where(p => p.Dummy))
        {
            foreach (var c in p.Homes.Concat(p.Owns)) ctx.Unowned.Add(c);
            if (p.Units.Count > 0) res.Warnings.Add($"non-player power {p.Name}: its {p.Units.Count} standing units were dropped (not supported)");
        }
        foreach (var p in played) foreach (var o in p.Owns) ctx.Unowned.Add(o);
        var homeOf = new Dictionary<string, string>();
        foreach (var p in played)
            foreach (var h in p.Homes)
            {
                if (!provType.ContainsKey(h)) { res.Warnings.Add($"{p.Name} home centre {h} is not on the map"); continue; }
                homeOf[h] = p.Name.ToLowerInvariant();
            }
        var neutral = ctx.Unowned.Where(c => provType.ContainsKey(c) && !homeOf.ContainsKey(c)).ToHashSet();

        var sb = new StringBuilder();
        sb.Append("# Converted from a DPjudge-format map by Parley's importer. Check the licence of the\n");
        sb.Append("# source map before redistributing this file.\n");
        sb.Append("parleymap 1\n");
        sb.Append("id ").Append(id).Append('\n');
        sb.Append("name ").Append(name ?? TitleCase(id)).Append('\n');
        sb.Append("start ").Append(ctx.StartYear).Append('\n');
        if (ctx.Victory > 0) sb.Append("victory ").Append(ctx.Victory).Append('\n');
        if (ctx.BuildAny) sb.Append("build any\n");
        sb.Append('\n');
        int pi = 0;
        foreach (var p in played)
        {
            sb.Append("power ").Append(p.Name.ToLowerInvariant()).Append(' ')
              .Append(TitleCase(p.Adjective).Replace(' ', '_')).Append(' ')
              .Append(Palette[pi % Palette.Length]).Append(' ')
              .Append(TitleCase(p.Name)).Append('\n');
            pi++;
        }
        sb.Append('\n');
        foreach (var (b, t) in provType)
        {
            string sc = homeOf.TryGetValue(b, out var hp) ? hp : (neutral.Contains(b) ? "*" : "-");
            string full = ctx.Names.TryGetValue(b, out var fn) ? fn : TitleCase(b);
            sb.Append("prov ").Append(Pid(b)).Append(' ').Append(t == "LAND" ? 'l' : t == "WATER" ? 's' : 'c').Append(' ')
              .Append(sc).Append(' ').Append(full).Append('\n');
            var al = new List<string> { full };
            if (ctx.Alias.TryGetValue(b, out var extra)) al.AddRange(extra.Select(x => x.Replace('+', ' ')));
            res.Aliases[Pid(b)] = al;
        }
        sb.Append('\n');
        foreach (var (b, set) in provCoasts.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var c in set) sb.Append("coast ").Append(Pid(b)).Append(' ').Append(c).Append('\n');
        sb.Append('\n');
        foreach (var g in armyEdges.GroupBy(e => e.Item1))
            sb.Append("army ").Append(g.Key).Append(": ").Append(string.Join(' ', g.Select(e => e.Item2))).Append('\n');
        sb.Append('\n');
        foreach (var g in fleetEdges.GroupBy(e => e.Item1))
            sb.Append("fleet ").Append(g.Key).Append(": ").Append(string.Join(' ', g.Select(e => e.Item2))).Append('\n');
        sb.Append('\n');
        foreach (var p in played)
        {
            foreach (var o in p.Owns.Where(o => provType.ContainsKey(o)))
                sb.Append("owner ").Append(p.Name.ToLowerInvariant()).Append(' ').Append(Pid(o)).Append('\n');
            foreach (var (type, loc) in p.Units)
                sb.Append("unit ").Append(p.Name.ToLowerInvariant()).Append(' ').Append(type).Append(' ').Append(loc.ToLowerInvariant()).Append('\n');
        }
        res.Pmap = sb.ToString();
        return res;
    }
}
