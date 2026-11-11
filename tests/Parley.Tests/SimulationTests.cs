// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// Bot-driven games through the whole stack. After every phase the simulator checks
/// that the position is possible, that it survives save/load, and that replaying the
/// recorded orders reproduces it exactly.
/// </summary>
public class SimulationTests
{
    [Theory]
    [InlineData("ring2", "basic", 12)]
    [InlineData("ring3", "random", 12)]
    [InlineData("ring5i", "basic", 10)]
    [InlineData("ring5i", "random", 10)]
    [InlineData("ring7", "basic", 6)]
    [InlineData("ring17", "basic", 2)]
    [InlineData("ring17", "random", 2)]
    public void ShippedMaps(string map, string bot, int games)
    {
        var rep = Simulator.Run(File.ReadAllText(Path.Combine(Game.RepoRoot(), "maps", map + ".pmap")), games, 42, 25, bot);
        Assert.True(rep.Problems.Count == 0, string.Join("\n", rep.Problems));
        Assert.Equal(games, rep.Games);
    }

    [Fact]
    public void LargeGeneratedMap()
    {
        // 34 players, 341 provinces: the engine has no built-in size or player limit
        var rep = Simulator.Run(RingMapGenerator.Generate(34), 1, 7, 6, "basic");
        Assert.True(rep.Problems.Count == 0, string.Join("\n", rep.Problems));
    }

    public static IEnumerable<object[]> ReferenceMaps()
    {
        var dir = Path.GetDirectoryName(DatcRunner.ReferenceMapPath())!;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, "*.map").OrderBy(x => x))
            yield return new object[] { Path.GetFileNameWithoutExtension(f) };
    }

    /// <summary>Every imported community map plays without problems (2 to 17 powers in this set).</summary>
    [Theory]
    [MemberData(nameof(ReferenceMaps))]
    public void ImportedMaps(string name)
    {
        var dir = Path.GetDirectoryName(DatcRunner.ReferenceMapPath())!;
        var pmap = DpjudgeImporter.Import(Path.Combine(dir, name + ".map")).Pmap;
        foreach (var bot in new[] { "basic", "random" })
        {
            var rep = Simulator.Run(pmap, 3, 99, 15, bot);
            Assert.True(rep.Problems.Count == 0, name + " (" + bot + "): " + string.Join("\n", rep.Problems));
        }
    }
}
