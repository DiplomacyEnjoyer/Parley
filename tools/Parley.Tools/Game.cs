// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;

namespace Parley.Tools;

/// <summary>Convenience bundle for tools and tests: a map, a board and a referee wired together.</summary>
public sealed class Game
{
    public MapData Map;
    public BoardState State;
    public Adjudicator Adj;
    public Notation Notation;

    public static Game FromPmap(string pmapText, Dictionary<string, List<string>> aliases = null)
    {
        var map = new MapData();
        if (!map.Load(pmapText)) throw new InvalidDataException("map error: " + map.error);
        return FromMap(map, aliases);
    }

    public static Game FromMap(MapData map, Dictionary<string, List<string>> aliases = null)
    {
        var g = new Game { Map = map, State = new BoardState(), Adj = new Adjudicator() };
        g.State.Init(map);
        g.State.SetupStart(map);
        g.Adj.Init(map);
        g.Notation = new Notation(map, aliases);
        return g;
    }

    /// <summary>Walks up from the current directory to find the repository root.</summary>
    public static string RepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "PLAN.md")) && Directory.Exists(Path.Combine(d.FullName, "engine"))) return d.FullName;
                d = d.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
