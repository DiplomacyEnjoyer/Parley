// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Text;
using System.Text.RegularExpressions;
using Parley.Engine;

namespace Parley.Tools;

public enum OrderKind { Invalid, Hold, Move, SupportHold, SupportMove, Convoy, Build, Remove, Waive }

public sealed class PlaceRef
{
    public int Prov = -1;
    public string Coast;      // null when no coast was written
    public string Text = "";
}

public sealed class ParsedOrder
{
    public OrderKind Kind = OrderKind.Invalid;
    public char UnitType = '?';
    public PlaceRef Unit = new();
    public PlaceRef Target = new(); // the unit being supported / convoyed
    public PlaceRef Dest = new();
    public bool Via;
    public string Raw = "";
}

/// <summary>
/// Reads and writes orders as text ("A lon - bel", "F North Sea Convoys A London - Belgium").
/// Used by tests, the simulator log and the web client. The VR world never needs this:
/// there, orders are built by tapping, straight from the legal-options generator.
///
/// Lenient parsing follows the DATC author's preferences (issues 4.B / 4.C): a missing
/// coast is filled in when only one is possible; a wrong coast or an ambiguous one
/// makes the order void; the unit letter is not trusted.
/// </summary>
public sealed class Notation
{
    private readonly MapData map;
    private readonly Dictionary<string, int> names = new();

    public Notation(MapData map, Dictionary<string, List<string>> aliases = null)
    {
        this.map = map;
        for (int p = 0; p < map.provCount; p++)
        {
            names[Norm(map.provId[p])] = p;
            names.TryAdd(Norm(map.provName[p]), p);
        }
        if (aliases != null)
            foreach (var (id, list) in aliases)
            {
                int p = map.FindProv(id);
                if (p < 0) continue;
                foreach (var a in list) if (!a.Contains('/') && !a.Contains('(')) names.TryAdd(Norm(a), p);
            }
    }

    public void AddAlias(string alias, string provId)
    {
        int p = map.FindProv(provId);
        if (p >= 0) names[Norm(alias)] = p;
    }

    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public PlaceRef ParsePlace(string text)
    {
        var r = new PlaceRef { Text = text.Trim() };
        var t = r.Text.ToLowerInvariant();
        var m = Regex.Match(t, @"^(.*?)\s*(?:\(\s*([a-z]{2})\s*\)|/\s*([a-z]{2}))$");
        if (m.Success)
        {
            r.Coast = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            t = m.Groups[1].Value;
        }
        if (names.TryGetValue(Norm(t), out int p)) r.Prov = p;
        return r;
    }

    private (char type, PlaceRef place) ParseUnit(string text)
    {
        var t = text.Trim();
        var m = Regex.Match(t, @"^(a|f|army|fleet)\s+(.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var place = ParsePlace(m.Groups[2].Value);
            if (place.Prov >= 0) return (char.ToUpperInvariant(m.Groups[1].Value[0]), place);
        }
        return ('?', ParsePlace(t));
    }

    public ParsedOrder Parse(string raw)
    {
        var o = new ParsedOrder { Raw = raw };
        var t = raw.Trim();
        Match m;
        if ((m = Regex.Match(t, @"^(build|remove|disband)\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            (o.UnitType, o.Unit) = ParseUnit(m.Groups[2].Value);
            o.Kind = m.Groups[1].Value.ToLowerInvariant() == "build" ? OrderKind.Build : OrderKind.Remove;
            return o;
        }
        if (Regex.IsMatch(t, @"^waive\b", RegexOptions.IgnoreCase)) { o.Kind = OrderKind.Waive; return o; }

        var via = Regex.Match(t, @"\s+via\s+convoy\s*$", RegexOptions.IgnoreCase);
        if (via.Success) { o.Via = true; t = t.Substring(0, via.Index); }

        const string moveSep = @"\s+-\s+|\s*->\s*";
        if ((m = Regex.Match(t, @"^(.+?)\s+(?:supports|support|s)\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            (o.UnitType, o.Unit) = ParseUnit(m.Groups[1].Value);
            var rest = Regex.Replace(m.Groups[2].Value, @"\s+(holds|hold|h)$", "", RegexOptions.IgnoreCase);
            var parts = Regex.Split(rest, moveSep);
            if (parts.Length == 2)
            {
                o.Kind = OrderKind.SupportMove;
                o.Target = ParseUnit(parts[0]).place;
                o.Dest = ParsePlace(parts[1]);
            }
            else { o.Kind = OrderKind.SupportHold; o.Target = ParseUnit(rest).place; }
            return o;
        }
        if ((m = Regex.Match(t, @"^(.+?)\s+(?:convoys|convoy|c)\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            (o.UnitType, o.Unit) = ParseUnit(m.Groups[1].Value);
            var parts = Regex.Split(m.Groups[2].Value, moveSep);
            if (parts.Length == 2)
            {
                o.Kind = OrderKind.Convoy;
                o.Target = ParseUnit(parts[0]).place;
                o.Dest = ParsePlace(parts[1]);
            }
            return o;
        }
        {
            var parts = Regex.Split(t, moveSep);
            if (parts.Length == 2)
            {
                (o.UnitType, o.Unit) = ParseUnit(parts[0]);
                o.Dest = ParsePlace(parts[1]);
                o.Kind = OrderKind.Move;
                return o;
            }
        }
        t = Regex.Replace(t, @"\s+(holds|hold|h)$", "", RegexOptions.IgnoreCase);
        (o.UnitType, o.Unit) = ParseUnit(t);
        o.Kind = o.Unit.Prov >= 0 ? OrderKind.Hold : OrderKind.Invalid;
        return o;
    }

    // ---------------------------------------------------------------------
    // Turning parsed orders into engine orders
    // ---------------------------------------------------------------------

    /// <summary>Loc for a unit of this type standing at 'place', or -1.</summary>
    public int UnitLoc(char unitType, PlaceRef place)
    {
        if (place.Prov < 0) return -1;
        if (unitType != 'F' || map.provCoastCount[place.Prov] == 0) return place.Prov;
        return place.Coast == null ? -1 : map.FindLoc(map.provId[place.Prov] + "/" + place.Coast);
    }

    private int MoveDest(int unitType, int unitLoc, PlaceRef dest)
    {
        if (dest.Prov < 0) return -1;
        if (unitType == MapData.ARMY) return dest.Prov;
        int nCoast = map.provCoastCount[dest.Prov];
        if (dest.Coast != null)
        {
            if (nCoast == 0) return -1; // a coast was named where there is none
            return map.FindLoc(map.provId[dest.Prov] + "/" + dest.Coast);
        }
        if (nCoast == 0) return dest.Prov;
        int found = -1; int count = 0;
        for (int i = 0; i < nCoast; i++)
        {
            int l = map.provCoastStart[dest.Prov] + i;
            if (map.FleetAdjacent(unitLoc, l)) { found = l; count++; }
        }
        return count == 1 ? found : -1;
    }

    /// <summary>Enters a movement-phase order. Returns false when it names no unit of this power.</summary>
    public bool ApplyMovement(BoardState s, Adjudicator adj, int power, ParsedOrder o)
    {
        if (o.Unit.Prov < 0) return false;
        int u = s.provUnit[o.Unit.Prov];
        if (u < 0 || s.unitPower[u] != power) return false;
        switch (o.Kind)
        {
            case OrderKind.Hold: adj.SetHold(u); return true;
            case OrderKind.Move: adj.SetMove(u, MoveDest(s.unitType[u], s.unitLoc[u], o.Dest), o.Via); return true;
            case OrderKind.SupportHold: adj.SetSupportHold(u, o.Target.Prov); return true;
            case OrderKind.SupportMove:
                {
                    int d = o.Dest.Prov;
                    if (d >= 0 && o.Dest.Coast != null) d = map.FindLoc(map.provId[d] + "/" + o.Dest.Coast);
                    adj.SetSupportMove(u, o.Target.Prov, d);
                    return true;
                }
            case OrderKind.Convoy: adj.SetConvoy(u, o.Target.Prov, o.Dest.Prov); return true;
            default: adj.SetHold(u); return true;
        }
    }

    public bool ApplyRetreat(BoardState s, Adjudicator adj, int power, ParsedOrder o)
    {
        if (o.Unit.Prov < 0) return false;
        for (int d = 0; d < s.disCount; d++)
        {
            if (map.locProv[s.disLoc[d]] != o.Unit.Prov || s.disPower[d] != power) continue;
            if (o.Kind == OrderKind.Move) adj.SetRetreat(d, MoveDest(s.disType[d], s.disLoc[d], o.Dest));
            else if (o.Kind == OrderKind.Remove) adj.SetRetreat(d, -1);
            else return false;
            return true;
        }
        return false;
    }

    public bool ApplyAdjust(BoardState s, Adjudicator adj, int power, ParsedOrder o)
    {
        switch (o.Kind)
        {
            case OrderKind.Build:
                {
                    if (o.UnitType == '?') return false;
                    int loc;
                    if (o.UnitType == 'A') loc = o.Unit.Prov;
                    else loc = UnitLoc('F', o.Unit);
                    adj.AddAdjust(power, o.UnitType == 'A' ? Adjudicator.ADJ_BUILD_ARMY : Adjudicator.ADJ_BUILD_FLEET, loc);
                    return true;
                }
            case OrderKind.Remove: adj.AddAdjust(power, Adjudicator.ADJ_DISBAND, o.Unit.Prov); return true;
            case OrderKind.Waive: adj.AddAdjust(power, Adjudicator.ADJ_WAIVE, -1); return true;
            default: return false;
        }
    }

    // ---------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------

    public string Loc(int loc) => loc >= 0 && loc < map.locCount ? map.locId[loc] : "?";
    public string Prov(int prov) => prov >= 0 && prov < map.provCount ? map.provId[prov] : "?";
    public static string TypeLetter(int unitType) => unitType == MapData.FLEET ? "F" : "A";

    public string MovementOrder(int unitType, int unitLoc, int oType, int oA, int oB, bool via)
    {
        string u = TypeLetter(unitType) + " " + Loc(unitLoc);
        switch (oType)
        {
            case Adjudicator.MOVE: return u + " - " + Loc(oA) + (via ? " via convoy" : "");
            case Adjudicator.SUPPORT_HOLD: return u + " S " + Prov(oA);
            case Adjudicator.SUPPORT_MOVE: return u + " S " + Prov(oA) + " - " + Loc(oB);
            case Adjudicator.CONVOY: return u + " C " + Prov(oA) + " - " + Prov(oB);
            default: return u + " H";
        }
    }

    public static string ResultWord(int status, bool dislodged)
    {
        string s = status switch
        {
            Adjudicator.R_BOUNCED => "bounced",
            Adjudicator.R_CUT => "cut",
            Adjudicator.R_VOID => "void",
            Adjudicator.R_NO_PATH => "no convoy",
            _ => "ok"
        };
        return dislodged ? s + ", dislodged" : s;
    }
}
