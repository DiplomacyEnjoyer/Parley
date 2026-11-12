// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Random play of the Purse and Dagger ruleset through the whole stack, checking after
/// every phase that the position is possible, survives a game code, and that replaying
/// the recorded sheets with the same dice gives the same position. Test equipment, not
/// an opponent: the "players" order at random, spend at random and build at random.
/// </summary>
public static class PurseSim
{
    public sealed class Report
    {
        public int Games, Phases, Finished;
        public List<string> Problems = new();
        public Dictionary<string, int> Seen = new();
        public void Count(string what) { Seen[what] = Seen.GetValueOrDefault(what) + 1; }
    }

    public static Report Run(string pmap, int games, int seed, int years)
    {
        var rep = new Report();
        for (int n = 0; n < games; n++)
        {
            var g = Game.FromPmap(pmap);
            var rnd = new Random(seed * 7919 + n);
            try { PlayOne(g, rnd, years, rep, pmap); }
            catch (Exception ex) { rep.Problems.Add($"game {n}: crashed in {g.State.PhaseLabel()}: {ex.GetType().Name}: {ex.Message} {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Parley"))}"); }
            rep.Games++;
            if (rep.Problems.Count > 20) break;
        }
        return rep;
    }

    private static void PlayOne(Game g, Random rnd, int years, Report rep, string pmap)
    {
        var f = g.Flow; var s = g.State; var m = g.Map; var pr = g.Purse;
        for (int p = 0; p < m.powerCount; p++) if (g.Rules.Playable(p)) f.ClaimSeat(p, "P" + p);
        if (!f.StartGame(0)) { rep.Problems.Add("could not start: " + f.error); return; }
        int guard = 0;
        var replay = Game.FromPmap(pmap);
        while (f.stage != GameFlow.STAGE_FINISHED && s.year < m.startYear + years && guard++ < 2000)
        {
            string before = g.Codec.EncodeState(s);
            f.diceNoise = "noise" + rnd.Next();
            var sheets = new string[m.powerCount];
            for (int p = 0; p < m.powerCount; p++)
            {
                if (!f.NeedsOrders(p)) continue;
                sheets[p] = Sheet(g, p, rnd, rep);
                if (!f.Submit(p, sheets[p])) { rep.Problems.Add($"{s.PhaseLabel()} {m.powerName[p]}: sheet refused ({f.error}): {sheets[p]}"); return; }
                f.SetSubmitted(p, true);
            }
            string label = s.PhaseLabel();
            string seedUsedKey = f.SealKey();
            if (!f.Tick(1) && f.stage == GameFlow.STAGE_ORDERS) f.ForceNext(1);
            rep.Phases++;
            for (int i = 0; i < g.Announce.count; i++) if (g.Announce.kind[i] == Announcement.K_NOTE) rep.Count(Shape(g.Announce.note[i]));
            string problem = Check(g);
            if (problem != null) { rep.Problems.Add($"{label} -> {s.PhaseLabel()}: {problem}"); return; }
            // the game code must carry everything
            string code = g.Codec.EncodeState(s);
            if (!g.Codec.DecodeState(code, g.Scratch)) { rep.Problems.Add($"{label}: code does not load: {g.Codec.error}"); return; }
            if (g.Codec.EncodeState(g.Scratch) != code) { rep.Problems.Add($"{label}: code changes when loaded and saved again"); return; }
            // replaying the recorded sheets from the earlier code with the same dice gives the same position
            if (!replay.Codec.DecodeState(before, replay.State)) { rep.Problems.Add($"{label}: earlier code does not load: {replay.Codec.error}"); return; }
            replay.Rules.ClearOrders();
            string recorded = g.History.orders[g.History.count - 2];
            if (!replay.Rules.AddOrders(recorded, replay.State, -1)) { rep.Problems.Add($"{label}: recorded sheets do not load: {replay.Rules.error}"); return; }
            replay.Rules.Resolve(replay.State, f.lastDiceSeed, replay.Announce, null);
            if (replay.Codec.EncodeState(replay.State) != code) { rep.Problems.Add($"{label}: replaying the recorded sheets gives a different position\n   {code}\n   {replay.Codec.EncodeState(replay.State)}"); return; }
        }
        if (f.stage == GameFlow.STAGE_FINISHED) rep.Finished++;
        // the whole game survives an archive
        var again = Game.FromPmap(pmap);
        if (!again.Flow.LoadArchive(f.Archive(), 0)) rep.Problems.Add("archive does not load: " + again.Flow.error);
        else if (again.Codec.EncodeState(again.State) != g.Codec.EncodeState(s)) rep.Problems.Add("archive loads to a different position");
    }

    // "12 ducats: the army in Milan is bribed to disband" -> a short key for the statistics
    private static string Shape(string note)
    {
        foreach (var key in new[] { "bribed to disband", "is bought", "autonomous", "rises in rebellion", "is assassinated", "attempt on the ruler", "pacified", "famine relief",
            "counter-bribe", "fails", "Borrows", "bank collects", "cannot repay", "Repays", "Gives", "siege of", "put down", "welcome", "starves", "plague", "Famine will", "out of the game",
            "now holds", "surrenders", "refuses a loan", "cannot come out" })
            if (note.Contains(key)) return key;
        return note.StartsWith("Income") || note.StartsWith("Treasury") ? "income" : "other";
    }

    /// <summary>A position nobody could reach means a bug. Returns what is wrong, or null.</summary>
    public static string Check(Game g)
    {
        var s = g.State; var m = g.Map; var pr = g.Purse;
        var seen = new HashSet<int>();
        for (int u = 0; u < s.unitCount; u++)
        {
            int q = m.locProv[s.unitLoc[u]];
            if (!seen.Add(q)) return "two units in " + m.provId[q];
            if (!m.CanStand(s.unitType[u], s.unitLoc[u])) return $"a unit of type {s.unitType[u]} cannot stand in {m.locId[s.unitLoc[u]]}";
            if (s.provUnit[q] != u) return "unit index out of step at " + m.provId[q];
            if (s.unitPower[u] != pr.autoPower && !pr.Alive(s, s.unitPower[u])) return "a unit of an eliminated power in " + m.provId[q];
            if (s.unitFlag[u] < 0 || s.unitFlag[u] > 3) return "bad unit class";
        }
        for (int p = 0; p < m.powerCount; p++)
        {
            if (pr.Treasury(s, p) < 0) return m.powerName[p] + " has a negative treasury";
            for (int q = 0; q < m.powerCount; q++) if (pr.Chits(s, p, q) < 0) return "negative assassination tokens";
            for (int i = 0; i < PurseRules.MAX_LOANS; i++)
                if (pr.LoanPrincipal(s, p, i) < 0 || pr.LoanInterest(s, p, i) < 0 || (pr.LoanDue(s, p, i) < 0) != (pr.LoanPrincipal(s, p, i) + pr.LoanInterest(s, p, i) == 0)) return m.powerName[p] + ": loan book is inconsistent";
        }
        for (int q = 0; q < m.landCount; q++)
        {
            int c = m.provCity[q];
            if (c >= 0 && s.provUnit[c] >= 0 && (s.provFlag[c] & PurseRules.F_REBEL) != 0) return "a garrisoned city in rebellion: " + m.provId[q];
            if (m.provSingle[q] && s.provUnit[q] >= 0 && (s.provFlag[q] & PurseRules.F_REBEL) != 0) return "rebels and a unit share " + m.provId[q];
            int u = s.provUnit[q];
            if ((s.provFlag[q] & PurseRules.F_REBEL) != 0 && u >= 0 && s.unitPower[u] != s.owner[q]) return "an outsider stands in rebelling " + m.provId[q];
            if ((s.provFlag[q] & PurseRules.F_SIEGE) != 0 && (u < 0 || s.unitType[u] == MapData.GARRISON)) return "a siege without a besieger in " + m.provId[q];
            if (u >= 0 && m.provType[q] != MapData.SEA && s.owner[q] != s.unitPower[u]) return m.provId[q] + " is not controlled by the unit standing in it";
        }
        if (s.phase == BoardState.PHASE_FINISHED && !s.drawn && (s.winner < 0 || !pr.Alive(s, s.winner))) return "finished without a living winner";
        return null;
    }

    private static string Sheet(Game g, int p, Random rnd, Report rep)
    {
        var s = g.State; var m = g.Map; var pr = g.Purse;
        var pad = new Adjudicator(); pad.Init(m);
        if (s.phase == BoardState.PHASE_RETREAT)
        {
            for (int d = 0; d < s.disCount; d++)
            {
                if (s.disPower[d] != p) continue;
                var ok = Enumerable.Range(0, m.locCount).Where(l => g.Adj.RetreatValid(s, d, l)).ToList();
                if (ok.Count > 0 && rnd.Next(5) > 0) pad.SetRetreat(d, ok[rnd.Next(ok.Count)]);
            }
            return g.Codec.EncodeOrders(s, pad, p);
        }
        var money = new List<string>();
        if (s.phase == BoardState.PHASE_ADJUST)
        {
            int keep = 0;
            for (int u = 0; u < s.unitCount; u++)
            {
                if (s.unitPower[u] != p) continue;
                if (rnd.Next(25) == 0) money.Add("D" + pr.PlaceChars(s.unitLoc[u])); else keep += pr.ClassCost(s.unitFlag[u]);
            }
            int spare = pr.Treasury(s, p) - keep;
            for (int l = 0; l < m.locCount && spare >= 3; l++)
            {
                int type = rnd.Next(3); int cls = pr.Has(s, PurseRules.OPT_SPECIAL) && rnd.Next(6) == 0 ? 1 + rnd.Next(3) : 0;
                if (rnd.Next(3) != 0 || !pr.BuildOk(s, p, type, cls, l) || pr.ClassCost(cls) > spare) continue;
                money.Add("N" + type + cls + pr.PlaceChars(l)); spare -= pr.ClassCost(cls);
            }
            if (rnd.Next(30) == 0) money.Add("N00" + pr.PlaceChars(rnd.Next(m.locCount))); // nonsense now and then
            return money.Count == 0 ? "" : "!" + string.Join(",", money);
        }
        // a campaign: unit orders ...
        for (int u = 0; u < s.unitCount; u++)
        {
            if (s.unitPower[u] != p) continue;
            int loc = s.unitLoc[u], q = m.locProv[loc], type = s.unitType[u];
            int roll = rnd.Next(10);
            if (type == MapData.GARRISON)
            {
                int outside = m.cityProv[q] >= 0 ? m.cityProv[q] : q;
                if (roll < 3)
                {
                    int n = g.Options.ListConversions(s, u);
                    if (n > 0) { int i = rnd.Next(n); pad.SetConvert(u, g.Options.optA[i], g.Options.optB[i]); }
                }
                else if (roll < 6 && s.provUnit[outside] >= 0 && outside != q) pad.SetSupportHold(u, outside);
                else if (roll < 8 && outside != q)
                {
                    var movers = Enumerable.Range(0, s.unitCount).Where(x => x != u && g.Options.ListMoves(s, x) > 0 && Enumerable.Range(0, g.Options.optCount).Any(i => m.locProv[g.Options.optA[i]] == outside)).ToList();
                    if (movers.Count > 0) pad.SetSupportMove(u, m.locProv[s.unitLoc[movers[rnd.Next(movers.Count)]]], outside);
                }
                continue;
            }
            int city = m.provCity[q];
            if (roll == 0 && g.Options.ListConversions(s, u) > 0) pad.SetConvert(u, g.Options.optA[0], g.Options.optB[0]);
            else if (roll == 1 || (roll < 5 && (s.provFlag[q] & PurseRules.F_SIEGE) != 0) || (roll < 4 && city >= 0 && s.provUnit[city] >= 0 && s.unitPower[s.provUnit[city]] != p)) pad.SetBesiege(u);
            else if (roll == 2 && (s.provFlag[q] & PurseRules.F_SIEGE) != 0) pad.SetLiftSiege(u);
            else if (roll < 8)
            {
                int n = g.Options.ListMoves(s, u);
                if (n > 0) { int i = rnd.Next(n); pad.SetMove(u, g.Options.optA[i], g.Options.optB[i] == 1); }
            }
            else if (roll == 8)
            {
                int n = g.Options.ListSupportable(s, u);
                if (n > 0)
                {
                    int target = g.Options.optA[rnd.Next(n)];
                    int k = g.Options.ListSupportTargets(s, u, target);
                    if (k > 0) { int i = rnd.Next(k); if (g.Options.optA[i] < 0) pad.SetSupportHold(u, target); else pad.SetSupportMove(u, target, g.Options.optA[i]); }
                }
            }
            else if (type == MapData.FLEET)
            {
                int n = g.Options.ListConvoyArmies(s, u);
                if (n > 0)
                {
                    int army = g.Options.optA[rnd.Next(n)];
                    int k = g.Options.ListConvoyDestinations(s, u, army);
                    if (k > 0) pad.SetConvoy(u, army, g.Options.optA[rnd.Next(k)]);
                }
            }
        }
        // ... and money
        int cash = pr.Treasury(s, p);
        if (pr.Debt(s, p) > 0 && rnd.Next(3) == 0) money.Add("P" + rnd.Next(1, 30));
        if (rnd.Next(12) == 0) money.Add("G" + pr.PowerChar(rnd.Next(m.powerCount)) + rnd.Next(1, 8));
        if (rnd.Next(10) == 0) money.Add("B" + (1 + rnd.Next(2)) + rnd.Next(1, 30));
        if (rnd.Next(25) == 0) { int x = rnd.Next(m.powerCount); money.Add("T" + pr.PowerChar(x) + pr.PowerChar(rnd.Next(m.powerCount))); }
        int expenses = rnd.Next(4) == 0 ? rnd.Next(1, 6) : 0;
        for (int e = 0; e < expenses; e++)
        {
            int kind = 1 + rnd.Next(8);
            if (kind == PurseRules.EX_ASSASSINATE) money.Add(pr.ExpenseItem(kind, rnd.Next(m.powerCount), 12 * rnd.Next(1, 4)));
            else if (kind == PurseRules.EX_RELIEF || kind == PurseRules.EX_PACIFY || kind == PurseRules.EX_REBEL)
            {
                var want = Enumerable.Range(0, m.landCount).Where(q => kind == PurseRules.EX_RELIEF ? (s.provFlag[q] & PurseRules.F_FAMINE) != 0
                    : kind == PurseRules.EX_PACIFY ? ((s.provFlag[q] & PurseRules.F_REBEL) != 0 || pr.CityRebels(s, q)) : pr.RebellionCost(s, p, q) > 0).ToList();
                int q = want.Count > 0 && rnd.Next(8) > 0 ? want[rnd.Next(want.Count)] : rnd.Next(m.landCount);
                money.Add(pr.ExpenseItem(kind, q, kind == PurseRules.EX_RELIEF ? 3 : kind == PurseRules.EX_PACIFY ? 12 : 9 + 6 * rnd.Next(2)));
            }
            else if (s.unitCount > 0)
            {
                int u = rnd.Next(s.unitCount);
                int need = kind == PurseRules.EX_COUNTER ? 3 : pr.MinBribe(s, p, kind, s.unitLoc[u]);
                money.Add(pr.ExpenseItem(kind, s.unitLoc[u], Math.Max(3, need) + 3 * rnd.Next(3)));
            }
        }
        string units = g.Codec.EncodeOrders(s, pad, p);
        return money.Count == 0 ? units : units + "!" + string.Join(",", money);
    }
}
