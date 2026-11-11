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
