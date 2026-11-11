// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Parley web client: a small server that runs the same rules engine as the VR world
// and a single web page with a flat map and tap-style order entry. Meant for playing a
// real game with friends over Discord before the VR world exists.
using System.Security.Cryptography;
using System.Text.Json;
using Parley.Engine;
using Parley.Tools;
using Parley.Web;

string root = Game.RepoRoot();
// the page lives next to the program when published, or in the source tree when run with "dotnet run"
string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(webRoot)) webRoot = Path.Combine(root, "web", "Parley.Web", "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRoot });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

string dataDir = Environment.GetEnvironmentVariable("PARLEY_DATA") ?? Path.Combine(root, "web", "data");
var lobby = new Lobby(new[] { Path.Combine(root, "maps"), Path.Combine(root, "maps-local") }, dataDir);

app.UseDefaultFiles();
app.UseStaticFiles();

IResult Fail(string message) => Results.Json(new { error = message }, statusCode: 400);

app.MapGet("/api/maps", () => lobby.Maps.Values.OrderBy(m => m.Map.powerCount).ThenBy(m => m.Map.mapName).Select(m => new
{
    id = m.Map.mapId, name = m.Map.mapName, powers = m.Map.powerCount, provinces = m.Map.provCount, centres = m.Map.scCount,
    credit = m.Map.credit
}));

app.MapGet("/api/games", () => lobby.Games.Values.OrderByDescending(g => g.Created).Take(50).Select(g =>
{
    lock (g) return new { id = g.Id, title = g.Title, map = g.G.Map.mapName, phase = g.G.Flow.stage == GameFlow.STAGE_LOBBY ? "lobby" : g.G.State.PhaseLabel(), seats = g.G.Flow.SeatedCount(), powers = g.G.Map.powerCount };
}));

app.MapPost("/api/games", (CreateRequest req) =>
{
    if (req.MapId == null || !lobby.Maps.TryGetValue(req.MapId, out var m)) return Fail("unknown map");
    var wg = lobby.Create(m, string.IsNullOrWhiteSpace(req.Title) ? m.Map.mapName : req.Title.Trim());
    Lobby.ApplySettings(wg.G.Flow, req.MoveSchedule, req.RetreatSeconds, req.AdjustSeconds, req.Announce, req.ReadyUp);
    wg.G.Flow.waitForAbsent = req.WaitForEveryone;
    lobby.Save(wg);
    return Results.Json(new { id = wg.Id });
});

app.MapPost("/api/games/import", (ImportRequest req) =>
{
    string text = (req.Text ?? "").Trim();
    if (text.Length == 0) return Fail("paste a game code or a game archive");
    bool isArchive = text.StartsWith(GameHistory.MAGIC + ";");
    var probe = lobby.Maps.Values.FirstOrDefault();
    if (probe == null) return Fail("no maps installed");
    string need = isArchive ? new GameHistory().PeekMap(text) : PeekCode(text);
    if (need == "") return Fail("that is neither a game code nor a game archive");
    string mapId = need.Split('.')[0];
    if (!lobby.Maps.TryGetValue(mapId, out var m)) return Fail("this save is for the map '" + mapId + "', which is not installed on this server");
    var wg = lobby.Create(m, m.Map.mapName + " (resumed)");
    bool ok = isArchive ? wg.G.Flow.LoadArchive(text, lobby.Now) : wg.G.Flow.LoadCode(text, lobby.Now);
    if (!ok) { string err = wg.G.Flow.error; lobby.Remove(wg); return Fail(err); }
    lobby.Save(wg);
    return Results.Json(new { id = wg.Id });
});

static string PeekCode(string code)
{
    var p = code.Split('.');
    return p.Length >= 4 && p[0] == GameCodec.MAGIC ? p[1] + "." + p[2] : "";
}

app.MapGet("/api/games/{id}/map", (string id) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    return Results.Json(wg.Info.Json);
});

app.MapPost("/api/games/{id}/join", (string id, JoinRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    string name = (req.Name ?? "").Trim();
    if (name.Length == 0 || name.Length > 24) return Fail("enter a name (up to 24 characters)");
    lock (wg)
    {
        // a seat is tied to a name; reusing the name of somebody who is in the game needs their link
        if (wg.Players.Values.Any(p => p.Name == name)) return Fail("that name is already in this game. If it is you, use the personal link you were given (or ask the host to free the seat).");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        wg.Players[token] = new Player { Name = name, LastSeen = lobby.Now, JoinOrder = wg.Players.Count };
        if (wg.HostToken == null) wg.HostToken = token;
        lobby.Save(wg);
        return Results.Json(new { token });
    }
});

app.MapGet("/api/games/{id}/state", (string id, string token) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        wg.Players.TryGetValue(token ?? "", out var me);
        if (me != null) { me.LastSeen = lobby.Now; }
        lobby.Tick(wg);
        return Results.Json(Views.State(wg, me, token, lobby.Now));
    }
});

app.MapPost("/api/games/{id}/seat", (string id, SeatRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (!wg.Players.TryGetValue(req.Token ?? "", out var me)) return Fail("join the game first");
        var f = wg.G.Flow;
        if (req.Power < 0) { int cur = f.SeatOf(me.Name); if (cur >= 0 && f.stage == GameFlow.STAGE_LOBBY) f.ReleaseSeat(cur); }
        else if (!f.ClaimSeat(req.Power, me.Name)) return Fail(f.error);
        lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

// Sealed orders: the browser keeps its order sheet to itself and sends only a seal
// (a SHA-256 fingerprint mixed with a secret salt). Flipping submit on = sending a seal.
app.MapPost("/api/games/{id}/seal", (string id, SealRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (!wg.Players.TryGetValue(req.Token ?? "", out var me)) return Fail("join the game first");
        var f = wg.G.Flow;
        int power = f.SeatOf(me.Name);
        if (power < 0) return Fail("you do not play a power in this game");
        if (req.SealKey != f.SealKey()) return Fail("the phase has changed; your screen was out of date");
        if (!f.SetSeal(power, req.Seal ?? "")) return Fail(f.error);
        lobby.Tick(wg); lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

// Submit toggle off: withdraws the seal so the sheet can be edited again.
app.MapPost("/api/games/{id}/unsubmit", (string id, TokenRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (!wg.Players.TryGetValue(req.Token ?? "", out var me)) return Fail("join the game first");
        var f = wg.G.Flow;
        int power = f.SeatOf(me.Name);
        if (power < 0) return Fail("you do not play a power in this game");
        if (!f.SetSubmitted(power, false)) return Fail(f.error);
        lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

// Once every seal is frozen, each browser opens its own sheet; the server checks it against the seal.
app.MapPost("/api/games/{id}/open", (string id, OpenRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (!wg.Players.TryGetValue(req.Token ?? "", out var me)) return Fail("join the game first");
        var f = wg.G.Flow;
        int power = f.SeatOf(me.Name);
        if (power < 0) return Fail("you do not play a power in this game");
        if (!f.Open(power, req.Salt ?? "", req.Orders ?? "")) return Fail(f.error);
        lobby.Tick(wg); lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

app.MapPost("/api/games/{id}/ready", (string id, DrawRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (!wg.Players.TryGetValue(req.Token ?? "", out var me)) return Fail("join the game first");
        var f = wg.G.Flow;
        int power = f.SeatOf(me.Name);
        if (power < 0) return Fail("you do not play a power in this game");
        if (!f.SetReady(power, req.Value)) return Fail(f.error);
        lobby.Tick(wg); lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

app.MapPost("/api/games/{id}/draw", (string id, DrawRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (!wg.Players.TryGetValue(req.Token ?? "", out var me)) return Fail("join the game first");
        int power = wg.G.Flow.SeatOf(me.Name);
        if (power < 0) return Fail("you do not play a power in this game");
        wg.G.Flow.VoteDraw(power, req.Value);
        lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

app.MapPost("/api/games/{id}/host", (string id, HostRequest req) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        if (req.Token == null || req.Token != wg.HostToken) return Fail("only the host can do that");
        var f = wg.G.Flow; float now = lobby.Now;
        switch (req.Action)
        {
            case "start": if (!f.StartGame(now)) return Fail(f.error); break;
            case "pause": f.Pause(now); break;
            case "resume": f.Resume(now); break;
            case "next": f.ForceNext(now); break;
            case "rollback": if (!f.RollBack(req.Index, now)) return Fail(f.error); break;
            case "timers": Lobby.ApplySettings(f, req.MoveSchedule, req.RetreatSeconds, req.AdjustSeconds, req.Announce, req.ReadyUp); break;
            case "wait": f.waitForAbsent = req.Value; break;
            case "free":
                if (req.Index >= 0 && req.Index < wg.G.Map.powerCount)
                {
                    string holder = wg.G.History.seatName[req.Index];
                    f.ReleaseSeat(req.Index);
                    // their personal link stops working, so the seat can really be handed on
                    foreach (var k in wg.Players.Where(p => p.Value.Name == holder && p.Key != wg.HostToken).Select(p => p.Key).ToList()) wg.Players.Remove(k);
                }
                break;
            default: return Fail("unknown action");
        }
        lobby.Save(wg);
        return Results.Json(new { ok = true });
    }
});

app.MapGet("/api/games/{id}/history/{index:int}", (string id, int index) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg)
    {
        var v = Views.HistoryEntry(wg, index);
        return v == null ? Results.NotFound() : Results.Json(v);
    }
});

app.MapGet("/api/games/{id}/archive", (string id) =>
{
    if (!lobby.Games.TryGetValue(id, out var wg)) return Results.NotFound();
    lock (wg) return Results.Text(wg.G.History.count == 0 ? "" : wg.G.Flow.Archive());
});

// the clock: resolve phases whose time has run out even if nobody is looking
var timer = new System.Threading.Timer(_ =>
{
    foreach (var wg in lobby.Games.Values) { try { lock (wg) lobby.Tick(wg); } catch { /* keep the clock alive */ } }
}, null, 1000, 1000);

string url = Environment.GetEnvironmentVariable("PARLEY_URL") ?? "http://0.0.0.0:5080";
Console.WriteLine($"Parley web client: {lobby.Maps.Count} maps, {lobby.Games.Count} saved games. Listening on {url}");
app.Run(url);

public record CreateRequest(string MapId, string Title, int[] MoveSchedule, int RetreatSeconds, int AdjustSeconds, bool WaitForEveryone, string Announce, bool ReadyUp);
public record ImportRequest(string Text);
public record JoinRequest(string Name);
public record SeatRequest(string Token, int Power);
public record SealRequest(string Token, string SealKey, string Seal);
public record OpenRequest(string Token, string Salt, string Orders);
public record TokenRequest(string Token);
public record DrawRequest(string Token, bool Value);
public record HostRequest(string Token, string Action, int Index, int[] MoveSchedule, int RetreatSeconds, int AdjustSeconds, bool Value, string Announce, bool ReadyUp);
