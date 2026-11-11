// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>Board pictures fetched by scripts/fetch-reference-maps.sh, matched to imported maps.</summary>
public class ArtTests
{
    [Theory]
    [InlineData("standard")]
    [InlineData("modern")]
    [InlineData("ancmed")]
    public void BoardPictureCoversEveryProvinceAndIsSafeToShow(string name)
    {
        string root = Game.RepoRoot();
        string mapFile = Path.Combine(root, "third_party", "maps", "dpjudge", name + ".map");
        string svgFile = Path.Combine(root, "third_party", "svg", "diplomacy", name + ".svg");
        Assert.True(File.Exists(svgFile), "run scripts/fetch-reference-maps.sh first");
        var res = DpjudgeImporter.Import(mapFile);
        var map = new MapData();
        Assert.True(map.Load(res.Pmap), map.error);
        var warnings = new List<string>();
        var art = SvgArt.Convert(svgFile, map, res.Aliases, warnings);
        Assert.DoesNotContain(warnings, w => w.StartsWith("no "));   // every province, coast and centre has its picture
        Assert.Equal(map.provCount, art.Provs.Count);
        Assert.Equal(map.locCount, art.Units.Count);
        Assert.Equal(map.scCount, art.Centres.Count);
        // the markup ends up inside the web page: only plain shapes and text may survive
        foreach (var markup in art.Provs.Values.Append(art.Background).Append(art.Labels))
            foreach (var bad in new[] { "<script", "href", "<image", "<use", "<style", "<foreignObject", " on", "url(", "javascript" })
                Assert.DoesNotContain(bad, markup, StringComparison.OrdinalIgnoreCase);
        // every unit spot lies on the picture
        foreach (var xy in art.Units.Values) { Assert.InRange(xy[0], 0, art.Width); Assert.InRange(xy[1], 0, art.Height); }
    }
}

/// <summary>The jDip importer, checked against the DPjudge importer on the one map both formats describe.</summary>
public class JdipTests
{
    private static string Pack(string name) => Path.Combine(Game.RepoRoot(), "third_party", "jdip", name);

    [Fact]
    public void JdipStandardIsTheSameBoardAsDpjudgeStandard()
    {
        Assert.True(File.Exists(Pack("stdVariants.zip")), "run scripts/fetch-reference-maps.sh first");
        var j = JdipImporter.Import(Pack("stdVariants.zip"), "Standard", "standard");
        var d = DpjudgeImporter.Import(Path.Combine(Game.RepoRoot(), "third_party", "maps", "dpjudge", "standard.map"));
        var a = new MapData(); Assert.True(a.Load(j.Pmap), a.error);
        var b = new MapData(); Assert.True(b.Load(d.Pmap), b.error);
        Assert.Equal(b.provCount, a.provCount); Assert.Equal(b.locCount, a.locCount);
        Assert.Equal(b.scCount, a.scCount); Assert.Equal(b.startUnitCount, a.startUnitCount);
        for (int p = 0; p < a.provCount; p++)
        {
            int q = b.FindProv(a.provId[p]);
            Assert.True(q >= 0, a.provId[p]);
            Assert.Equal(b.provType[q], a.provType[p]);
            Assert.Equal(b.provSc[q], a.provSc[p]);
            for (int r = 0; r < a.provCount; r++)
            {
                int s = b.FindProv(a.provId[r]);
                Assert.True(b.ArmyAdjacent(q, s) == a.ArmyAdjacent(p, r), "army " + a.provId[p] + "-" + a.provId[r]);
            }
        }
        for (int l = 0; l < a.locCount; l++)
            for (int m = 0; m < a.locCount; m++)
                Assert.True(b.FleetAdjacent(b.FindLoc(a.locId[l]), b.FindLoc(a.locId[m])) == a.FleetAdjacent(l, m), "fleet " + a.locId[l] + "-" + a.locId[m]);
    }

    [Theory]
    [InlineData("hundredVariants.zip", "Hundred3 v2 [H32]")]
    [InlineData("youngstownVariants.zip", "Youngstown")]
    [InlineData("sailhoVariants.zip", "Sail Ho!")]
    [InlineData("stdVariants.zip", "1898")]
    [InlineData("stdVariants.zip", "Fleet Rome")]
    [InlineData("milanVariants.zip", "Milan")]
    [InlineData("asiaVariants.zip", "Asia")]
    [InlineData("loeb9Variants.zip", "Loeb9")]
    [InlineData("rootzVariants.zip", "Root Z")]
    public void JdipVariantConvertsWithItsPictureAndPlays(string pack, string variant)
    {
        Assert.True(File.Exists(Pack(pack)), "run scripts/fetch-reference-maps.sh first");
        var res = JdipImporter.Import(Pack(pack), variant, "v");
        var map = new MapData(); Assert.True(map.Load(res.Pmap), map.error);
        string svg = Path.GetTempFileName();
        try
        {
            File.WriteAllText(svg, res.Svg);
            var warnings = new List<string>();
            var art = SvgArt.Convert(svg, map, res.Aliases, warnings);
            Assert.Equal(map.provCount, art.Provs.Count);
        }
        finally { File.Delete(svg); }
        var report = Simulator.Run(res.Pmap, 3, 5, 8, "basic");   // checks every phase; throws on any broken invariant
        Assert.True(report.Problems.Count == 0, string.Join("\n", report.Problems));
        Assert.Equal(3, report.Games);
    }
}
