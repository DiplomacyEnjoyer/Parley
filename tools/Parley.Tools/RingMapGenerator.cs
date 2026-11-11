// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Globalization;
using System.Text;

namespace Parley.Tools;

/// <summary>
/// Generates Parley's original "Ring" maps: a ring-shaped continent around an inner
/// sea with an island in the middle, an outer ocean, and one identical slice of land
/// per player. Because every player's slice is the same, the map is fair for any
/// number of players from 2 upwards.
///
/// One slice ("realm"), from the middle of the board outwards:
///   Bay      inner sea
///   Marsh, Haven, Wood     inner coast   (Haven = home port; Marsh and Wood = neutral centres)
///   Hold                   inland        (home capital)
///   Downs, Gate, Heath     outer coast   (Gate = home centre)
///   Deep     outer ocean (straddles the border with the previous realm)
///   Narrows  a neutral centre on the border with the previous realm that touches both
///            seas. By default fleets can sail through it (a canal). With the
///            "isthmus" option it has two separate coasts instead.
/// In the very middle sits the Crown, a neutral island centre reachable only by sea.
/// </summary>
public static class RingMapGenerator
{
    private static readonly (string stem, string code)[] Stems =
    {
        ("Alder", "al"), ("Brann", "br"), ("Corvin", "co"), ("Dunmar", "du"), ("Esker", "es"), ("Farrow", "fa"),
        ("Garrow", "ga"), ("Hollin", "ho"), ("Isen", "is"), ("Jessamy", "je"), ("Kestrel", "ke"), ("Lorn", "lo"),
        ("Marrow", "ma"), ("Nyth", "ny"), ("Orrin", "or"), ("Penhal", "pe"), ("Quill", "qu"), ("Roven", "ro"),
        ("Sable", "sa"), ("Thorne", "th"), ("Umber", "um"), ("Vesper", "ve"), ("Wyck", "wy"), ("Yarrow", "ya"),
        ("Zephyr", "ze"), ("Ashby", "as"), ("Brindle", "bi"), ("Cobalt", "cb"), ("Dapple", "da"), ("Ember", "em"),
    };

    private static readonly string[] Palette =
    {
        "#c8553d", "#2f6690", "#3a7d44", "#8d6a9f", "#d9a441", "#5f6c7b", "#b56576", "#4c9f9a", "#7f5539",
        "#6a994e", "#bc4749", "#577590", "#f3722c", "#43aa8b", "#9d4edd", "#90be6d", "#277da1", "#f94144",
        "#f8961e", "#4d908e", "#7209b7", "#bb9457", "#588157", "#e56b6f", "#3d5a80", "#ee6c4d", "#84a59d",
        "#6d597a", "#b5838d", "#e09f3e"
    };

    private sealed class Cell
    {
        public string Id = "", Name = "", Sc = "-";
        public char Type;            // l, c, s
        public int RingLo, RingHi;   // inclusive
        public double ALo, AHi;      // angular units; AHi > ALo; may exceed the full circle (wraps)
        public bool Full;            // whole circle (the Crown)
        public int Realm;
        public string Role = "";
    }

    private static readonly double[] RingInner = { 0, 62, 175, 262, 348, 428 };
    private static readonly double[] RingOuter = { 62, 175, 262, 348, 428, 496 };
    private const double W = 12; // angular units per realm

    public static string Generate(int players, bool isthmus = false)
    {
        if (players < 2) throw new ArgumentException("a ring map needs at least 2 players");
        int n = players;
        double total = n * W;
        var cells = new List<Cell>();
        if (n > Stems.Length + 260) throw new ArgumentException("ring maps support at most " + (Stems.Length + 260) + " players");
        // beyond the named realms, ids are a digit and a letter ("0a", "0b", ...), which cannot clash with the names above
        (string stem, string code) Stem(int i) => i < Stems.Length ? Stems[i]
            : ("Realm" + (i + 1), "" + (char)('0' + (i - Stems.Length) / 26) + (char)('a' + (i - Stems.Length) % 26));

        cells.Add(new Cell { Id = "crn", Name = "The Crown", Type = 'c', Sc = "*", RingLo = 0, RingHi = 0, Full = true, ALo = 0, AHi = total, Realm = -1, Role = "crown" });
        for (int i = 0; i < n; i++)
        {
            var (stem, code) = Stem(i);
            string pw = stem.ToLowerInvariant();
            double b = i * W;
            void Add(string role, string suffix, string name, char type, string sc, int rLo, int rHi, double aLo, double aHi) =>
                cells.Add(new Cell { Id = code + suffix, Name = stem + " " + name, Type = type, Sc = sc, RingLo = rLo, RingHi = rHi, ALo = b + aLo, AHi = b + aHi, Realm = i, Role = role });
            Add("bay", "s", "Bay", 's', "-", 1, 1, 0, 12);
            Add("narrows", "k", "Narrows", 'c', "*", 2, 4, -1.5, 1.5);
            Add("marsh", "l", "Marsh", 'c', "*", 2, 2, 1.5, 4.5);
            Add("haven", "p", "Haven", 'c', pw, 2, 2, 4.5, 7.5);
            Add("wood", "r", "Wood", 'c', "*", 2, 2, 7.5, 10.5);
            Add("hold", "c", "Hold", 'l', pw, 3, 3, 1.5, 10.5);
            Add("downs", "m", "Downs", 'c', "-", 4, 4, 1.5, 4.5);
            Add("gate", "g", "Gate", 'c', pw, 4, 4, 4.5, 7.5);
            Add("heath", "n", "Heath", 'c', "-", 4, 4, 7.5, 10.5);
            Add("deep", "o", "Deep", 's', "-", 5, 5, -6, 6);
        }

        bool Overlap(Cell x, Cell y)
        {
            if (x.Full || y.Full) return true;
            foreach (double shift in new[] { -total, 0, total })
                if (Math.Min(x.AHi, y.AHi + shift) - Math.Max(x.ALo, y.ALo + shift) > 0.01) return true;
            return false;
        }
        bool Abut(Cell x, Cell y)
        {
            if (x.Full || y.Full) return false;
            foreach (double shift in new[] { -total, 0, total })
                if (Math.Abs(x.AHi - (y.ALo + shift)) < 0.01 || Math.Abs(y.AHi + shift - x.ALo) < 0.01) return true;
            return false;
        }
        bool Touch(Cell x, Cell y)
        {
            if (x == y) return false;
            bool shareRing = x.RingLo <= y.RingHi && y.RingLo <= x.RingHi;
            if (shareRing && Abut(x, y)) return true;
            bool stacked = x.RingHi + 1 == y.RingLo || y.RingHi + 1 == x.RingLo;
            return stacked && Overlap(x, y);
        }

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        string variant = isthmus ? "i" : "";
        sb.Append("# Parley original map, generated by `parley gen-ring ").Append(n).Append(isthmus ? " isthmus" : "").Append("`.\n");
        sb.Append("# Map data and layout: CC BY-SA 4.0, the Parley contributors.\n");
        sb.Append("parleymap 1\n");
        sb.Append("id ring").Append(n).Append(variant).Append('\n');
        sb.Append("name Ring of ").Append(n).Append(isthmus ? " (isthmus)" : "").Append('\n');
        sb.Append("credit Original map for Parley. Every realm is identical, so any player count is fair.\n");
        sb.Append("license CC-BY-SA-4.0\n");
        sb.Append("start 1\n");
        sb.Append("size 1000 1000\n\n");
        for (int i = 0; i < n; i++)
        {
            var (stem, _) = Stem(i);
            sb.Append("power ").Append(stem.ToLowerInvariant()).Append(' ').Append(stem).Append(' ').Append(Palette[i % Palette.Length]).Append(' ').Append(stem).Append('\n');
        }
        sb.Append('\n');
        foreach (var c in cells) sb.Append("prov ").Append(c.Id).Append(' ').Append(c.Type).Append(' ').Append(c.Sc).Append(' ').Append(c.Name).Append('\n');
        sb.Append('\n');
        if (isthmus)
        {
            foreach (var c in cells.Where(c => c.Role == "narrows")) sb.Append("coast ").Append(c.Id).Append(" ic\ncoast ").Append(c.Id).Append(" oc\n");
            sb.Append('\n');
        }

        // which sea cells does each cell touch?
        var seasOf = cells.ToDictionary(c => c, c => cells.Where(s => s.Type == 's' && Touch(c, s)).ToList());
        string FleetLoc(Cell c, Cell other)
        {
            if (!isthmus || c.Role != "narrows") return c.Id;
            // inner coast faces the bays and the inner-coast provinces; outer coast the rest
            bool inner = other.RingHi <= 2;
            return c.Id + (inner ? "/ic" : "/oc");
        }
        var army = new StringBuilder(); var fleet = new StringBuilder();
        for (int a = 0; a < cells.Count; a++)
        {
            var la = new List<string>(); var lf = new List<string>();
            for (int b = a + 1; b < cells.Count; b++)
            {
                var x = cells[a]; var y = cells[b];
                if (!Touch(x, y)) continue;
                if (x.Type != 's' && y.Type != 's' && x.Role != "crown" && y.Role != "crown") la.Add(y.Id);
                bool fleetOk;
                if (x.Type == 'l' || y.Type == 'l') fleetOk = false;
                else if (x.Type == 's' || y.Type == 's') fleetOk = true;
                else fleetOk = seasOf[x].Intersect(seasOf[y]).Any(); // two coasts sharing a shoreline
                if (fleetOk) lf.Add(FleetLoc(x, y) + ">" + FleetLoc(y, x));
            }
            if (la.Count > 0) army.Append("army ").Append(cells[a].Id).Append(": ").Append(string.Join(' ', la)).Append('\n');
            foreach (var g in lf.GroupBy(e => e.Split('>')[0]))
                fleet.Append("fleet ").Append(g.Key).Append(": ").Append(string.Join(' ', g.Select(e => e.Split('>')[1]))).Append('\n');
        }
        sb.Append(army).Append('\n').Append(fleet).Append('\n');

        for (int i = 0; i < n; i++)
        {
            var (stem, code) = Stem(i);
            string pw = stem.ToLowerInvariant();
            sb.Append("unit ").Append(pw).Append(" F ").Append(code).Append("p\n");
            sb.Append("unit ").Append(pw).Append(" A ").Append(code).Append("c\n");
            sb.Append("unit ").Append(pw).Append(" A ").Append(code).Append("g\n");
        }
        sb.Append('\n');

        // layout: label point and outline polygon for every province
        (int x, int y) Pt(double r, double a)
        {
            double th = 2 * Math.PI * a / total - Math.PI / 2;
            return ((int)Math.Round(500 + r * Math.Cos(th)), (int)Math.Round(500 + r * Math.Sin(th)));
        }
        foreach (var c in cells)
        {
            double r0 = RingInner[c.RingLo], r1 = RingOuter[c.RingHi];
            if (c.Full)
            {
                sb.Append("xy ").Append(c.Id).Append(" 500 500\n");
                var pts = new List<string>();
                for (int k = 0; k < 28; k++) { var p = Pt(r1, total * k / 28); pts.Add(p.x.ToString(inv) + "," + p.y.ToString(inv)); }
                sb.Append("shape ").Append(c.Id).Append(' ').Append(string.Join(' ', pts)).Append('\n');
                continue;
            }
            var mid = Pt((r0 + r1) / 2, (c.ALo + c.AHi) / 2);
            sb.Append("xy ").Append(c.Id).Append(' ').Append(mid.x).Append(' ').Append(mid.y).Append('\n');
            double span = c.AHi - c.ALo;
            int steps = Math.Max(2, (int)Math.Ceiling(span / total * 72));
            var poly = new List<string>();
            for (int k = 0; k <= steps; k++) { var p = Pt(r1, c.ALo + span * k / steps); poly.Add(p.x.ToString(inv) + "," + p.y.ToString(inv)); }
            for (int k = steps; k >= 0; k--) { var p = Pt(r0, c.ALo + span * k / steps); poly.Add(p.x.ToString(inv) + "," + p.y.ToString(inv)); }
            sb.Append("shape ").Append(c.Id).Append(' ').Append(string.Join(' ', poly)).Append('\n');
        }
        return sb.ToString();
    }
}
