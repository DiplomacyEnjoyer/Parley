// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

public class MapTests
{
    public static IEnumerable<object[]> ShippedMaps()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Game.RepoRoot(), "maps"), "*.pmap"))
            yield return new object[] { Path.GetFileName(f) };
    }

    private static MapData LoadShipped(string file)
    {
        var map = new MapData();
        Assert.True(map.Load(File.ReadAllText(Path.Combine(Game.RepoRoot(), "maps", file))), map.error);
        return map;
    }

    [Theory]
    [MemberData(nameof(ShippedMaps))]
    public void ShippedMapLoadsAndIsConsistent(string file)
    {
        var map = LoadShipped(file);
        Assert.True(map.powerCount >= 2);
        Assert.True(map.victoryCenters > 0 && map.victoryCenters <= map.scCount);
        // adjacency is two-way
        for (int p = 0; p < map.provCount; p++)
            for (int i = 0; i < map.armyCount[p]; i++) Assert.True(map.ArmyAdjacent(map.armyAdj[map.armyStart[p] + i], p));
        for (int l = 0; l < map.locCount; l++)
            for (int i = 0; i < map.fleetCount[l]; i++) Assert.True(map.FleetAdjacent(map.fleetAdj[map.fleetStart[l] + i], l));
        // every power has a home centre and a unit, and every unit stands somewhere legal
        for (int pw = 0; pw < map.powerCount; pw++)
        {
            Assert.Contains(Enumerable.Range(0, map.provCount), p => map.provHome[p] == pw);
            Assert.Contains(Enumerable.Range(0, map.startUnitCount), u => map.startUnitPower[u] == pw);
        }
        for (int u = 0; u < map.startUnitCount; u++) Assert.True(map.CanStand(map.startUnitType[u], map.startUnitLoc[u]));
        // every province can be reached by something
        for (int p = 0; p < map.provCount; p++) Assert.True(map.anyCount[p] > 0, map.provId[p] + " is isolated");
        // every province has a layout position
        for (int p = 0; p < map.provCount; p++) Assert.True(map.provX[p] >= 0 && map.provShape[p] != "", map.provId[p] + " has no layout");
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(7)] [InlineData(17)] [InlineData(34)]
    public void RingMapsWorkForAnyPlayerCount(int players)
    {
        var map = new MapData();
        Assert.True(map.Load(RingMapGenerator.Generate(players)), map.error);
        Assert.Equal(players, map.powerCount);
        Assert.Equal(players * 10 + 1, map.provCount);
        Assert.Equal(players * 6 + 1, map.scCount);
        // every realm is identical: same number of neighbours for the same role
        int p0 = map.FindProv("alc");
        for (int pw = 1; pw < players; pw++)
        {
            int hold = -1;
            for (int p = 0; p < map.provCount; p++) if (map.provHome[p] == pw && map.provType[p] == MapData.LAND) hold = p;
            Assert.Equal(map.armyCount[p0], map.armyCount[hold]);
        }
    }

    [Fact]
    public void GeneratedMapsMatchTheCommittedFiles()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Game.RepoRoot(), "maps"), "ring*.pmap"))
        {
            var name = Path.GetFileNameWithoutExtension(f).Substring(4);
            bool isthmus = name.EndsWith("i");
            int n = int.Parse(isthmus ? name.Substring(0, name.Length - 1) : name);
            Assert.Equal(RingMapGenerator.Generate(n, isthmus), File.ReadAllText(f));
        }
    }

    [Fact]
    public void IsthmusVariantHasTwoCoasts()
    {
        var map = LoadShipped("ring5i.pmap");
        int k = map.FindProv("alk");
        Assert.Equal(2, map.provCoastCount[k]);
        Assert.False(map.CanStand(MapData.FLEET, k));
        Assert.True(map.CanStand(MapData.FLEET, map.FindLoc("alk/ic")));
        Assert.True(map.CanStand(MapData.ARMY, k));
        // the two coasts do not connect to each other's sea
        Assert.True(map.FleetAdjacent(map.FindLoc("alk/ic"), map.FindProv("als")));
        Assert.False(map.FleetAdjacent(map.FindLoc("alk/oc"), map.FindProv("als")));
    }

    [Fact]
    public void BadMapsAreRejectedWithAMessage()
    {
        var map = new MapData();
        Assert.False(map.Load("prov a l - A"));
        Assert.Contains("header", map.error);
        Assert.False(map.Load("parleymap 1\npower x X #fff X\nprov a l - A\nprov a l - B"));
        Assert.Contains("duplicate", map.error);
        Assert.False(map.Load("parleymap 1\npower x X #fff X\nprov a l - A\nprov b s - B\narmy a: b"));
        Assert.Contains("sea", map.error);
    }

    [Fact]
    public void ReferenceMapsImport()
    {
        // every DPjudge-format map in the reference package converts and loads
        var dir = Path.GetDirectoryName(DatcRunner.ReferenceMapPath())!;
        int count = 0;
        foreach (var f in Directory.GetFiles(dir, "*.map"))
        {
            var res = DpjudgeImporter.Import(f);
            var map = new MapData();
            Assert.True(map.Load(res.Pmap), Path.GetFileName(f) + ": " + map.error);
            Assert.True(map.powerCount >= 2, Path.GetFileName(f));
            Assert.True(map.startUnitCount >= map.powerCount, Path.GetFileName(f));
            count++;
        }
        Assert.True(count >= 10);
    }
}
