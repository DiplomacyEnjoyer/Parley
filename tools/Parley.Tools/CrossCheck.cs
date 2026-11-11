// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Text.Json;
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Writes random movement-phase situations (position, orders, and this engine's verdict)
/// as JSON lines, so a second, independent adjudicator can be asked the same questions.
/// See scripts/crosscheck.py, which feeds them to the Python "diplomacy" package.
/// </summary>
public static class CrossCheck
{
    public static void Export(string pmapText, int count, int seed, TextWriter output)
    {
        int written = 0; int gameNo = 0;
        while (written < count)
        {
            var g = Game.FromPmap(pmapText);
            var map = g.Map; var s = g.State; var f = g.Flow;
            var rng = new Random(seed * 1000003 + gameNo++);
            var basic = new Bot(g, rng, true);
            var wild = new Bot(g, rng, false);
            for (int p = 0; p < map.powerCount; p++) f.ClaimSeat(p, "bot" + p);
            f.StartGame(0);
            var probe = new BoardState(); probe.Init(map);
            var adj = new Adjudicator(); adj.Init(map);
            for (int step = 0; step < 80 && f.stage == GameFlow.STAGE_ORDERS && written < count; step++)
            {
                if (s.phase == BoardState.PHASE_MOVE && s.unitCount > 0)
                {
                    // a separate set of orders just for the comparison (half sensible, half wild)
                    var bot = rng.Next(2) == 0 ? basic : wild;
                    adj.ClearOrders();
                    for (int p = 0; p < map.powerCount; p++) g.Codec.DecodeOrders(bot.Choose(p), s, adj, p);
                    // An explicit "via convoy" to a neighbouring province is where rulebook editions differ
                    // (see DECISIONS.md, rule interpretations), so those are left out of the comparison.
                    for (int u = 0; u < s.unitCount; u++)
                        if (adj.oType[u] == Adjudicator.MOVE && adj.oVia[u] && s.unitType[u] == MapData.ARMY
                            && map.ArmyAdjacent(map.locProv[s.unitLoc[u]], map.locProv[adj.oA[u]])) adj.oVia[u] = false;
                    var units = new Dictionary<string, List<string>>(); var orders = new Dictionary<string, List<string>>(); var result = new Dictionary<string, List<string>>();
                    for (int p = 0; p < map.powerCount; p++) { string n = map.powerId[p].ToUpperInvariant(); units[n] = new(); orders[n] = new(); result[n] = new(); }
                    string U(int type, int loc) => Notation.TypeLetter(type) + " " + map.locId[loc].ToUpperInvariant();
                    for (int u = 0; u < s.unitCount; u++)
                    {
                        string pw = map.powerId[s.unitPower[u]].ToUpperInvariant();
                        string me = U(s.unitType[u], s.unitLoc[u]);
                        units[pw].Add(me);
                        int a = adj.oA[u], b = adj.oB[u];
                        string text = me + " H";
                        switch (adj.oType[u])
                        {
                            case Adjudicator.MOVE: text = me + " - " + map.locId[a].ToUpperInvariant() + (adj.oVia[u] ? " VIA" : ""); break;
                            case Adjudicator.SUPPORT_HOLD: { int t = s.provUnit[a]; text = me + " S " + U(s.unitType[t], s.unitLoc[t]); break; }
                            case Adjudicator.SUPPORT_MOVE: { int t = s.provUnit[a]; text = me + " S " + U(s.unitType[t], s.unitLoc[t]) + " - " + map.provId[map.locProv[b]].ToUpperInvariant(); break; }
                            case Adjudicator.CONVOY: { int t = s.provUnit[a]; text = me + " C " + U(s.unitType[t], s.unitLoc[t]) + " - " + map.provId[b].ToUpperInvariant(); break; }
                        }
                        orders[pw].Add(text);
                    }
                    probe.CopyFrom(s);
                    adj.ResolveMovement(probe);
                    for (int u = 0; u < adj.resCount; u++)
                    {
                        string pw = map.powerId[adj.resPower[u]].ToUpperInvariant();
                        int loc = adj.rMoved[u] ? (adj.resType[u] == MapData.ARMY ? map.locProv[adj.oA[u]] : adj.oA[u]) : adj.resLoc[u];
                        result[pw].Add((adj.rDislodged[u] ? "*" : "") + U(adj.resType[u], loc));
                    }
                    output.WriteLine(JsonSerializer.Serialize(new { id = written, units, orders, result }));
                    written++;
                }
                // the game itself moves on with sensible play
                for (int p = 0; p < map.powerCount; p++) if (f.NeedsOrders(p)) { f.Submit(p, basic.Choose(p)); f.SetSubmitted(p, true); }
                if (!f.Tick(step + 1)) break;
            }
        }
    }

    /// <summary>
    /// Writes whole bot games, phase by phase (movement, retreats and builds), as JSON lines:
    /// the orders of every phase and the position after it. A second engine replays each game
    /// from the start and compares the position after every phase (scripts/crosscheck_games.py).
    /// </summary>
    public static void ExportGames(string pmapText, int games, int seed, int maxYears, TextWriter output)
    {
        for (int gi = 0; gi < games; gi++)
        {
            var g = Game.FromPmap(pmapText);
            var map = g.Map; var s = g.State; var adj = g.Adj;
            var rng = new Random(seed * 1000003 + gi);
            var basic = new Bot(g, rng, true);
            var wild = new Bot(g, rng, false);
            string U(int type, int loc) => Notation.TypeLetter(type) + " " + map.locId[loc].ToUpperInvariant();
            string P(int p) => map.powerId[p].ToUpperInvariant();
            var phases = new List<object>();
            while (s.phase != BoardState.PHASE_FINISHED && s.year < map.startYear + maxYears)
            {
                string label = s.PhaseLabel();
                var orders = new Dictionary<string, List<string>>();
                for (int p = 0; p < map.powerCount; p++) orders[P(p)] = new();
                adj.ClearOrders();
                // movement: a mix of sensible and wild orders; retreats and builds: sensible
                var bot = s.phase == BoardState.PHASE_MOVE && rng.Next(3) == 0 ? wild : basic;
                for (int p = 0; p < map.powerCount; p++) g.Codec.DecodeOrders(bot.Choose(p), s, adj, p);
                // in half the games every required disband is ordered, so builds and disbands are
                // compared without the forced-disband rule (where rulebook editions differ)
                if (s.phase == BoardState.PHASE_ADJUST && gi % 2 == 0)
                    for (int p = 0; p < map.powerCount; p++)
                    {
                        int need = -adj.AdjustDelta(s, p);
                        var have = new HashSet<int>();
                        for (int i = 0; i < adj.adjCount; i++) if (adj.adjPower[i] == p && adj.adjKind[i] == Adjudicator.ADJ_DISBAND) have.Add(map.locProv[adj.adjLoc[i]]);
                        for (int u = 0; u < s.unitCount && have.Count < need; u++)
                            if (s.unitPower[u] == p && have.Add(map.locProv[s.unitLoc[u]])) adj.AddAdjust(p, Adjudicator.ADJ_DISBAND, s.unitLoc[u]);
                    }
                if (s.phase == BoardState.PHASE_MOVE)
                {
                    for (int u = 0; u < s.unitCount; u++)
                    {
                        // "via convoy" to a neighbouring province is where rulebook editions differ (DECISIONS §7)
                        if (adj.oType[u] == Adjudicator.MOVE && adj.oVia[u] && s.unitType[u] == MapData.ARMY
                            && map.ArmyAdjacent(map.locProv[s.unitLoc[u]], map.locProv[adj.oA[u]])) adj.oVia[u] = false;
                        string me = U(s.unitType[u], s.unitLoc[u]);
                        int a = adj.oA[u], b = adj.oB[u];
                        string text = me + " H";
                        switch (adj.oType[u])
                        {
                            case Adjudicator.MOVE: text = me + " - " + map.locId[a].ToUpperInvariant() + (adj.oVia[u] ? " VIA" : ""); break;
                            case Adjudicator.SUPPORT_HOLD: { int t = s.provUnit[a]; if (t >= 0) text = me + " S " + U(s.unitType[t], s.unitLoc[t]); break; }
                            case Adjudicator.SUPPORT_MOVE: { int t = s.provUnit[a]; if (t >= 0) text = me + " S " + U(s.unitType[t], s.unitLoc[t]) + " - " + map.provId[map.locProv[b]].ToUpperInvariant(); break; }
                            case Adjudicator.CONVOY: { int t = s.provUnit[a]; if (t >= 0) text = me + " C " + U(s.unitType[t], s.unitLoc[t]) + " - " + map.provId[b].ToUpperInvariant(); break; }
                        }
                        orders[P(s.unitPower[u])].Add(text);
                    }
                }
                else if (s.phase == BoardState.PHASE_RETREAT)
                {
                    for (int d = 0; d < s.disCount; d++)
                        orders[P(s.disPower[d])].Add(U(s.disType[d], s.disLoc[d]) + (adj.retDest[d] >= 0 ? " R " + map.locId[adj.retDest[d]].ToUpperInvariant() : " D"));
                }
                else if (s.phase == BoardState.PHASE_ADJUST)
                {
                    for (int i = 0; i < adj.adjCount; i++)
                    {
                        int k = adj.adjKind[i], loc = adj.adjLoc[i];
                        string text = k == Adjudicator.ADJ_BUILD_ARMY ? U(MapData.ARMY, loc) + " B"
                            : k == Adjudicator.ADJ_BUILD_FLEET ? U(MapData.FLEET, loc) + " B"
                            : k == Adjudicator.ADJ_DISBAND ? (s.provUnit[map.locProv[loc]] >= 0 ? U(s.unitType[s.provUnit[map.locProv[loc]]], s.unitLoc[s.provUnit[map.locProv[loc]]]) + " D" : "")
                            : "WAIVE";
                        if (text != "") orders[P(adj.adjPower[i])].Add(text);
                    }
                }
                int phaseBefore = s.phase;
                adj.Resolve(s);
                var units = new Dictionary<string, List<string>>(); var centres = new Dictionary<string, List<string>>();
                for (int p = 0; p < map.powerCount; p++) { units[P(p)] = new(); centres[P(p)] = new(); }
                for (int u = 0; u < s.unitCount; u++) units[P(s.unitPower[u])].Add(U(s.unitType[u], s.unitLoc[u]));
                if (s.phase == BoardState.PHASE_RETREAT)
                    for (int d = 0; d < s.disCount; d++) units[P(s.disPower[d])].Add("*" + U(s.disType[d], s.disLoc[d]));
                for (int pv = 0; pv < map.provCount; pv++) if (s.owner[pv] >= 0) centres[P(s.owner[pv])].Add(map.provId[pv].ToUpperInvariant());
                var forced = new List<string>();
                if (phaseBefore == BoardState.PHASE_ADJUST)
                    for (int i = 0; i < adj.autoDisbandCount; i++) forced.Add(P(adj.autoDisbandPower[i]));
                phases.Add(new { label, orders, units, centres, next = s.PhaseLabel(), forced });
            }
            output.WriteLine(JsonSerializer.Serialize(new { game = gi, phases }));
        }
    }
}
