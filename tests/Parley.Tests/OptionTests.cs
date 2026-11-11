// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

public class OptionTests
{
    private static HashSet<string> Locs(Game g, int n, bool onlyKind = false, int kind = 0)
    {
        var set = new HashSet<string>();
        for (int i = 0; i < n; i++)
            if (!onlyKind || g.Options.optB[i] == kind) set.Add(g.Options.optA[i] < 0 ? "hold" : g.Map.locId[g.Options.optA[i]]);
        return set;
    }

    private static int Unit(Game g, string loc) => g.State.provUnit[g.Map.locProv[g.Map.FindLoc(loc)]];

    [Fact]
    public void OpeningMovesOnTheRingMap()
    {
        var g = Game.FromPmap(RingMapGenerator.Generate(3));
        var s = g.State; var opt = g.Options;
        // fleet in Alder Haven: its bay and the two neighbouring coasts
        Assert.Equal(new HashSet<string> { "als", "all", "alr" }, Locs(g, opt.ListMoves(s, Unit(g, "alp"))));
        // army in Alder Hold (inland): six neighbours in its own realm plus the two border Narrows
        Assert.Equal(new HashSet<string> { "all", "alp", "alr", "alm", "alg", "aln", "alk", "brk" }, Locs(g, opt.ListMoves(s, Unit(g, "alc"))));
        // nobody is at sea yet, so no convoys anywhere
        for (int u = 0; u < s.unitCount; u++) Assert.Equal(0, opt.ListConvoyArmies(s, u));
        // the Hold army can support the Haven fleet to stay, or to go to Marsh or Wood (not into the bay)
        Assert.Equal(new HashSet<string> { "hold", "all", "alr" }, Locs(g, opt.ListSupportTargets(s, Unit(g, "alc"), g.Map.FindProv("alp"))));
        // the fleet cannot support the inland army to stay? It can: Hold is not reachable by a fleet -> no.
        Assert.DoesNotContain("hold", Locs(g, opt.ListSupportTargets(s, Unit(g, "alp"), g.Map.FindProv("alc"))));
        Assert.Equal(new HashSet<string> { "all", "alr" }, Locs(g, opt.ListSupportTargets(s, Unit(g, "alp"), g.Map.FindProv("alc"))));
    }

    [Fact]
    public void ConvoyOptionsAppearWhenAFleetIsAtSea()
    {
        var g = Game.FromPmap(RingMapGenerator.Generate(3));
        var s = g.State; var map = g.Map; var opt = g.Options;
        s.Clear(); s.year = 1;
        s.AddUnit(map, MapData.FLEET, 0, map.FindLoc("als"));   // Alder fleet in Alder Bay
        s.AddUnit(map, MapData.ARMY, 0, map.FindLoc("alp"));    // Alder army in Alder Haven
        s.AddUnit(map, MapData.ARMY, 1, map.FindLoc("brc"));
        int army = Unit(g, "alp"), fleet = Unit(g, "als");
        var byConvoy = Locs(g, opt.ListMoves(s, army), true, 1);
        // across the bay: the island in the middle and every coast the bay touches
        Assert.Contains("crn", byConvoy);
        Assert.Contains("alk", byConvoy);
        Assert.Contains("brk", byConvoy);
        Assert.DoesNotContain("alp", byConvoy);
        Assert.DoesNotContain("brp", byConvoy);                 // Brann Haven is on Brann Bay, not Alder Bay
        Assert.Equal(new HashSet<string> { "alp" }, Locs(g, opt.ListConvoyArmies(s, fleet)));
        Assert.Contains("crn", Locs(g, opt.ListConvoyDestinations(s, fleet, map.FindProv("alp"))));
        // "all" (Alder Marsh) is next door by land AND reachable by convoy: offered both ways
        Assert.Contains("all", Locs(g, opt.ListMoves(s, army), true, 0));
        Assert.Contains("all", byConvoy);

        // and the convoy really works
        g.Adj.ClearOrders();
        g.Adj.SetMove(army, map.FindLoc("crn"), true);
        g.Adj.SetConvoy(fleet, map.FindProv("alp"), map.FindProv("crn"));
        g.Adj.ResolveMovement(s);
        Assert.True(g.Adj.rMoved[army]);
    }

    [Fact]
    public void SplitCoastsAreOfferedSeparately()
    {
        var g = Game.FromPmap(RingMapGenerator.Generate(3, isthmus: true));
        var s = g.State; var map = g.Map; var opt = g.Options;
        s.Clear(); s.year = 1; s.season = BoardState.WINTER; s.phase = BoardState.PHASE_ADJUST;
        // Alder Marsh is next to Alder Narrows' inner coast only
        s.AddUnit(map, MapData.FLEET, 0, map.FindLoc("all"));
        s.phase = BoardState.PHASE_MOVE;
        var moves = Locs(g, opt.ListMoves(s, Unit(g, "all")));
        Assert.Contains("alk/ic", moves);
        Assert.DoesNotContain("alk/oc", moves);
        Assert.DoesNotContain("alk", moves);
        // an army simply goes to the province
        s.Clear(); s.year = 1;
        s.AddUnit(map, MapData.ARMY, 0, map.FindLoc("all"));
        Assert.Contains("alk", Locs(g, opt.ListMoves(s, Unit(g, "all"))));
    }

    [Fact]
    public void BuildAndDisbandOptions()
    {
        var g = Game.FromPmap(RingMapGenerator.Generate(3, isthmus: true));
        var s = g.State; var map = g.Map; var opt = g.Options;
        s.Clear(); s.year = 3; s.season = BoardState.WINTER; s.phase = BoardState.PHASE_ADJUST;
        int alder = 0;
        foreach (var c in new[] { "alp", "alc", "alg", "alk" }) s.owner[map.FindProv(c)] = alder;
        s.AddUnit(map, MapData.ARMY, alder, map.FindLoc("alc"));
        // owns 4 centres, has 1 unit; free home centres: Haven and Gate (Hold is occupied; Narrows is not a home centre)
        Assert.Equal(2, g.Adj.AdjustDelta(s, alder));
        int n = opt.ListBuilds(s, alder);
        var builds = Enumerable.Range(0, n).Select(i => (opt.optB[i] == MapData.FLEET ? "F " : "A ") + map.locId[opt.optA[i]]).ToHashSet();
        Assert.Equal(new HashSet<string> { "A alp", "F alp", "A alg", "F alg" }, builds);
        Assert.Equal(0, opt.ListDisbands(s, alder));

        // too many units: every unit is a disband candidate, nothing to build
        s.owner[map.FindProv("alp")] = -1; s.owner[map.FindProv("alg")] = -1; s.owner[map.FindProv("alk")] = -1;
        s.AddUnit(map, MapData.ARMY, alder, map.FindLoc("alm"));
        Assert.Equal(-1, g.Adj.AdjustDelta(s, alder));
        Assert.Equal(0, opt.ListBuilds(s, alder));
        Assert.Equal(new HashSet<string> { "alc", "alm" }, Locs(g, opt.ListDisbands(s, alder)));
    }

    public static IEnumerable<object[]> PropertyMaps()
    {
        yield return new object[] { "ring4", 11 };
        yield return new object[] { "ring5i", 12 };
        yield return new object[] { "ref:standard", 13 };
        yield return new object[] { "ref:ancmed", 14 };
    }

    /// <summary>
    /// The options offered to players must be exactly the orders the referee accepts:
    /// nothing offered is void, and nothing acceptable is missing. Checked on many
    /// mid-game positions reached by bot play.
    /// </summary>
    [Theory]
    [MemberData(nameof(PropertyMaps))]
    public void OfferedOptionsAreExactlyWhatTheRefereeAccepts(string mapName, int seed)
    {
        string pmap = mapName.StartsWith("ref:")
            ? DpjudgeImporter.Import(Path.Combine(Path.GetDirectoryName(DatcRunner.ReferenceMapPath())!, mapName.Substring(4) + ".map")).Pmap
            : File.ReadAllText(Path.Combine(Game.RepoRoot(), "maps", mapName + ".pmap"));
        var g = Game.FromPmap(pmap);
        var map = g.Map; var s = g.State; var opt = g.Options; var f = g.Flow;
        for (int p = 0; p < map.powerCount; p++) f.ClaimSeat(p, "bot" + p);
        f.StartGame(0);
        var rng = new Random(seed);
        var bot = new Bot(g, rng, true);
        var probe = new BoardState(); probe.Init(map);
        var adj = new Adjudicator(); adj.Init(map);
        int checkedPositions = 0, checkedOrders = 0, movePhases = 0;
        for (int step = 0; step < 60 && f.stage == GameFlow.STAGE_ORDERS; step++)
        {
            if (s.phase == BoardState.PHASE_MOVE) movePhases++;
            if (s.phase == BoardState.PHASE_MOVE && movePhases % 3 == 1)
            {
                checkedPositions++;
                for (int u = 0; u < s.unitCount; u++)
                {
                    // --- moves ---
                    int n = opt.ListMoves(s, u);
                    var plain = new HashSet<int>(); var conv = new HashSet<int>();
                    for (int i = 0; i < n; i++) (opt.optB[i] == 0 ? plain : conv).Add(opt.optA[i]);
                    for (int l = 0; l < map.locCount; l++)
                    {
                        if (s.unitType[u] == MapData.ARMY && l >= map.provCount) continue;
                        probe.CopyFrom(s); adj.ClearOrders(); adj.SetMove(u, l, false);
                        adj.ResolveMovement(probe);
                        bool accepted = adj.rStatus[u] != Adjudicator.R_VOID;
                        Assert.True(accepted == (plain.Contains(l) || conv.Contains(l)),
                            $"{map.locId[s.unitLoc[u]]} -> {map.locId[l]}: referee {(accepted ? "accepts" : "rejects")}, options {(accepted ? "omit" : "offer")} it");
                        checkedOrders++;
                    }
                    // --- supports: offered ones are accepted when the other unit plays along ---
                    int ns = opt.ListSupportable(s, u);
                    var supportable = Enumerable.Range(0, ns).Select(i => opt.optA[i]).ToList();
                    foreach (int tp in supportable)
                    {
                        int m = opt.ListSupportTargets(s, u, tp);
                        var targets = Enumerable.Range(0, m).Select(i => opt.optA[i]).ToList();
                        Assert.NotEmpty(targets);
                        int t = s.provUnit[tp];
                        foreach (int d in targets)
                        {
                            probe.CopyFrom(s); adj.ClearOrders();
                            if (d < 0) adj.SetSupportHold(u, tp);
                            else
                            {
                                adj.SetSupportMove(u, tp, d);
                                // the supported unit makes that move (to any coast of d it can reach)
                                int dest = d;
                                if (s.unitType[t] == MapData.FLEET && map.provCoastCount[d] > 0)
                                    for (int c = 0; c < map.provCoastCount[d]; c++)
                                        if (map.FleetAdjacent(s.unitLoc[t], map.provCoastStart[d] + c)) dest = map.provCoastStart[d] + c;
                                adj.SetMove(t, dest, s.unitType[t] == MapData.ARMY && !map.ArmyAdjacent(tp, d));
                            }
                            adj.ResolveMovement(probe);
                            Assert.True(adj.rStatus[u] != Adjudicator.R_VOID,
                                $"offered support is void: {map.locId[s.unitLoc[u]]} S {map.provId[tp]} {(d < 0 ? "hold" : "- " + map.provId[d])}");
                            checkedOrders++;
                        }
                    }
                    // --- convoys ---
                    int nc = opt.ListConvoyArmies(s, u);
                    var armies = Enumerable.Range(0, nc).Select(i => opt.optA[i]).ToList();
                    foreach (int ap in armies)
                    {
                        int m = opt.ListConvoyDestinations(s, u, ap);
                        var dests = Enumerable.Range(0, m).Select(i => opt.optA[i]).ToList();
                        Assert.NotEmpty(dests);
                        foreach (int d in dests)
                        {
                            probe.CopyFrom(s); adj.ClearOrders();
                            adj.SetConvoy(u, ap, d); adj.SetMove(s.provUnit[ap], d, true);
                            adj.ResolveMovement(probe);
                            Assert.True(adj.rStatus[u] != Adjudicator.R_VOID, $"offered convoy is void: {map.locId[s.unitLoc[u]]} C {map.provId[ap]} - {map.provId[d]}");
                            checkedOrders++;
                        }
                    }
                }
            }
            else if (s.phase == BoardState.PHASE_RETREAT)
            {
                for (int d = 0; d < s.disCount; d++)
                {
                    int n = opt.ListRetreats(s, d);
                    var offered = Enumerable.Range(0, n).Select(i => opt.optA[i]).ToHashSet();
                    for (int l = 0; l < map.locCount; l++) Assert.Equal(g.Adj.RetreatValid(s, d, l), offered.Contains(l));
                }
            }
            else if (s.phase == BoardState.PHASE_ADJUST)
            {
                for (int p = 0; p < map.powerCount; p++)
                {
                    int n = opt.ListBuilds(s, p);
                    for (int i = 0; i < n; i++) Assert.True(g.Adj.BuildValid(s, p, opt.optB[i], opt.optA[i]));
                    if (g.Adj.AdjustDelta(s, p) > 0) Assert.True(n > 0);
                }
            }
            for (int p = 0; p < map.powerCount; p++) if (f.NeedsOrders(p)) { f.Submit(p, bot.Choose(p)); f.SetSubmitted(p, true); }
            Assert.True(f.Tick(step + 1));
        }
        Assert.True(checkedPositions >= 3 && checkedOrders > 500, $"only {checkedPositions} positions / {checkedOrders} orders checked");
    }
}
