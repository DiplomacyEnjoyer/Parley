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
    public int JoinOrder;
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
    public Dictionary<int, (int serial, object value)> OptionCache = new();
}

/// <summary>All maps and games on this server, the shared clock, and saving games to disk.</summary>
public sealed class Lobby
{
    public readonly Dictionary<string, MapInfo> Maps = new();
    public readonly ConcurrentDictionary<string, WebGame> Games = new();
    private readonly string dataDir;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    /// <summary>A player whose browser has not checked in for this long counts as away (PARLEY_AWAY_SECONDS).</summary>
    public static readonly float AwayAfterSeconds = float.TryParse(Environment.GetEnvironmentVariable("PARLEY_AWAY_SECONDS"), out var a) && a > 0 ? a : 45;

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
        WebDefaults(wg.G.Flow);
        Games[id] = wg;
        return wg;
    }

    /// <summary>
    /// The web client always plays with sealed orders: the server never holds anybody's
    /// orders before every seal is frozen. Browsers poll every couple of seconds, so the
    /// closing and opening windows are a little longer than in VR.
    /// </summary>
    public static void WebDefaults(GameFlow f)
    {
        f.UseLiveSettings();
        f.closeGraceSeconds = 10; f.revealSeconds = 20;
        f.waitForAbsent = false;
    }

    /// <summary>Timer schedule (seconds), announcement speed ("normal", "fast", "off") and ready-up.</summary>
    public static void ApplySettings(GameFlow f, int[] schedule, int retreat, int adjust, string announce, bool readyUp)
    {
        if (schedule != null && schedule.Length > 0) f.SetMoveSchedule(schedule, schedule.Length);
        f.history.retreatSeconds = Math.Max(0, retreat);
        f.history.adjustSeconds = Math.Max(0, adjust);
        f.readyMode = readyUp ? GameFlow.READY_MOVEMENT : GameFlow.READY_NEVER;
        switch (announce)
        {
            case "off": f.announceLeadSeconds = 0; f.announcePowerSeconds = 0; f.announceOrderSeconds = 0; f.announceResultSeconds = 0; break;
            case "fast": f.announceLeadSeconds = 1; f.announcePowerSeconds = 0.5f; f.announceOrderSeconds = 0.5f; f.announceResultSeconds = 2; break;
            default: f.announceLeadSeconds = 3; f.announcePowerSeconds = 1.5f; f.announceOrderSeconds = 3; f.announceResultSeconds = 6; break;
        }
    }

    public static string AnnounceName(GameFlow f) =>
        f.announceOrderSeconds <= 0 ? "off" : f.announceOrderSeconds < 1 ? "fast" : "normal";

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
        // the host role moves on if the host's browser has gone away, so a dropped host never stalls the game
        if (wg.HostToken == null || !wg.Players.TryGetValue(wg.HostToken, out var host) || now - host.LastSeen >= AwayAfterSeconds)
        {
            var next = wg.Players.Where(p => now - p.Value.LastSeen < AwayAfterSeconds)
                .OrderBy(p => f.SeatOf(p.Value.Name) < 0 ? 1 : 0).ThenBy(p => p.Value.JoinOrder).FirstOrDefault();
            if (next.Key != null && next.Key != wg.HostToken) { wg.HostToken = next.Key; Save(wg); }
        }
        bool changed = false;
        while (f.Tick(now)) changed = true;
        if (changed) Save(wg);
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
        public int[] Schedule { get; set; }
        public int[] Timers { get; set; }
        public string Announce { get; set; }
        public bool ReadyUp { get; set; }
        public string[] Seals { get; set; }
        public string[] JoinOrder { get; set; }
        public string[] Seats { get; set; }
        public string Archive { get; set; }
        public bool Running { get; set; }

    }

    public void Save(WebGame wg)
    {
        var f = wg.G.Flow; var h = wg.G.History;
        var s = new Saved
        {
            Id = wg.Id, Title = wg.Title, MapId = wg.G.Map.mapId, Created = wg.Created, Host = wg.HostToken,
            Players = wg.Players.ToDictionary(p => p.Key, p => p.Value.Name),
            WaitForAbsent = f.waitForAbsent,
            Schedule = h.moveSchedule.Take(h.moveScheduleCount).ToArray(),
            Timers = new[] { 0, h.retreatSeconds, h.adjustSeconds },
            Announce = AnnounceName(f), ReadyUp = f.readyMode != GameFlow.READY_NEVER,
            Seats = h.seatName.ToArray(),
            Archive = h.count > 0 ? f.Archive() : "",
            Running = f.stage != GameFlow.STAGE_PAUSED && f.stage != GameFlow.STAGE_LOBBY && f.stage != GameFlow.STAGE_FINISHED,
            Seals = f.sealText.ToArray(),
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
        int order = 0;
        foreach (var (token, name) in s.Players) wg.Players[token] = new Player { Name = name, LastSeen = -1000, JoinOrder = order++ };
        var f = wg.G.Flow; var h = wg.G.History;
        WebDefaults(f);
        ApplySettings(f, s.Schedule, s.Timers?[1] ?? 0, s.Timers?[2] ?? 0, s.Announce, s.ReadyUp);
        if (!string.IsNullOrEmpty(s.Archive))
        {
            if (!f.LoadArchive(s.Archive, Now)) { Console.WriteLine($"saved game {file}: {f.error}"); return; }
            // seals that were published before the server stopped still count; the browsers kept the sheets
            for (int p = 0; p < wg.G.Map.powerCount && s.Seals != null && p < s.Seals.Length; p++)
                if (!string.IsNullOrEmpty(s.Seals[p])) f.SetSeal(p, s.Seals[p]);
            if (s.Running) f.Resume(Now);
        }
        else
        {
            for (int p = 0; p < h.seatName.Length && p < s.Seats.Length; p++) h.seatName[p] = s.Seats[p];
        }
        f.waitForAbsent = s.WaitForAbsent;
        Games[wg.Id] = wg;
    }
}
