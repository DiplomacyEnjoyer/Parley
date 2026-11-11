// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Parley.Engine;
using Parley.Tools;

namespace Parley.Web;

public sealed class Player
{
    public string Name = "";
    public float LastSeen;
}

public sealed class MapInfo
{
    public MapData Map;
    public string Pmap = "";
    public object Json;
}

public sealed class WebGame
{
    public string Id = "", Title = "";
    public DateTime Created = DateTime.UtcNow;
    public Game G;
    public MapInfo Info;
    public string HostToken;
    public Dictionary<string, Player> Players = new();
    public Dictionary<int, (int serial, string submitted, object value)> OptionCache = new();
}

/// <summary>All maps and games on this server, the shared clock, and saving games to disk.</summary>
public sealed class Lobby
{
    public readonly Dictionary<string, MapInfo> Maps = new();
    public readonly ConcurrentDictionary<string, WebGame> Games = new();
    private readonly string dataDir;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private const float AwayAfterSeconds = 45;

    public float Now => (float)clock.Elapsed.TotalSeconds;

    public Lobby(IEnumerable<string> mapDirs, string dataDirectory)
    {
        dataDir = dataDirectory;
        Directory.CreateDirectory(dataDir);
        foreach (var dir in mapDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.pmap").OrderBy(f => f))
            {
                var text = File.ReadAllText(file);
                var map = new MapData();
                if (!map.Load(text)) { Console.WriteLine($"skipping {file}: {map.error}"); continue; }
                Maps[map.mapId] = new MapInfo { Map = map, Pmap = text, Json = Views.MapJson(map) };
            }
        }
        foreach (var file in Directory.GetFiles(dataDir, "*.json"))
        {
            try { Load(file); } catch (Exception ex) { Console.WriteLine($"could not load saved game {file}: {ex.Message}"); }
        }
    }

    public WebGame Create(MapInfo m, string title)
    {
        string id;
        do { id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(); } while (Games.ContainsKey(id));
        var wg = new WebGame { Id = id, Title = title, Info = m, G = Game.FromPmap(m.Pmap) };
        wg.G.Flow.waitForAbsent = true;
        Games[id] = wg;
        return wg;
    }

    public void Remove(WebGame wg)
    {
        Games.TryRemove(wg.Id, out _);
        var path = Path.Combine(dataDir, wg.Id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Updates who is here, and resolves the phase if it is time. Call with the game locked.</summary>
    public void Tick(WebGame wg)
    {
        var f = wg.G.Flow; var h = wg.G.History;
        float now = Now;
        for (int p = 0; p < wg.G.Map.powerCount; p++)
        {
            string name = h.seatName[p];
            if (name == "") { f.seatPresent[p] = false; continue; }
            f.seatPresent[p] = wg.Players.Values.Any(pl => pl.Name == name && now - pl.LastSeen < AwayAfterSeconds);
        }
        if (f.Tick(now)) Save(wg);
    }

    private sealed class Saved
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string MapId { get; set; }
        public DateTime Created { get; set; }
        public string Host { get; set; }
        public Dictionary<string, string> Players { get; set; }
        public bool WaitForAbsent { get; set; }
        public int[] Timers { get; set; }
        public string[] Seats { get; set; }
        public string Archive { get; set; }
        public bool Running { get; set; }
        public string[] Submitted { get; set; }
        public bool[] Locked { get; set; }
    }

    public void Save(WebGame wg)
    {
        var f = wg.G.Flow; var h = wg.G.History;
        var s = new Saved
        {
            Id = wg.Id, Title = wg.Title, MapId = wg.G.Map.mapId, Created = wg.Created, Host = wg.HostToken,
            Players = wg.Players.ToDictionary(p => p.Key, p => p.Value.Name),
            WaitForAbsent = f.waitForAbsent,
            Timers = new[] { h.moveSeconds, h.retreatSeconds, h.adjustSeconds },
            Seats = h.seatName.ToArray(),
            Archive = h.count > 0 ? f.Archive() : "",
            Running = f.stage == GameFlow.STAGE_ORDERS,
            Submitted = f.submitted.ToArray(), Locked = f.locked.ToArray(),
        };
        string path = Path.Combine(dataDir, wg.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(s));
        File.Move(path + ".tmp", path, true);
    }

    private void Load(string file)
    {
        var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file));
        if (s == null || !Maps.TryGetValue(s.MapId, out var m)) { Console.WriteLine($"saved game {file}: map '{s?.MapId}' is not installed"); return; }
        var wg = new WebGame { Id = s.Id, Title = s.Title, Created = s.Created, Info = m, G = Game.FromPmap(m.Pmap), HostToken = s.Host };
        foreach (var (token, name) in s.Players) wg.Players[token] = new Player { Name = name, LastSeen = -1000 };
        var f = wg.G.Flow; var h = wg.G.History;
        if (!string.IsNullOrEmpty(s.Archive))
        {
            if (!f.LoadArchive(s.Archive, Now)) { Console.WriteLine($"saved game {file}: {f.error}"); return; }
            // orders that were handed in before the server stopped are still there
            for (int p = 0; p < wg.G.Map.powerCount && s.Submitted != null && p < s.Submitted.Length; p++)
            {
                f.Submit(p, s.Submitted[p]);
                if (s.Locked != null && p < s.Locked.Length) f.SetLocked(p, s.Locked[p]);
            }
            if (s.Running) f.Resume(Now);
        }
        else
        {
            for (int p = 0; p < h.seatName.Length && p < s.Seats.Length; p++) h.seatName[p] = s.Seats[p];
            f.SetTimers(s.Timers[0], s.Timers[1], s.Timers[2]);
        }
        f.waitForAbsent = s.WaitForAbsent;
        Games[wg.Id] = wg;
    }
}
