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
    public OptionGenerator Options;
    public GameCodec Codec;
    public GameHistory History;
    public GameFlow Flow;
    public OrderSeal Seal;
    public Announcement Announce;
    public BoardState Scratch;

    public static Game FromPmap(string pmapText, Dictionary<string, List<string>> aliases = null)
    {
        var map = new MapData();
        if (!map.Load(pmapText)) throw new InvalidDataException("map error: " + map.error);
        return FromMap(map, aliases);
    }

    public static Game FromFile(string pmapPath) => FromPmap(File.ReadAllText(pmapPath));

    public static Game FromMap(MapData map, Dictionary<string, List<string>> aliases = null)
    {
        var g = new Game { Map = map, State = new BoardState(), Adj = new Adjudicator() };
        g.State.Init(map);
        g.State.SetupStart(map);
        g.Adj.Init(map);
        g.Notation = new Notation(map, aliases);
        g.Options = new OptionGenerator(); g.Options.Init(map, g.Adj);
        g.Codec = new GameCodec(); g.Codec.Init(map);
        g.History = new GameHistory(); g.History.Init(map, g.Codec);
        g.Scratch = new BoardState(); g.Scratch.Init(map);
        g.Seal = new OrderSeal(); g.Seal.Init();
        g.Announce = new Announcement(); g.Announce.Init(map);
        g.Flow = new GameFlow(); g.Flow.Init(map, g.State, g.Adj, g.Codec, g.History, g.Scratch, g.Seal, g.Announce);
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
