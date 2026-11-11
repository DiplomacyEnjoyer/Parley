// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Turns a jDip-style board SVG (the kind the Python "diplomacy" package and jDip ship:
/// a MapLayer with the drawn board, a MouseLayer with one clickable shape per province,
/// label text, supply-centre markers and per-province unit positions) into a small
/// "art" file for the web client, with every shape matched to a province of a .pmap.
///
/// Third-party SVGs end up inside our web page, so only plain shapes and text are kept:
/// no scripts, links, images, event handlers, styles or external references.
/// The art file is written next to the imported map in maps-local/ and is never committed.
/// </summary>
public static class SvgArt
{
    public sealed class Art
    {
        public string Source { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string Background { get; set; } = "";
        public string Labels { get; set; } = "";
        /// <summary>Province id -> clickable shape markup (geometry only).</summary>
        public Dictionary<string, string> Provs { get; set; } = new();
        /// <summary>Loc id (province or coast) -> where a unit stands.</summary>
        public Dictionary<string, double[]> Units { get; set; } = new();
        /// <summary>Province id -> centre of the supply-centre marker.</summary>
        public Dictionary<string, double[]> Centres { get; set; } = new();
    }

    private static readonly HashSet<string> Shapes = new() { "path", "rect", "polygon", "polyline", "circle", "ellipse", "line" };
    private static readonly HashSet<string> Geometry = new() { "d", "x", "y", "width", "height", "points", "cx", "cy", "r", "rx", "ry", "x1", "y1", "x2", "y2", "transform" };
    private static readonly Dictionary<string, string> ClassStyle = new()
    {
        ["water"] = "fill=\"#c5dfea\" stroke=\"#33475b\" stroke-width=\"1\"",
        ["nopower"] = "fill=\"#f5ecd7\" stroke=\"#4a4033\" stroke-width=\"1\"",
        ["neutral"] = "fill=\"#ddd8cc\" stroke=\"#4a4033\" stroke-width=\"1\"",
        ["impassable"] = "fill=\"#5a5650\" stroke=\"#222\" stroke-width=\"1\"",
    };

    private static string Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v.ToString(CultureInfo.InvariantCulture) : null;

    private static string GeometryAttrs(XElement e)
    {
        var sb = new StringBuilder();
        foreach (var a in e.Attributes())
        {
            string n = a.Name.LocalName;
            if (a.Name.Namespace != XNamespace.None || !Geometry.Contains(n)) continue;
            string v = a.Value;
            // geometry is numbers, letters for path commands, spaces, commas, dots, minus signs and brackets for transforms
            if (v.Any(c => !(char.IsLetterOrDigit(c) || " ,.-+()\t\n\r".Contains(c)))) continue;
            if (n == "transform" && !(v.StartsWith("translate(") || v.StartsWith("matrix(") || v.StartsWith("scale(") || v.StartsWith("rotate("))) continue;
            sb.Append(' ').Append(n).Append("=\"").Append(v).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>Copies plain shapes (recursively through groups), dropping everything else.</summary>
    private static void CopyShapes(XElement e, StringBuilder sb, bool keepStyle)
    {
        string tag = e.Name.LocalName;
        if (e.Name.Namespace != "http://www.w3.org/2000/svg") return;
        if (tag == "g")
        {
            sb.Append("<g").Append(GeometryAttrs(e)).Append('>');
            foreach (var c in e.Elements()) CopyShapes(c, sb, keepStyle);
            sb.Append("</g>");
            return;
        }
        if (!Shapes.Contains(tag)) return;
        sb.Append('<').Append(tag).Append(GeometryAttrs(e));
        if (keepStyle)
        {
            string cls = (string)e.Attribute("class") ?? "";
            sb.Append(' ').Append(ClassStyle.TryGetValue(cls, out var st) ? st : ClassStyle["nopower"]);
        }
        sb.Append("/>");
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static Art Convert(string svgPath, MapData map, Dictionary<string, List<string>> aliases, List<string> warnings)
    {
        // jDip files declare their namespace in an internal DTD, so the DTD is read, but nothing
        // outside the file is ever fetched and entity expansion is capped
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 100000 };
        XDocument doc;
        using (var reader = System.Xml.XmlReader.Create(svgPath, settings)) doc = XDocument.Load(reader);
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = doc.Root ?? throw new InvalidDataException("empty SVG");
        var art = new Art { Source = Path.GetFileName(svgPath) };
        var vb = ((string)root.Attribute("viewBox") ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (vb.Length == 4) { art.Width = (int)Math.Round(double.Parse(vb[2], CultureInfo.InvariantCulture)); art.Height = (int)Math.Round(double.Parse(vb[3], CultureInfo.InvariantCulture)); }
        else { art.Width = (int)double.Parse(((string)root.Attribute("width") ?? "1000").TrimEnd('p', 'x'), CultureInfo.InvariantCulture); art.Height = (int)double.Parse(((string)root.Attribute("height") ?? "1000").TrimEnd('p', 'x'), CultureInfo.InvariantCulture); }

        // name in the SVG -> province / loc of the map (exact id, coast "spa-nc" -> "spa/nc", or any alias)
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int l = 0; l < map.locCount; l++) lookup[map.locId[l]] = map.locId[l];
        foreach (var (id, list) in aliases ?? new())
            foreach (var a in list) if (!lookup.ContainsKey(a.Replace(" ", ""))) lookup[a.Replace(" ", "")] = id;
        string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = name.Trim().TrimStart('_');
            if (lookup.TryGetValue(name, out var id)) return id;
            int dash = name.IndexOfAny(new[] { '-', '/' });
            if (dash > 0 && lookup.TryGetValue(name[..dash], out var p)) { string c = p + "/" + name[(dash + 1)..].ToLowerInvariant(); if (map.FindLoc(c) >= 0) return c; }
            return null;
        }

        var layer = (string id) => root.Descendants().FirstOrDefault(e => (string)e.Attribute("id") == id);

        var bg = layer("MapLayer");
        if (bg == null) throw new InvalidDataException("the SVG has no MapLayer: not a jDip-style board");
        var sb = new StringBuilder();
        sb.Append("<rect x=\"0\" y=\"0\" width=\"").Append(art.Width).Append("\" height=\"").Append(art.Height).Append("\" fill=\"#c5dfea\"/>");
        sb.Append("<g").Append(GeometryAttrs(bg)).Append('>');
        foreach (var c in bg.Elements()) CopyShapes(c, sb, true);
        sb.Append("</g>");
        art.Background = sb.ToString();

        var labels = layer("BriefLabelLayer");
        if (labels != null)
        {
            sb.Clear();
            sb.Append("<g font-family=\"Georgia,serif\" font-style=\"italic\" text-anchor=\"middle\" fill=\"#222\">");
            foreach (var t in labels.Descendants(svg + "text"))
            {
                string x = Num((string)t.Attribute("x") ?? ""), y = Num((string)t.Attribute("y") ?? "");
                if (x == null || y == null) continue;
                string size = ((string)t.Attribute("class") ?? "") == "labeltext18" ? "17" : "21";
                sb.Append("<text x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" font-size=\"").Append(size).Append("\">").Append(Esc(t.Value.Trim())).Append("</text>");
            }
            sb.Append("</g>");
            art.Labels = sb.ToString();
        }

        var mouse = layer("MouseLayer");
        if (mouse == null) throw new InvalidDataException("the SVG has no MouseLayer (clickable provinces)");
        string transform = (string)mouse.Attribute("transform") ?? "";
        var coastShapes = new Dictionary<string, string>();
        foreach (var e in mouse.Elements())
        {
            string id = Resolve((string)e.Attribute("id"));
            if (id == null) { warnings.Add("shape '" + (string)e.Attribute("id") + "' matches no province"); continue; }
            bool coast = id.Contains('/');
            if (coast) id = id[..id.IndexOf('/')];   // coast outlines are kept only in case the province has no outline of its own
            var shape = new StringBuilder();
            if (e.Name.LocalName == "g") foreach (var c in e.Elements()) CopyShapes(c, shape, false);
            else CopyShapes(e, shape, false);
            string markup = transform != "" && GeometryAttrs(mouse) != "" ? "<g" + GeometryAttrs(mouse) + ">" + shape + "</g>" : shape.ToString();
            var into = coast ? coastShapes : art.Provs;
            into[id] = into.TryGetValue(id, out var have) ? have + markup : markup;
        }
        foreach (var (id, markup) in coastShapes) if (!art.Provs.ContainsKey(id)) art.Provs[id] = markup;

        foreach (var p in root.Descendants().Where(e => e.Name.LocalName == "PROVINCE" && e.Name.Namespace != svg))
        {
            string id = Resolve((string)p.Attribute("name"));
            var scm = p.Elements().FirstOrDefault(e => e.Name.LocalName == "SUPPLY_CENTER");
            if (id != null && scm != null && !id.Contains('/'))
            {
                string sx = Num((string)scm.Attribute("x") ?? ""), sy = Num((string)scm.Attribute("y") ?? "");
                if (sx != null && sy != null) art.Centres[id] = new[] { double.Parse(sx, CultureInfo.InvariantCulture), double.Parse(sy, CultureInfo.InvariantCulture) };
            }
            var u = p.Elements().FirstOrDefault(e => e.Name.LocalName == "UNIT");
            if (id == null || u == null) continue;
            string x = Num((string)u.Attribute("x") ?? ""), y = Num((string)u.Attribute("y") ?? "");
            if (x == null || y == null) continue;
            art.Units[id] = new[] { double.Parse(x, CultureInfo.InvariantCulture), double.Parse(y, CultureInfo.InvariantCulture) };
        }
        var scLayer = layer("SupplyCenterLayer");
        if (scLayer != null)
            foreach (var u in scLayer.Elements())
            {
                string id = Resolve(((string)u.Attribute("id") ?? "").Replace("sc_", ""));
                string x = Num((string)u.Attribute("x") ?? ""), y = Num((string)u.Attribute("y") ?? "");
                double w = double.TryParse((string)u.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ww) ? ww : 0;
                if (id == null || x == null || y == null) continue;
                art.Centres[id] = new[] { double.Parse(x, CultureInfo.InvariantCulture) + w / 2, double.Parse(y, CultureInfo.InvariantCulture) + w / 2 };
            }

        for (int p = 0; p < map.provCount; p++)
        {
            if (!art.Provs.ContainsKey(map.provId[p])) warnings.Add("no shape for province " + map.provId[p]);
            if (!art.Units.ContainsKey(map.provId[p])) warnings.Add("no unit position for province " + map.provId[p]);
            if (map.provSc[p] && !art.Centres.ContainsKey(map.provId[p])) warnings.Add("no supply-centre marker for " + map.provId[p]);
        }
        for (int l = map.provCount; l < map.locCount; l++)
            if (!art.Units.ContainsKey(map.locId[l])) warnings.Add("no unit position for coast " + map.locId[l]);
        return art;
    }

    public static void Write(Art art, string path) => File.WriteAllText(path, JsonSerializer.Serialize(art));

    public static Art Read(string path) => JsonSerializer.Deserialize<Art>(File.ReadAllText(path));
}
