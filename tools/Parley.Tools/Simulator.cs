// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Diagnostics;
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Plays whole games with bots through the real game flow (seats, order submission,
/// lock-in, resolution, history) and checks after every phase that nothing impossible
/// has happened. Also gathers per-power results for map-balance comparisons.
/// </summary>
public sealed class Simulator
{
    public sealed class PowerStats
    {
        public string Name = "";
        public int Wins, Survived, Eliminated;
        public long CentersAtEnd;
        public int Games;
    }

    public sealed class Report
    {
        public string MapName = "";
        public int Games, Solos, Unfinished;
        public long Phases, MovePhases;
        public double TotalYears;
        public double ResolveMicrosAvg;
        public long MaxArchiveChars, MaxCodeChars;
        public List<PowerStats> Powers = new();
        public List<string> Problems = new();
    }

    public sealed class InvariantException : Exception { public InvariantException(string m) : base(m) { } }

    public static Report Run(string pmapText, int games, int seed, int maxYears, string botKind, bool verify = true, Action<string> log = null)
    {
        var report = new Report();
        var first = Game.FromPmap(pmapText);
        report.MapName = first.Map.mapName;
        for (int p = 0; p < first.Map.powerCount; p++) report.Powers.Add(new PowerStats { Name = first.Map.powerName[p] });
        var sw = new Stopwatch();
        long resolves = 0;
        for (int gi = 0; gi < games; gi++)
        {
            var g = Game.FromPmap(pmapText);
            var rng = new Random(seed * 7919 + gi);
            var bot = new Bot(g, rng, botKind != "random");
            var flow = g.Flow;
            for (int p = 0; p < g.Map.powerCount; p++) flow.ClaimSeat(p, "bot" + p);
            float now = 0;
            flow.StartGame(now);
            string trail = "";
            try
            {
                int endYear = g.Map.startYear + maxYears;
                while (flow.stage == GameFlow.STAGE_ORDERS && g.State.year < endYear)
                {
                    string before = g.Codec.EncodeState(g.State);
                    trail = before;
                    for (int p = 0; p < g.Map.powerCount; p++)
                    {
                        if (!flow.NeedsOrders(p)) continue;
                        string orders = bot.Choose(p);
                        if (!flow.Submit(p, orders)) throw new InvariantException("orders rejected: " + flow.error + " [" + orders + "]");
                        flow.SetLocked(p, true);
                    }
                    bool wasMove = g.State.phase == BoardState.PHASE_MOVE;
                    now += 1;
                    sw.Start();
                    bool resolved = flow.Tick(now);
                    sw.Stop();
                    if (!resolved) throw new InvariantException("phase did not resolve although every bot locked in");
                    resolves++; report.Phases++; if (wasMove) report.MovePhases++;
                    if (verify) Verify(g, before);
                }
                if (verify)
                {
                    // the whole game must survive a save/load of the archive
                    string archive = flow.Archive();
                    report.MaxArchiveChars = Math.Max(report.MaxArchiveChars, archive.Length);
                    var g2 = Game.FromPmap(pmapText);
                    if (!g2.Flow.LoadArchive(archive, 0)) throw new InvariantException("archive did not load: " + g2.Flow.error);
                    if (g2.Flow.Archive() != archive) throw new InvariantException("archive changed after save/load");
                    if (g2.Codec.EncodeState(g2.State) != g.Codec.EncodeState(g.State)) throw new InvariantException("position changed after archive save/load");
                }
                report.MaxCodeChars = Math.Max(report.MaxCodeChars, g.Codec.EncodeState(g.State).Length);
            }
            catch (Exception ex)
            {
                report.Problems.Add($"game {gi} (seed {seed}) at {trail}: {ex.GetType().Name}: {ex.Message}");
                log?.Invoke(report.Problems[^1]);
                if (report.Problems.Count >= 10) break;
                continue;
            }
            report.Games++;
            report.TotalYears += g.State.year - g.Map.startYear;
            if (g.State.phase == BoardState.PHASE_FINISHED && g.State.winner >= 0) report.Solos++; else report.Unfinished++;
            for (int p = 0; p < g.Map.powerCount; p++)
            {
                var ps = report.Powers[p];
                ps.Games++;
                int c = g.State.CountCenters(p);
                ps.CentersAtEnd += c;
                if (g.State.winner == p) ps.Wins++;
                if (c == 0 && g.State.CountUnits(p) == 0) ps.Eliminated++; else ps.Survived++;
            }
        }
        report.ResolveMicrosAvg = resolves == 0 ? 0 : sw.Elapsed.TotalMilliseconds * 1000.0 / resolves;
        return report;
    }

    /// <summary>Checks that the position just produced is possible and that saving and loading it loses nothing.</summary>
    private static void Verify(Game g, string before)
    {
        var s = g.State; var map = g.Map;
        var seenProv = new HashSet<int>();
        for (int u = 0; u < s.unitCount; u++)
        {
            if (!map.CanStand(s.unitType[u], s.unitLoc[u])) throw new InvariantException("unit in an impossible place: " + map.locId[s.unitLoc[u]]);
            int p = map.locProv[s.unitLoc[u]];
            if (!seenProv.Add(p)) throw new InvariantException("two units in " + map.provId[p]);
            if (s.provUnit[p] != u) throw new InvariantException("unit index out of step at " + map.provId[p]);
            if (s.unitPower[u] < 0 || s.unitPower[u] >= map.powerCount) throw new InvariantException("unit without owner");
        }
        for (int p = 0; p < map.provCount; p++)
        {
            if (s.owner[p] >= 0 && !map.provSc[p]) throw new InvariantException("owner on a non-centre " + map.provId[p]);
            if (s.provUnit[p] >= 0 && !seenProv.Contains(p)) throw new InvariantException("ghost unit at " + map.provId[p]);
        }
        if (s.phase == BoardState.PHASE_MOVE && s.season == BoardState.SPRING)
        {
            // straight after winter: nobody may have more units than centres
            for (int pw = 0; pw < map.powerCount; pw++)
                if (s.CountUnits(pw) > s.CountCenters(pw) && s.year > map.startYear)
                    throw new InvariantException(map.powerName[pw] + " has more units than centres after adjustments");
        }
        if (s.phase == BoardState.PHASE_RETREAT)
        {
            if (s.disCount == 0) throw new InvariantException("retreat phase with nothing to retreat");
            for (int d = 0; d < s.disCount; d++)
                if (s.provUnit[map.locProv[s.disLoc[d]]] < 0) throw new InvariantException("dislodged unit but nobody took its place");
        }
        // save -> load -> save must give the same text
        string code = g.Codec.EncodeState(s);
        if (!g.Codec.DecodeState(code, g.Scratch)) throw new InvariantException("code did not load: " + g.Codec.error + " " + code);
        string again = g.Codec.EncodeState(g.Scratch);
        if (again != code) throw new InvariantException("code changed after save/load: " + code + " vs " + again);
        // replaying the recorded orders from the saved position must give the same result
        var h = g.History;
        string prevBody = h.body[h.count - 2];
        string orders = h.orders[h.count - 2];
        if (!g.Codec.DecodeBody(prevBody, g.Scratch)) throw new InvariantException("history entry did not load");
        if (g.Codec.EncodeState(g.Scratch) != before) throw new InvariantException("history entry differs from the position that was played");
        var adj2 = new Adjudicator(); adj2.Init(map);
        if (!g.Codec.DecodeOrders(orders, g.Scratch, adj2, -1)) throw new InvariantException("recorded orders did not load: " + orders);
        adj2.Resolve(g.Scratch);
        if (g.Codec.EncodeState(g.Scratch) != code) throw new InvariantException("replaying recorded orders gave a different result");
    }

    public static string Format(Report r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{r.MapName}: {r.Games} games, {r.Solos} won outright, {r.Unfinished} unfinished at the year limit; average length {(r.Games == 0 ? 0 : r.TotalYears / r.Games):0.0} years");
        sb.AppendLine($"  {r.Phases} phases resolved, {r.ResolveMicrosAvg:0} microseconds each on this machine; longest game code {r.MaxCodeChars} chars, largest archive {r.MaxArchiveChars} chars");
        sb.AppendLine("  power               wins   win%   avg centres   eliminated%");
        foreach (var p in r.Powers)
        {
            double n = Math.Max(1, p.Games);
            sb.AppendLine($"  {p.Name,-18} {p.Wins,5} {100.0 * p.Wins / n,6:0.0} {p.CentersAtEnd / n,13:0.00} {100.0 * p.Eliminated / n,12:0.0}");
        }
        foreach (var pr in r.Problems) sb.AppendLine("  PROBLEM: " + pr);
        return sb.ToString();
    }
}
