// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>Fog of war: the rule for what a power sees (docs/fog-of-war.md).</summary>
public class VisibilityTests
{
    private static (Game g, Visibility v) Ring()
    {
        var g = Game.FromPmap(RingMapGenerator.Generate(5));
        var v = new Visibility(); v.Init(g.Map);
        return (g, v);
    }

    [Fact]
    public void APowerSeesItsUnitsItsCentresAndTheirNeighbours()
    {
        var (g, v) = Ring(); var m = g.Map; var s = g.State;
        v.Look(s, 0);
        for (int u = 0; u < s.unitCount; u++)
        {
            int p = m.locProv[s.unitLoc[u]];
            if (s.unitPower[u] == 0)
            {
                Assert.True(v.SeesUnit(s, u));
                for (int i = 0; i < m.anyCount[p]; i++) Assert.True(v.SeesProvince(m.anyAdj[m.anyStart[p] + i]));
            }
        }
        // On a five-player ring, most of the board is out of sight at the start, including every unit of the power opposite.
        Assert.InRange(v.Count(), 5, m.provCount / 2);
        int hidden = 0;
        for (int u = 0; u < s.unitCount; u++) if (s.unitPower[u] == 2 && !v.SeesUnit(s, u)) hidden++;
        Assert.Equal(s.CountUnits(2), hidden);
        // Everything a power sees is explained by one of its units or centres.
        for (int p = 0; p < m.provCount; p++)
        {
            if (!v.SeesProvince(p)) continue;
            bool why = s.owner[p] == 0 || (s.provUnit[p] >= 0 && s.unitPower[s.provUnit[p]] == 0);
            for (int i = 0; i < m.anyCount[p] && !why; i++)
            {
                int q = m.anyAdj[m.anyStart[p] + i];
                why = s.owner[q] == 0 || (s.provUnit[q] >= 0 && s.unitPower[s.provUnit[q]] == 0);
            }
            Assert.True(why, m.provId[p] + " is seen for no reason");
        }
    }

    [Fact]
    public void CentresCanBeToldToSeeOnlyThemselves()
    {
        var (g, v) = Ring(); var s = g.State;
        v.Look(s, 0); int wide = v.Count();
        while (s.unitCount > 0) s.RemoveUnit(g.Map, 0);
        v.Look(s, 0); int centresAndNeighbours = v.Count();
        v.centresSeeNeighbours = false;
        v.Look(s, 0);
        Assert.Equal(s.CountCenters(0), v.Count());
        Assert.True(centresAndNeighbours > v.Count() && wide >= centresAndNeighbours);
    }

    [Fact]
    public void SightMovesWithTheUnitsAndOrdersAreWitnessedOnlyInSight()
    {
        var (g, v) = Ring(); var m = g.Map; var s = g.State; var f = g.Flow;
        for (int p = 0; p < m.powerCount; p++) f.ClaimSeat(p, "P" + p);
        f.StartGame(0);
        g.Scratch.CopyFrom(s);
        // every unit of power 2 moves somewhere; power 0 holds
        var pad = new Adjudicator(); pad.Init(m);
        for (int u = 0; u < s.unitCount; u++)
            if (s.unitPower[u] == 2 && g.Options.ListMoves(s, u) > 0) pad.SetMove(u, g.Options.optA[0], false);
        f.Submit(2, g.Codec.EncodeOrders(s, pad, 2));
        f.ForceNext(0);
        var a = g.Announce;
        v.Look(g.Scratch, 0); v.AlsoLook(s, 0);          // before or after the turn
        int own = 0, others = 0, witnessed = 0;
        for (int i = 0; i < a.count; i++)
        {
            if (a.power[i] == 0) { own++; Assert.True(v.SeesLine(a, i, 0)); }
            else { others++; if (v.SeesLine(a, i, 0)) witnessed++; }
            if (a.power[i] == 2) Assert.False(v.SeesLine(a, i, 0)); // the far side of the ring moved unseen
        }
        Assert.True(own > 0 && others > 0);
        Assert.True(witnessed < others);
        // power 2 saw all of its own moves
        v.Look(g.Scratch, 2); v.AlsoLook(s, 2);
        for (int i = 0; i < a.count; i++) if (a.power[i] == 2) Assert.True(v.SeesLine(a, i, 2));
    }

    [Fact]
    public void AWalledCityIsSeenWithItsProvince()
    {
        var b = new PurseBoard();
        var v = new Visibility(); v.Init(b.m);
        v.Look(b.s, b.P("blue"));
        Assert.True(v.SeesProvince(b.Prov("mid")));      // next to Bluefields
        Assert.True(v.SeesUnit(b.s, b.U("mid_c")));      // so the garrison inside is seen too
        Assert.False(v.SeesProvince(b.Prov("rcap"))); Assert.False(v.SeesProvince(b.Prov("rcap_c")));
        // a garrison sees what a unit outside its walls would see
        b.ClearUnits().Put("blue", 'G', "mid_c");
        b.s.owner[b.Prov("bcap")] = -1; b.s.owner[b.Prov("bcap_c")] = -1; b.s.owner[b.Prov("bfield")] = -1; b.s.owner[b.Prov("bport")] = -1; b.s.owner[b.Prov("bport_c")] = -1;
        v.Look(b.s, b.P("blue"));
        Assert.True(v.SeesProvince(b.Prov("pass"))); Assert.True(v.SeesProvince(b.Prov("gfield")));
        Assert.False(v.SeesProvince(b.Prov("gcap")));
    }
}
