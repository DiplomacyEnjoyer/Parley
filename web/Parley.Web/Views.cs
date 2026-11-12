// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;

namespace Parley.Web;

/// <summary>Builds the JSON the web page shows. Each player only ever receives their own orders.</summary>
public static class Views
{
    public static object MapJson(MapData map, SvgArt.Art art = null)
    {
        if (art != null) return ArtMapJson(map, art);
        bool hasLayout = Enumerable.Range(0, map.provCount).All(p => map.provX[p] >= 0);
        int[] x = map.provX, y = map.provY;
        int w = map.layoutWidth > 0 ? map.layoutWidth : 1000, hgt = map.layoutHeight > 0 ? map.layoutHeight : 1000;
        if (!hasLayout) { (x, y) = AutoLayout.Compute(map); w = 1000; hgt = 1000; }
        // a walled city is drawn beside the province it stands in
        x = (int[])x.Clone(); y = (int[])y.Clone();
        for (int p = map.landCount; p < map.provCount; p++) { x[p] = x[map.cityProv[p]] + 15; y[p] = y[map.cityProv[p]] + 12; }
        var links = new List<int[]>();
        for (int p = 0; p < map.provCount; p++)
            for (int i = 0; i < map.anyCount[p]; i++)
            {
                int q = map.anyAdj[map.anyStart[p] + i];
                if (q > p) links.Add(new[] { p, q });
            }
        return new
        {
            id = map.mapId, name = map.mapName, hash = map.hash, credit = map.credit, license = map.license,
            width = w, height = hgt, victory = map.victoryCenters, drawn = hasLayout,
            rules = map.rulesId, seasons = Seasons(map),
            powers = Enumerable.Range(0, map.powerCount).Select(i => new { id = map.powerId[i], name = map.powerName[i], color = map.powerColor[i] }),
            provs = Enumerable.Range(0, map.provCount).Select(p => new
            {
                id = map.provId[p], name = map.provName[p], type = map.provType[p], home = map.provHome[p],
                // in a game with cities the marker under a province is its city, not a supply centre
                sc = map.rulesId == "standard" ? map.provSc[p] : (p < map.landCount && map.cityKind[p] != MapData.CITY_NONE),
                city = map.cityKind[p], income = map.cityIncome[p], port = map.cityPort[p], node = map.provCity[p], around = map.cityProv[p],
                x = x[p], y = y[p], shape = hasLayout ? map.provShape[p] : ""
            }),
            locs = Enumerable.Range(0, map.locCount).Select(l => new { id = map.locId[l], prov = map.locProv[l], coast = map.locCoast[l], name = map.LocName(l) }),
            links
        };
    }

    /// <summary>A map with an imported picture: the drawn board, a clickable outline per province, unit spots.</summary>
    private static object ArtMapJson(MapData map, SvgArt.Art art)
    {
        double[] Spot(string id, int prov)
        {
            if (art.Units.TryGetValue(id, out var u)) return u;
            if (art.Units.TryGetValue(map.provId[prov], out u)) return u;
            return new double[] { map.provX[prov] >= 0 ? map.provX[prov] : art.Width / 2, map.provY[prov] >= 0 ? map.provY[prov] : art.Height / 2 };
        }
        return new
        {
            id = map.mapId, name = map.mapName, hash = map.hash, credit = map.credit, license = map.license,
            width = art.Width, height = art.Height, victory = map.victoryCenters, drawn = true,
            rules = map.rulesId, seasons = Seasons(map),
            art = art.Background, labels = art.Labels, artSource = art.Source,
            powers = Enumerable.Range(0, map.powerCount).Select(i => new { id = map.powerId[i], name = map.powerName[i], color = map.powerColor[i] }),
            provs = Enumerable.Range(0, map.provCount).Select(p =>
            {
                var at = Spot(map.provId[p], p);
                art.Centres.TryGetValue(map.provId[p], out var sc);
                return new
                {
                    id = map.provId[p], name = map.provName[p], type = map.provType[p], sc = map.provSc[p], home = map.provHome[p],
                    x = at[0], y = at[1], shape = "", svg = art.Provs.TryGetValue(map.provId[p], out var m) ? m : "",
                    scx = sc != null ? sc[0] : -1, scy = sc != null ? sc[1] : -1
                };
            }),
            locs = Enumerable.Range(0, map.locCount).Select(l =>
            {
                var at = Spot(map.locId[l], map.locProv[l]);
                return new { id = map.locId[l], prov = map.locProv[l], coast = map.locCoast[l], name = map.LocName(l), x = at[0], y = at[1] };
            }),
            links = new List<int[]>()
        };
    }

    private static object Seasons(MapData map) =>
        Enumerable.Range(0, map.seasonCount + 1).Select(i => new { letter = map.seasonLetters.Substring(i, 1), name = map.seasonNames[i] }).ToList();

    private static object Units(BoardState s) =>
        Enumerable.Range(0, s.unitCount).Select(u => new { t = s.unitType[u], p = s.unitPower[u], l = s.unitLoc[u], c = s.unitFlag[u] }).ToList();

    /// <summary>
    /// What a Purse and Dagger game shows besides the board. All of it is public: every
    /// device holds the whole position, so nothing in it could be kept secret anyway.
    /// </summary>
    private static object PurseInfo(Game g)
    {
        if (g.Rules.kind != Ruleset.KIND_PURSE) return null;
        var s = g.State; var map = g.Map; var pr = g.Purse; int np = map.powerCount;
        return new
        {
            options = pr.Options(s), victoryHomes = pr.victoryHomes, auto = pr.autoPower,
            treasury = Enumerable.Range(0, np).Select(p => pr.Treasury(s, p)),
            debt = Enumerable.Range(0, np).Select(p => pr.Debt(s, p)),
            cities = Enumerable.Range(0, np).Select(p => pr.CitiesHeld(s, p)),
            homes = Enumerable.Range(0, np).Select(p => pr.HomesHeld(s, p)),
            homeHolder = Enumerable.Range(0, np).Select(h => pr.HomeHolder(s, h)),
            income = Enumerable.Range(0, np).Select(p => pr.Alive(s, p) ? pr.FixedIncome(s, p) : 0),
            flags = s.provFlag.Take(map.provCount),
            campaign = pr.CampaignNumber(s),
        };
    }

    private static object Dislodged(BoardState s) =>
        s.phase != BoardState.PHASE_RETREAT ? new List<object>()
        : Enumerable.Range(0, s.disCount).Select(d => (object)new { t = s.disType[d], p = s.disPower[d], l = s.disLoc[d] }).ToList();

    public static object State(WebGame wg, Player me, string token, float now)
    {
        var g = wg.G; var f = g.Flow; var s = g.State; var map = g.Map; var h = g.History;
        int myPower = me == null ? -1 : f.SeatOf(me.Name);
        bool open = f.stage == GameFlow.STAGE_ORDERS || f.stage == GameFlow.STAGE_PAUSED || f.stage == GameFlow.STAGE_CLOSING || f.stage == GameFlow.STAGE_REVEAL;
        object my = null;
        if (myPower >= 0 && open)
        {
            if (!wg.OptionCache.TryGetValue(myPower, out var cached) || cached.serial != f.phaseSerial)
            {
                cached = (f.phaseSerial, Options(g, myPower));
                wg.OptionCache[myPower] = cached;
            }
            my = new
            {
                power = myPower, submitted = f.submitted[myPower], sealedOrders = f.sealText[myPower] != "", opened = f.opened[myPower],
                needs = f.NeedsOrders(myPower), needsSeal = f.NeedsSeal(myPower),
                delta = s.phase == BoardState.PHASE_ADJUST && g.Rules.kind == Ruleset.KIND_STANDARD ? g.Adj.AdjustDelta(s, myPower) : 0, options = cached.value
            };
        }
        object announce = null;
        var an = g.Announce;
        if (an.count > 0 && f.stage != GameFlow.STAGE_LOBBY)
        {
            int read = an.count; int at = Announcement.AT_DONE; int atPower = -1;
            if (f.stage == GameFlow.STAGE_ANNOUNCE)
            {
                f.LocateAnnouncement(now);
                at = an.at; atPower = an.atPower;
                read = an.at == Announcement.AT_LEAD ? 0 : an.at == Announcement.AT_POWER ? FirstOf(an, atPower) : an.at == Announcement.AT_ORDER ? an.ReadIndex(an.atLine) + 1 : an.count;
            }
            announce = new
            {
                label = an.label, at, atPower, read,
                status = Enumerable.Range(0, map.powerCount).Select(p => an.StatusText(an.powerStatus[p])),
                lines = Enumerable.Range(0, an.count).Select(k => an.seq[k]).Select(i => new
                {
                    p = an.power[i], text = an.Text(i), res = an.OutcomeText(i), from = an.fromLoc[i], to = an.moveTo[i], moved = an.moved[i]
                }),
                battles = Enumerable.Range(0, an.battleCount).Select(k => new { prov = an.battleProv[k], movers = an.battleMovers[k], supports = an.battleSupports[k], winner = an.battleWinner[k] })
            };
        }
        return new
        {
            id = wg.Id, title = wg.Title, stage = f.stage, serial = f.phaseSerial, sealKey = open ? f.SealKey() : "",
            label = f.stage == GameFlow.STAGE_LOBBY ? "Lobby" : s.PhaseLabel(), phase = s.phase, year = s.year, season = s.season,
            secondsLeft = f.SecondsLeft(now), waitForAbsent = f.waitForAbsent, closeReason = f.closeReason,
            schedule = h.moveSchedule.Take(h.moveScheduleCount), timers = new[] { h.retreatSeconds, h.adjustSeconds },
            announceSpeed = Lobby.AnnounceName(f), readyUp = f.readyMode != GameFlow.READY_NEVER,
            isHost = token != null && token == wg.HostToken,
            host = wg.HostToken != null && wg.Players.TryGetValue(wg.HostToken, out var hp) ? hp.Name : "",
            me = me == null ? null : new { name = me.Name, power = myPower },
            seats = Enumerable.Range(0, map.powerCount).Select(p => new
            {
                power = p, name = h.seatName[p], present = f.seatPresent[p], submitted = f.submitted[p], opened = f.opened[p], ready = f.ready[p],
                needs = f.NeedsOrders(p), vote = f.drawVote[p], units = s.CountUnits(p), centres = s.CountCenters(p),
                gone = f.stage != GameFlow.STAGE_LOBBY && g.Rules.IsOut(s, p), playable = g.Rules.Playable(p), unopened = f.unopened[p]
            }),
            units = Units(s), owners = s.owner.Take(map.provCount), dislodged = Dislodged(s),
            code = f.stage == GameFlow.STAGE_LOBBY ? "" : f.CurrentCode(),
            historyCount = h.count, winner = s.winner, drawn = s.drawn,
            rules = g.Rules.Title(), purse = PurseInfo(g), diceSeed = f.lastDiceSeed,
            announce, my
        };
    }

    private static int FirstOf(Announcement an, int power)
    {
        for (int g = 0; g < an.groupCount; g++) if (an.groupPower[g] == power) return an.groupFirst[g];
        return 0;
    }

    private static string AdjustText(MapData map, int kind, int loc) => kind switch
    {
        Adjudicator.ADJ_BUILD_ARMY => "Build A " + map.locId[loc],
        Adjudicator.ADJ_BUILD_FLEET => "Build F " + map.locId[loc],
        Adjudicator.ADJ_DISBAND => "Disband the unit in " + map.provId[map.locProv[loc]],
        _ => "Waive a build"
    };

    /// <summary>Everything this power may tap in the current phase, straight from the engine's option generator.</summary>
    private static object Options(Game g, int power)
    {
        var s = g.State; var map = g.Map; var opt = g.Options;
        bool purse = g.Rules.kind == Ruleset.KIND_PURSE;
        if (purse && s.phase == BoardState.PHASE_ADJUST) return PurseAdjustOptions(g, power);
        if (s.phase == BoardState.PHASE_MOVE)
        {
            var units = new List<object>();
            for (int u = 0; u < s.unitCount; u++)
            {
                if (s.unitPower[u] != power) continue;
                int n = opt.ListMoves(s, u);
                var moves = Enumerable.Range(0, n).Select(i => new { l = opt.optA[i], via = opt.optB[i] == 1 }).ToList();
                n = opt.ListSupportable(s, u);
                var provs = Enumerable.Range(0, n).Select(i => opt.optA[i]).ToList();
                var sup = new List<object>();
                foreach (int tp in provs)
                {
                    int m = opt.ListSupportTargets(s, u, tp);
                    sup.Add(new { p = tp, to = Enumerable.Range(0, m).Select(i => opt.optA[i]).ToList() });
                }
                n = opt.ListConvoyArmies(s, u);
                var armies = Enumerable.Range(0, n).Select(i => opt.optA[i]).ToList();
                var con = new List<object>();
                foreach (int ap in armies)
                {
                    int m = opt.ListConvoyDestinations(s, u, ap);
                    con.Add(new { p = ap, to = Enumerable.Range(0, m).Select(i => opt.optA[i]).ToList() });
                }
                n = opt.ListConversions(s, u);
                var conv = Enumerable.Range(0, n).Select(i => new { l = opt.optA[i], t = opt.optB[i] }).ToList();
                // sieges: an army, or a fleet at a port, standing outside a walled city
                int q = map.locProv[s.unitLoc[u]];
                bool siege = purse && s.unitType[u] != MapData.GARRISON && map.provCity[q] >= 0 && (s.unitType[u] == MapData.ARMY || map.cityPort[q]);
                bool sieging = purse && siege && (s.provFlag[q] & PurseRules.F_SIEGE) != 0;
                units.Add(new { l = s.unitLoc[u], t = s.unitType[u], moves, sup, con, conv, siege, sieging });
            }
            if (purse) return new { units, spend = PurseSpendOptions(g, power) };
            return new { units };
        }
        if (s.phase == BoardState.PHASE_RETREAT)
        {
            var dis = new List<object>();
            for (int d = 0; d < s.disCount; d++)
            {
                if (s.disPower[d] != power) continue;
                int n = opt.ListRetreats(s, d);
                dis.Add(new { l = s.disLoc[d], t = s.disType[d], to = Enumerable.Range(0, n).Select(i => opt.optA[i]).ToList() });
            }
            return new { dis };
        }
        if (s.phase == BoardState.PHASE_ADJUST)
        {
            int n = opt.ListBuilds(s, power);
            var builds = Enumerable.Range(0, n).Select(i => new { l = opt.optA[i], t = opt.optB[i] }).ToList();
            n = opt.ListDisbands(s, power);
            var disbands = Enumerable.Range(0, n).Select(i => opt.optA[i]).ToList();
            return new { builds, disbands };
        }
        return new { };
    }

    /// <summary>What a power can spend money on in a campaign: targets and the least each would cost.</summary>
    private static object PurseSpendOptions(Game g, int power)
    {
        var s = g.State; var map = g.Map; var pr = g.Purse;
        bool near = !pr.Has(s, PurseRules.OPT_ADJACENCY);
        var bribes = new List<object>();
        for (int u = 0; u < s.unitCount; u++)
        {
            int loc = s.unitLoc[u]; int land = pr.LandOf(loc);
            bool reach = near || pr.Near(s, power, land, false);
            int d = reach ? pr.MinBribe(s, power, PurseRules.EX_DISBAND, loc) : 0, b = reach ? pr.MinBribe(s, power, PurseRules.EX_BUY, loc) : 0, a = reach ? pr.MinBribe(s, power, PurseRules.EX_AUTONOMOUS, loc) : 0;
            bool counter = s.unitPower[u] == power || reach;
            if (d > 0 || b > 0 || a > 0 || counter) bribes.Add(new { l = loc, t = s.unitType[u], p = s.unitPower[u], d, b, a, counter });
        }
        var rebel = new List<object>(); var relief = new List<int>(); var pacify = new List<int>();
        for (int q = 0; q < map.landCount; q++)
        {
            if (map.provType[q] == MapData.SEA) continue;
            int cost = pr.RebellionCost(s, power, q);
            if (cost > 0 && (near || pr.Near(s, power, q, false))) rebel.Add(new { p = q, cost });
            bool close = near || pr.Near(s, power, q, true);
            if ((s.provFlag[q] & PurseRules.F_FAMINE) != 0 && close) relief.Add(q);
            if (((s.provFlag[q] & PurseRules.F_REBEL) != 0 || pr.CityRebels(s, q)) && close) pacify.Add(q);
        }
        var kill = Enumerable.Range(0, map.powerCount).Where(v => v != power && pr.Alive(s, v) && pr.Chits(s, power, v) > 0 && pr.Has(s, PurseRules.OPT_ASSASSINS)).ToList();
        var others = Enumerable.Range(0, map.powerCount).Where(v => v != power && pr.Alive(s, v)).ToList();
        var loans = Enumerable.Range(0, PurseRules.MAX_LOANS).Where(i => pr.LoanDue(s, power, i) >= 0)
            .Select(i => new { principal = pr.LoanPrincipal(s, power, i), interest = pr.LoanInterest(s, power, i), dueIn = pr.LoanDue(s, power, i) - pr.CampaignNumber(s) }).ToList();
        return new
        {
            treasury = pr.Treasury(s, power), debt = pr.Debt(s, power), loans,
            canBorrow = pr.Has(s, PurseRules.OPT_LOANS) && !pr.Barred(s, power),
            bribes, rebel, relief, pacify, kill, others,
            tokens = Enumerable.Range(0, map.powerCount).Select(v => pr.Chits(s, power, v)),
        };
    }

    /// <summary>The winter: what each unit costs to keep, and where something new could be built.</summary>
    private static object PurseAdjustOptions(Game g, int power)
    {
        var s = g.State; var map = g.Map; var pr = g.Purse;
        var mine = Enumerable.Range(0, s.unitCount).Where(u => s.unitPower[u] == power)
            .Select(u => new { l = s.unitLoc[u], t = s.unitType[u], cost = pr.ClassCost(s.unitFlag[u]) }).ToList();
        var builds = new List<object>();
        for (int l = 0; l < map.locCount; l++)
            for (int t = 0; t < map.unitTypeCount; t++)
                if ((t != MapData.ARMY || l < map.provCount) && pr.BuildOk(s, power, t, PurseRules.CLASS_NORMAL, l)) builds.Add(new { l, t });
        // dismissing a unit can make room: its place is offered too, and the engine has the last word
        return new { purse = true, treasury = pr.Treasury(s, power), unitCost = PurseRules.UNIT_COST, mine, builds };
    }

    /// <summary>
    /// A past phase of a game with its own ruleset: replayed through that ruleset, with the
    /// dice seed the archive recorded, and reported from the announcement.
    /// </summary>
    private static object PurseHistoryEntry(WebGame wg, int index)
    {
        var g = wg.G; var h = g.History;
        wg.Replay ??= Game.FromPmap(wg.Info.Pmap);
        var r = wg.Replay; var s = r.State;
        if (!r.Codec.DecodeBody(h.body[index], s)) return null;
        string label = s.PhaseLabel();
        var units = Units(s); var owners = s.owner.Take(r.Map.provCount).ToList(); var dislodged = Dislodged(s);
        var flags = s.provFlag.Take(r.Map.provCount).ToList();
        var orders = new List<object>();
        bool resolved = index < h.count - 1 && s.phase != BoardState.PHASE_FINISHED;
        if (resolved)
        {
            r.Rules.ClearOrders();
            if (r.Rules.AddOrders(h.OrdersAt(index), s, -1))
            {
                r.Rules.Resolve(s, h.SeedAt(index), r.Announce, null);
                var an = r.Announce;
                for (int k = 0; k < an.count; k++)
                {
                    int i = an.seq[k];
                    string res = an.OutcomeText(i);
                    int kind = an.kind[i] == Announcement.K_CONVERT || an.kind[i] == Announcement.K_RETREAT ? 1 : an.kind[i] <= Announcement.K_CONVOY ? an.kind[i] : 0;
                    orders.Add(new { p = an.power[i], text = an.Text(i), res = res == "" ? "ok" : res, ok = res == "", k = kind, u = an.fromLoc[i], a = an.kind[i] == Announcement.K_CONVERT ? an.moveTo[i] : an.a[i], b = an.b[i] });
                }
            }
        }
        return new { index, label, resolved, units, owners, dislodged, orders, flags, code = h.CodeAt(index) };
    }

    /// <summary>One past phase: the position it started from, and every order with its outcome.</summary>
    public static object HistoryEntry(WebGame wg, int index)
    {
        var g = wg.G; var h = g.History; var map = g.Map;
        if (index < 0 || index >= h.count) return null;
        if (g.Rules.kind != Ruleset.KIND_STANDARD) return PurseHistoryEntry(wg, index);
        var s = new BoardState(); s.Init(map);
        if (!g.Codec.DecodeBody(h.body[index], s)) return null;
        string label = s.PhaseLabel();
        var units = Units(s); var owners = s.owner.Take(map.provCount).ToList(); var dislodged = Dislodged(s);
        var orders = new List<object>();
        bool resolved = index < h.count - 1;
        if (resolved && s.phase != BoardState.PHASE_FINISHED)
        {
            var adj = new Adjudicator(); adj.Init(map);
            g.Codec.DecodeOrders(h.orders[index], s, adj, -1);
            if (s.phase == BoardState.PHASE_MOVE)
            {
                int n = s.unitCount;
                var text = new string[n]; var pw = new int[n]; var from = new int[n]; var kind = new int[n]; var a = new int[n]; var b = new int[n];
                for (int u = 0; u < n; u++)
                {
                    text[u] = g.Notation.MovementOrder(s.unitType[u], s.unitLoc[u], adj.oType[u], adj.oA[u], adj.oB[u], adj.oVia[u]);
                    pw[u] = s.unitPower[u]; from[u] = s.unitLoc[u]; kind[u] = adj.oType[u]; a[u] = adj.oA[u]; b[u] = adj.oB[u];
                }
                adj.ResolveMovement(s);
                for (int u = 0; u < n; u++)
                    orders.Add(new { p = pw[u], text = text[u], res = Notation.ResultWord(adj.rStatus[u], adj.rDislodged[u]), ok = adj.rStatus[u] == Adjudicator.R_OK && !adj.rDislodged[u], k = kind[u], u = from[u], a = a[u], b = b[u] });
            }
            else if (s.phase == BoardState.PHASE_RETREAT)
            {
                int n = s.disCount;
                var text = new string[n]; var pw = new int[n]; var from = new int[n]; var to = new int[n];
                for (int d = 0; d < n; d++)
                {
                    string unit = Notation.TypeLetter(s.disType[d]) + " " + map.locId[s.disLoc[d]];
                    text[d] = adj.retDest[d] >= 0 && adj.retDest[d] < map.locCount ? unit + " retreats to " + map.locId[adj.retDest[d]] : unit + " disbands";
                    pw[d] = s.disPower[d]; from[d] = s.disLoc[d]; to[d] = adj.retDest[d];
                }
                adj.ResolveRetreats(s);
                for (int d = 0; d < n; d++)
                    orders.Add(new { p = pw[d], text = text[d], res = to[d] < 0 ? "ok" : adj.retOk[d] ? "ok" : "destroyed", ok = to[d] < 0 || adj.retOk[d], k = to[d] < 0 ? 0 : 1, u = from[d], a = to[d], b = -1 });
            }
            else if (s.phase == BoardState.PHASE_ADJUST)
            {
                int n = adj.adjCount;
                var text = new string[n]; var pw = new int[n];
                for (int i = 0; i < n; i++) { text[i] = AdjustText(map, adj.adjKind[i], adj.adjLoc[i]); pw[i] = adj.adjPower[i]; }
                adj.ResolveAdjustments(s);
                for (int i = 0; i < n; i++) orders.Add(new { p = pw[i], text = text[i], res = adj.adjOk[i] ? "ok" : "refused", ok = adj.adjOk[i], k = 0, u = -1, a = -1, b = -1 });
                for (int i = 0; i < adj.autoDisbandCount; i++)
                    orders.Add(new { p = adj.autoDisbandPower[i], text = "Unit in " + map.locId[adj.autoDisbandLoc[i]] + " removed (no disband was ordered)", res = "ok", ok = true, k = 0, u = -1, a = -1, b = -1 });
            }
        }
        return new { index, label, resolved, units, owners, dislodged, orders, code = h.CodeAt(index) };
    }
}
