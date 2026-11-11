// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Parley.Tools;

/// <summary>
/// Converts a variant from a jDip variant pack (a zip with variants.xml, an adjacency XML
/// file and a board SVG) into a .pmap. The board SVG is jDip-style, so SvgArt turns it into
/// the web client's picture. Only data is read; no jDip code is used.
///
/// Not carried over (a warning is printed): special borders (rules that restrict who may
/// cross where and when), inactive powers' standing units, and rule options other than
/// "build in any owned centre".
/// </summary>
public static class JdipImporter
{
    public sealed class Result
    {
        public string Pmap = "";
        public string Svg = "";            // the SVG markup of the variant's default map, if any
        public string Name = "";
        public List<string> Warnings = new();
        public Dictionary<string, List<string>> Aliases = new();
    }

    /// <summary>The names of the variants in a pack.</summary>
    public static List<string> List(string packZip)
    {
        using var z = ZipFile.OpenRead(packZip);
        return VariantsXml(z).Root!.Elements("VARIANT").Select(v => (string)v.Attribute("name") ?? "").ToList();
    }

    private static XDocument VariantsXml(ZipArchive z)
    {
        var e = z.Entries.FirstOrDefault(x => x.Name.Equals("variants.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("no variants.xml in the pack");
        return Load(e);
    }

    private static XDocument Load(ZipArchiveEntry e)
    {
        using var s = e.Open();
        // the files carry an internal DTD; parse it without fetching anything
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
        using var r = System.Xml.XmlReader.Create(s, settings);
        return XDocument.Load(r);
    }

    private static ZipArchiveEntry Find(ZipArchive z, string uri) =>
        z.Entries.FirstOrDefault(x => x.FullName.Equals(uri, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(Path.GetFileName(uri), StringComparison.OrdinalIgnoreCase));

    private static string Id(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    public static Result Import(string packZip, string variantName, string id)
    {
        var res = new Result();
        using var z = ZipFile.OpenRead(packZip);
        var vx = VariantsXml(z).Root!;
        var v = vx.Elements("VARIANT").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), variantName, StringComparison.OrdinalIgnoreCase))
            ?? vx.Elements("VARIANT").FirstOrDefault(x => ((string)x.Attribute("aliases") ?? "").Split(',').Any(a => a.Trim().Equals(variantName, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidDataException("no variant called '" + variantName + "' (have: " + string.Join(", ", vx.Elements("VARIANT").Select(x => (string)x.Attribute("name"))) + ")");
        res.Name = (string)v.Attribute("name")!;
        var map = v.Element("MAP")!;
        var adjEntry = Find(z, (string)map.Attribute("adjacencyURI")!) ?? throw new InvalidDataException("adjacency file missing");
        var adj = Load(adjEntry).Root!;
        // the map picture
        var graphic = map.Elements("MAP_GRAPHIC").OrderByDescending(g => (string)g.Attribute("default") == "true").FirstOrDefault();
        if (graphic != null)
        {
            var def = vx.Elements("MAP_DEFINITION").FirstOrDefault(d => (string)d.Attribute("id") == (string)graphic.Attribute("ref"));
            var svg = def == null ? null : Find(z, (string)def.Attribute("URI")!);
            if (svg != null) { using var sr = new StreamReader(svg.Open()); res.Svg = sr.ReadToEnd(); }
        }

        // ---- provinces and links ----
        var provs = new List<(string id, string name)>();
        var army = new Dictionary<string, HashSet<string>>();
        var fleet = new Dictionary<string, HashSet<string>>();      // loc -> locs
        var coasts = new Dictionary<string, List<string>>();        // prov -> named coasts
        var hasLand = new HashSet<string>(); var hasFleet = new HashSet<string>();
        if (adj.Element("BORDER_DEFINITIONS") != null) res.Warnings.Add("special borders are defined in this variant; they are not supported and are ignored");
        // every name a province goes by (short name, full name, unique names) -> its id
        var names = new Dictionary<string, string>();
        foreach (var p in adj.Elements("PROVINCE"))
        {
            string pid = Id((string)p.Attribute("shortname")!);
            names[pid] = pid;
            foreach (var u in p.Elements("UNIQUENAME")) names.TryAdd(Id((string)u.Attribute("name")!), pid);
            names.TryAdd(Id((string)p.Attribute("fullname") ?? ""), pid);
        }
        string Norm(string n) { string k = Id(n); return names.TryGetValue(k, out var v2) ? v2 : k; }
        foreach (var p in adj.Elements("PROVINCE"))
        {
            string pid = Id((string)p.Attribute("shortname")!);
            string full = (string)p.Attribute("fullname") ?? pid;
            provs.Add((pid, full));
            res.Aliases[pid] = new List<string> { full };
            foreach (var u in p.Elements("UNIQUENAME")) res.Aliases[pid].Add((string)u.Attribute("name")!);
            foreach (var a in p.Elements("ADJACENCY"))
            {
                string type = (string)a.Attribute("type") ?? "mv";
                var refs = ((string)a.Attribute("refs") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => r.ToLowerInvariant().Replace("-", "/")).ToList();
                if (type == "mv") { hasLand.Add(pid); foreach (var r in refs) Add(army, pid, Norm(r.Split('/')[0])); continue; }
                hasFleet.Add(pid);
                string from = pid;
                if (type != "xc")
                {
                    if (!coasts.TryGetValue(pid, out var cl)) coasts[pid] = cl = new List<string>();
                    if (!cl.Contains(type)) cl.Add(type);
                    from = pid + "/" + type;
                }
                foreach (var r in refs)
                {
                    var parts = r.Split('/');
                    string to = Norm(parts[0]) + (parts.Length > 1 && parts[1] != "mv" && parts[1] != "xc" ? "/" + parts[1] : "");
                    Add(fleet, from, to);
                }
            }
        }
        // impassable areas are written as a province whose only neighbour is itself: leave them out
        var impassable = provs.Where(p => !hasFleet.Contains(p.id) && army.TryGetValue(p.id, out var n) && n.All(x => x == p.id)).Select(p => p.id).ToHashSet();
        provs.RemoveAll(p => impassable.Contains(p.id));
        foreach (var x in impassable) { army.Remove(x); foreach (var set in army.Values) set.Remove(x); }
        var ids = provs.Select(p => p.id).ToHashSet();
        // a fleet link to a province that has named coasts must name one: take the coast that links back
        var fixedFleet = new HashSet<(string, string)>();
        foreach (var (a, set) in fleet)
            foreach (var b in set)
            {
                string bb = b;
                if (!b.Contains('/') && coasts.ContainsKey(b))
                {
                    var back = coasts[b].Select(c => b + "/" + c).Where(c => fleet.TryGetValue(c, out var s2) && s2.Contains(a)).ToList();
                    if (back.Count == 0) { res.Warnings.Add($"fleet link {a} - {b}: no coast of {b} links back; dropped"); continue; }
                    bb = back[0];
                }
                if (!ids.Contains(bb.Split('/')[0])) { res.Warnings.Add($"link to unknown province {bb}; dropped"); continue; }
                fixedFleet.Add(string.CompareOrdinal(a, bb) < 0 ? (a, bb) : (bb, a));
            }
        var armyEdges = new HashSet<(string, string)>();
        foreach (var (a, set) in army)
            foreach (var b in set)
            {
                if (!ids.Contains(b)) { res.Warnings.Add($"army link to unknown province {b}; dropped"); continue; }
                armyEdges.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a));
            }

        // ---- powers, centres, units ----
        var powers = v.Elements("POWER").ToList();
        var active = powers.Where(p => (string)p.Attribute("active") != "false").ToList();
        var pid2 = new Dictionary<string, string>();
        foreach (var p in active)
        {
            string main = Id((string)p.Attribute("name")!);
            pid2[((string)p.Attribute("name")!).ToLowerInvariant()] = main;
            foreach (var alt in ((string)p.Attribute("altnames") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                pid2.TryAdd(alt.ToLowerInvariant(), main);
        }
        string PowerOf(string name) => name == null ? null : pid2.TryGetValue(name.ToLowerInvariant(), out var x) ? x : null;
        foreach (var p in powers.Except(active)) res.Warnings.Add("inactive power " + (string)p.Attribute("name") + " left out; its centres are neutral");
        var home = new Dictionary<string, string>(); var sc = new HashSet<string>(); var owner = new Dictionary<string, string>();
        foreach (var c in v.Elements("SUPPLYCENTER"))
        {
            string p = Norm((string)c.Attribute("province")!);
            sc.Add(p);
            string hp = PowerOf((string)c.Attribute("homepower"));
            if (hp != null) home[p] = hp;
            string ow = PowerOf((string)c.Attribute("owner"));
            if (ow != null) owner[p] = ow;
        }
        if (home.Count == 0) res.Warnings.Add("no power has a home centre: this variant depends on special build rules Parley does not have, and will not play properly");
        var units = new List<string>();
        foreach (var u in v.Elements("INITIALSTATE"))
        {
            string pw = PowerOf((string)u.Attribute("power"));
            if (pw == null) { res.Warnings.Add("unit of an inactive power dropped"); continue; }
            string loc = Norm((string)u.Attribute("province")!);
            string coast = ((string)u.Attribute("unitcoast") ?? "").ToLowerInvariant();
            if (coast != "" && coast != "mv" && coast != "xc") loc += "/" + coast;
            string t = ((string)u.Attribute("unit") ?? "army").ToLowerInvariant().StartsWith("f") ? "F" : "A";
            units.Add($"unit {pw} {t} {loc}");
        }
        string turn = (string)v.Element("STARTINGTIME")?.Attribute("turn") ?? "1901";
        var ym = Regex.Match(turn, "-?[0-9]+");
        int year = ym.Success ? int.Parse(ym.Value) : 1901;
        if (!turn.ToLowerInvariant().Contains("spring")) res.Warnings.Add("the variant does not start in spring (" + turn + "); it starts in spring here");
        if (units.Count == 0) res.Warnings.Add("the variant starts with no units (players build their first units); Parley maps cannot start with a build phase yet");
        int victory = (int?)v.Element("VICTORYCONDITIONS")?.Element("WINNING_SUPPLY_CENTERS")?.Attribute("value") ?? 0;
        bool buildAny = false;
        foreach (var o in v.Element("RULEOPTIONS")?.Elements("RULEOPTION") ?? Enumerable.Empty<XElement>())
        {
            string val = (string)o.Attribute("value") ?? "";
            if (val == "VALUE_BUILDS_ANY_OWNED" || val == "VALUE_BUILDS_ANY_IF_HOME_OWNED") buildAny = true;
            else if (val != "VALUE_BUILDS_HOME_ONLY") res.Warnings.Add("rule option " + (string)o.Attribute("name") + " = " + val + " is not supported");
            if (val == "VALUE_BUILDS_ANY_IF_HOME_OWNED") res.Warnings.Add("builds: 'anywhere if a home centre is still owned' is played as 'anywhere'");
        }

        // colours: the SVG's own, when it has them
        var colors = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(res.Svg, "POWERCOLOR\\s+power=\"([^\"]+)\"\\s+color=\"(#[0-9a-fA-F]{6})\""))
            colors[Id(m.Groups[1].Value)] = m.Groups[2].Value;

        var sb = new StringBuilder();
        sb.Append("# Converted from a jDip variant pack by Parley's importer. Check the licence of the\n");
        sb.Append("# source before redistributing this file.\n");
        sb.Append("parleymap 1\nid ").Append(id).Append('\n');
        sb.Append("name ").Append(res.Name.Replace('\n', ' ')).Append('\n');
        sb.Append("credit Converted from the jDip variant pack (").Append(Path.GetFileName(packZip)).Append(")\n");
        sb.Append("start ").Append(year).Append('\n');
        if (victory > 0) sb.Append("victory ").Append(victory).Append('\n');
        if (buildAny) sb.Append("build any\n");
        sb.Append('\n');
        int pi = 0;
        foreach (var p in active)
        {
            string n = (string)p.Attribute("name")!; string pidv = Id(n);
            string adjctv = ((string)p.Attribute("adjective") ?? n).Replace(' ', '_');
            sb.Append($"power {pidv} {adjctv} {(colors.TryGetValue(pidv, out var col) ? col : DpjudgeImporter.Palette[pi % DpjudgeImporter.Palette.Length])} {n}\n");
            pi++;
        }
        sb.Append('\n');
        foreach (var (p, name) in provs)
        {
            char t = !hasFleet.Contains(p) ? 'l' : (!hasLand.Contains(p) ? 's' : 'c');
            string s = sc.Contains(p) ? (home.TryGetValue(p, out var hp) ? hp : "*") : "-";
            sb.Append($"prov {p} {t} {s} {name}\n");
        }
        sb.Append('\n');
        foreach (var (p, cl) in coasts) foreach (var c in cl) sb.Append($"coast {p} {c}\n");
        sb.Append('\n');
        foreach (var g in armyEdges.GroupBy(e => e.Item1)) sb.Append($"army {g.Key}: {string.Join(' ', g.Select(e => e.Item2))}\n");
        sb.Append('\n');
        foreach (var g in fixedFleet.GroupBy(e => e.Item1)) sb.Append($"fleet {g.Key}: {string.Join(' ', g.Select(e => e.Item2))}\n");
        sb.Append('\n');
        foreach (var (p, ow) in owner) if (!home.TryGetValue(p, out var h) || h != ow) sb.Append($"owner {ow} {p}\n");
        foreach (var u in units) sb.Append(u).Append('\n');
        res.Pmap = sb.ToString();
        return res;
    }

    private static void Add(Dictionary<string, HashSet<string>> d, string a, string b)
    {
        if (!d.TryGetValue(a, out var s)) d[a] = s = new HashSet<string>();
        s.Add(b);
    }
}
