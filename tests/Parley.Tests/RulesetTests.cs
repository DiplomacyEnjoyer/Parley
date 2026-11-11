// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// The parts a ruleset is built from: a calendar read from the map, unit strength,
/// units that live in cities, conversions, dice. Standard play must not notice any of it.
/// </summary>
public class RulesetTests
{
    private static string Ring3 => File.ReadAllText(Path.Combine(Game.RepoRoot(), "maps", "ring3.pmap"));

    [Fact]
    public void StandardBoardsKeepTheirFingerprintsAndCodes()
    {
        // These were the values before rulesets existed; a saved game from then must still load.
        var g = Game.FromPmap(Ring3);
        Assert.Equal("oqahgf", g.Map.hash);
        Assert.Equal("standard", g.Map.rulesId);
        Assert.Equal(2, g.Map.seasonCount);
        Assert.Equal(2, g.Map.unitTypeCount);
        Assert.False(g.Map.extended);
        Assert.Equal(9, g.Codec.EncodeState(g.State).Split('.').Length);
        Assert.StartsWith("P1.ring3.oqahgf.S1M.", g.Codec.EncodeState(g.State));
        Assert.Equal("Standard", g.Rules.Title());
        Assert.False(g.Rules.UsesDice(g.State));
    }

    [Fact]
    public void ABoardCanAskForARulesetThisVersionLacks()
    {
        var map = new MapData();
        Assert.True(map.Load(Ring3 + "\nrules moonbase low-gravity\n"), map.error);
        Assert.Equal("moonbase", map.rulesId);
        var ex = Assert.Throws<InvalidDataException>(() => Game.FromMap(map));
        Assert.Contains("moonbase", ex.Message);
    }

    [Fact]
    public void TheCalendarComesFromTheMap()
    {
        // Three movement seasons; centres change hands after summer and after fall.
        string pmap = Ring3 + "\nseasons Spring Summer* Fall* Winter\n";
        var g = Game.FromPmap(pmap);
        Assert.Equal(3, g.Map.seasonCount);
        Assert.Equal("SUFW", g.Map.seasonLetters);
        Assert.NotEqual("oqahgf", g.Map.hash); // a different calendar is a different game
        var f = g.Flow;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        f.SetMoveSchedule(new[] { 100, 200, 300, 400 }, 4);
        Assert.True(f.StartGame(0));
        var labels = new List<string>();
        var clocks = new List<float>();
        for (int i = 0; i < 4; i++)
        {
            labels.Add(g.State.PhaseLabel()); clocks.Add(f.SecondsLeft(0));
            f.ForceNext(0); // everybody holds
        }
        Assert.Equal(new[] { "S1M", "U1M", "F1M", "S2M" }, labels);
        Assert.Equal(new float[] { 100, 200, 300, 400 }, clocks);
        // codes carry the new season letter and load again
        g.State.season = 1;
        string code = g.Codec.EncodeState(g.State);
        Assert.Contains(".U2M.", code);
        Assert.True(g.Codec.DecodeState(code, g.Scratch), g.Codec.error);
        Assert.Equal(1, g.Scratch.season);
    }

    [Fact]
    public void AThreeSeasonGamePlaysThroughWithBots()
    {
        foreach (var bot in new[] { "basic", "random" })
        {
            var rep = Simulator.Run(Ring3 + "\nseasons Spring Summer Fall* Winter\n", 6, 5, 12, bot);
            Assert.True(rep.Problems.Count == 0, string.Join("\n", rep.Problems));
        }
    }

    // A small board with a walled port city, for the unit-type tests.
    private const string Fort = @"parleymap 1
id fort
rules standard
victory 9
power red Red #c00 Red
power blue Blue #00c Blue
prov keep c red Keep
prov moor l - Moor
prov fen l - Fen
prov hill l blue Hill
prov bay s - Bay
army keep: moor fen
army moor: fen hill
army fen: hill
fleet bay: keep
city keep walled 2 port
unit red G keep
unit red A fen
unit blue A moor
unit blue A hill
";

    // The calendar is switched off (as a ruleset with its own bookkeeping would do), so
    // that every Resolve in these tests is a movement or retreat phase.
    private static Game FortGame() { var g = Game.FromPmap(Fort); g.Adj.autoAdvance = false; return g; }

    private static int UnitAt(Game g, string loc) => g.State.provUnit[g.Map.locProv[g.Map.FindLoc(loc)]];

    [Fact]
    public void AWalledCityIsASpaceOfItsOwnForGarrisons()
    {
        var g = FortGame(); var m = g.Map;
        Assert.Equal(5, m.landCount); Assert.Equal(6, m.provCount); Assert.Equal(3, m.unitTypeCount);
        int keep = m.FindProv("keep"), city = m.provCity[keep];
        Assert.Equal(MapData.CITY, m.provType[city]);
        Assert.Equal(keep, m.cityProv[city]);
        Assert.True(m.CanStand(MapData.GARRISON, city));
        Assert.False(m.CanStand(MapData.ARMY, city));
        Assert.False(m.CanStand(MapData.GARRISON, keep));
        Assert.Equal(MapData.GARRISON, g.State.unitType[UnitAt(g, "keep_c")]);
        // the position, garrison included, survives a game code
        string code = g.Codec.EncodeState(g.State);
        Assert.True(g.Codec.DecodeState(code, g.Scratch), g.Codec.error);
        Assert.Equal(code, g.Codec.EncodeState(g.Scratch));
    }

    [Fact]
    public void AGarrisonSupportsIntoItsProvinceAndCannotBePushedOut()
    {
        var g = FortGame(); var m = g.Map; var a = g.Adj; var s = g.State;
        int keep = m.FindProv("keep");
        // Blue walks into the empty province around the city; the garrison alone cannot stop it,
        // but with the garrison's support Red's army gets there first.
        a.ClearOrders();
        a.SetMove(UnitAt(g, "moor"), keep, false);
        a.SetMove(UnitAt(g, "fen"), keep, false);
        a.SetSupportMove(UnitAt(g, "keep_c"), m.FindProv("fen"), keep);
        a.Resolve(s);
        Assert.Equal(0, s.unitPower[UnitAt(g, "keep")]);
        Assert.True(UnitAt(g, "moor") >= 0);
        // A garrison never moves, and a unit outside cannot enter an occupied city.
        a.ClearOrders();
        a.SetMove(UnitAt(g, "keep_c"), m.FindProv("fen"), false);
        a.SetConvert(UnitAt(g, "keep"), m.provCity[keep], MapData.GARRISON);
        a.Resolve(s);
        Assert.Equal(MapData.GARRISON, s.unitType[UnitAt(g, "keep_c")]);
        Assert.Equal(MapData.ARMY, s.unitType[UnitAt(g, "keep")]);
        Assert.Equal(Adjudicator.R_VOID, a.rStatus[0] == Adjudicator.R_VOID ? a.rStatus[0] : a.rStatus[1]);
    }

    [Fact]
    public void ConversionsAreMovesBetweenCityAndProvince()
    {
        var g = FortGame(); var m = g.Map; var a = g.Adj; var s = g.State;
        int keep = m.FindProv("keep"), city = m.provCity[keep];
        // The garrison comes out as a fleet while a Blue army walks in unsupported: both bounce.
        a.ClearOrders();
        a.SetConvert(UnitAt(g, "keep_c"), keep, MapData.FLEET);
        a.SetMove(UnitAt(g, "moor"), keep, false);
        a.Resolve(s);
        Assert.True(UnitAt(g, "keep") < 0);
        Assert.Equal(MapData.GARRISON, s.unitType[UnitAt(g, "keep_c")]);
        // Left alone, it comes out; the city is empty afterwards.
        a.ClearOrders();
        a.SetConvert(UnitAt(g, "keep_c"), keep, MapData.FLEET);
        a.Resolve(s);
        Assert.Equal(MapData.FLEET, s.unitType[UnitAt(g, "keep")]);
        Assert.True(s.provUnit[city] < 0);
        // And back in again.
        a.ClearOrders();
        a.SetConvert(UnitAt(g, "keep"), city, MapData.GARRISON);
        a.Resolve(s);
        Assert.Equal(MapData.GARRISON, s.unitType[s.provUnit[city]]);
        Assert.True(UnitAt(g, "keep") < 0);
        // An army in a province without a city has nothing to convert into.
        a.ClearOrders();
        int fen = UnitAt(g, "fen");
        a.SetConvert(fen, m.FindProv("fen"), MapData.GARRISON);
        a.Resolve(s);
        Assert.Equal(Adjudicator.R_VOID, a.rStatus[fen]);
    }

    [Fact]
    public void ADislodgedUnitMayRetreatIntoItsCityOnlyAsALastResort()
    {
        var g = FortGame(); var m = g.Map; var a = g.Adj; var s = g.State;
        int keep = m.FindProv("keep"), city = m.provCity[keep];
        // Put the red army in Keep (garrison removed), blue armies on Moor and Fen, blue fleet in the Bay.
        s.Clear(); s.year = 1;
        s.AddUnit(m, MapData.ARMY, 0, keep);
        s.AddUnit(m, MapData.ARMY, 1, m.FindProv("moor"));
        s.AddUnit(m, MapData.ARMY, 1, m.FindProv("fen"));
        a.ClearOrders();
        a.SetMove(UnitAt(g, "moor"), keep, false);
        a.SetSupportMove(UnitAt(g, "fen"), m.FindProv("moor"), keep);
        a.Resolve(s);
        Assert.Equal(BoardState.PHASE_RETREAT, s.phase);
        // Moor (where the attacker came from) and Fen (occupied) are closed: the city is the only way out.
        Assert.True(a.RetreatValid(s, 0, city));
        string code = g.Codec.EncodeState(s);
        Assert.True(g.Codec.DecodeState(code, g.Scratch), g.Codec.error);
        a.ClearOrders(); a.SetRetreat(0, city); a.Resolve(s);
        Assert.Equal(MapData.GARRISON, s.unitType[s.provUnit[city]]);
        Assert.Equal(0, s.unitPower[s.provUnit[city]]);

        // Same attack, but Fen is free this time: the open country must be used instead.
        s.Clear(); s.year = 1;
        s.AddUnit(m, MapData.ARMY, 0, keep);
        s.AddUnit(m, MapData.ARMY, 1, m.FindProv("moor"));
        s.AddUnit(m, MapData.ARMY, 1, m.FindProv("hill"));
        a.ClearOrders();
        a.SetMove(UnitAt(g, "moor"), keep, false);
        s.unitFlag[UnitAt(g, "moor")] = 0; a.uStr[UnitAt(g, "moor")] = 2; // an elite unit needs no support
        a.Resolve(s);
        Assert.Equal(BoardState.PHASE_RETREAT, s.phase);
        Assert.False(a.RetreatValid(s, 0, city));
        Assert.True(a.RetreatValid(s, 0, m.FindProv("fen")));
    }

    [Fact]
    public void StrengthAndBonusCountInEveryComparison()
    {
        var g = FortGame(); var m = g.Map; var a = g.Adj; var s = g.State;
        int fen = m.FindProv("fen"), moor = m.FindProv("moor"), hill = m.FindProv("hill");
        // Blue Moor -> Fen with support from Hill (2) against Red's army in Fen made strong (2): bounce.
        a.ClearOrders();
        a.SetMove(UnitAt(g, "moor"), fen, false);
        a.SetSupportMove(UnitAt(g, "hill"), moor, fen);
        a.uStr[UnitAt(g, "fen")] = 2;
        a.Resolve(s);
        Assert.Equal(0, s.unitPower[UnitAt(g, "fen")]);
        Assert.Equal(BoardState.PHASE_MOVE, s.phase);
        // A bonus of one on the mover tips it.
        a.ClearOrders();
        a.SetMove(UnitAt(g, "moor"), fen, false);
        a.SetSupportMove(UnitAt(g, "hill"), moor, fen);
        a.uStr[UnitAt(g, "fen")] = 2; a.uBonus[UnitAt(g, "moor")] = 1;
        a.Resolve(s);
        Assert.Equal(BoardState.PHASE_RETREAT, s.phase);
        // A strong unit's support is worth two, and is still cut by anybody.
        s.Clear(); s.year = 1;
        s.AddUnit(m, MapData.ARMY, 0, fen); s.AddUnit(m, MapData.ARMY, 1, moor); s.AddUnit(m, MapData.ARMY, 1, hill);
        s.AddUnit(m, MapData.ARMY, 0, m.FindProv("keep"));
        a.ClearOrders();
        a.SetMove(UnitAt(g, "keep"), moor, false);
        a.SetSupportMove(UnitAt(g, "fen"), m.FindProv("keep"), moor);
        a.SetSupportHold(UnitAt(g, "hill"), moor);
        a.uStr[UnitAt(g, "fen")] = 2;
        a.Resolve(s); // 1 + 2 against 1 + 1 (the loser has nowhere to go and is destroyed)
        Assert.Equal(0, s.unitPower[UnitAt(g, "moor")]);
        Assert.Equal(3, s.unitCount);
    }

    [Fact]
    public void CoastalFleetsConvoyOnlyWhereTheMapSaysSo()
    {
        const string coast = @"parleymap 1
id strand
power red Red #c00 Red
power blue Blue #00c Blue
prov a c red Aport
prov b c - Bmouth
prov c c blue Cove
army a: b
army b: c
fleet a: b
fleet b: c
unit red A a
unit red F b
unit blue A c
";
        foreach (bool allowed in new[] { false, true })
        {
            var g = Game.FromPmap(coast + (allowed ? "convoy coastal\n" : ""));
            var m = g.Map; var a = g.Adj; var s = g.State;
            s.RemoveUnit(m, UnitAt(g, "c"));
            a.ClearOrders();
            a.SetMove(UnitAt(g, "a"), m.FindProv("c"), true);
            a.SetConvoy(UnitAt(g, "b"), m.FindProv("a"), m.FindProv("c"));
            a.Resolve(s);
            Assert.Equal(allowed, UnitAt(g, "c") >= 0);
        }
    }

    [Fact]
    public void DiceAreAFixedFunctionOfSeedAndLabelAndComeOutEven()
    {
        var g = Game.FromPmap(Ring3); var d = g.Dice;
        d.Begin(g.Seal.Sha256Hex("a seed"));
        int first = d.Roll(6, "famine.row");
        int other = d.Roll(6, "famine.col");
        d.Begin(g.Seal.Sha256Hex("a seed"));
        Assert.Equal(other, d.Roll(6, "famine.col"));   // asking in another order changes nothing
        Assert.Equal(first, d.Roll(6, "famine.row"));
        Assert.Equal(2, d.rollCount);
        Assert.Equal("famine.col", d.rollLabel[0]); Assert.Equal(6, d.rollSides[0]); Assert.Equal(other, d.rollValue[0]);
        // an independent calculation of the same roll
        string h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("PD1|" + d.seed + "|famine.row|0"))).ToLowerInvariant();
        int v = Convert.ToInt32(h.Substring(0, 7), 16);
        Assert.True(v < 268435452, "this seed happens to need a second draw; pick another for the test");
        Assert.Equal(v % 6 + 1, first);
        // 6000 rolls: every face near 1000 (a fair die misses 850..1150 less than once in a million tries)
        var counts = new int[7];
        for (int i = 0; i < 6000; i++) counts[d.Roll(6, "t" + i)]++;
        for (int face = 1; face <= 6; face++) Assert.InRange(counts[face], 850, 1150);
        Assert.Equal(1, d.Roll(1, "one-sided"));
        for (int i = 0; i < 200; i++) Assert.InRange(d.Roll(11, "wide" + i), 1, 11);
    }
}
