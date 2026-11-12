// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// A scratch game of Purse and Dagger on the Three Valleys test board, with short
/// commands for setting up a situation, writing each power's sheet and resolving it.
/// Places are named by province id; a garrison's place is the id with "_c" ("rcap_c").
/// </summary>
public sealed class PurseBoard
{
    public static string Valleys => File.ReadAllText(Path.Combine(Game.RepoRoot(), "tests", "purse", "valleys.pmap"));

    public readonly Game g; public readonly BoardState s; public readonly PurseRules pr; public readonly MapData m;
    private Adjudicator[] pad; private List<string>[] money;

    /// <summary>A game standing at the start of a campaign (season 0 = spring). options: PurseRules.OPT_* or -1 for the board's own.</summary>
    public PurseBoard(int options = -1, int season = 0, int year = 1400)
    {
        g = Game.FromPmap(Valleys); s = g.State; pr = g.Purse; m = g.Map;
        if (options >= 0) pr.SetOptions(s, options);
        Campaign(season, year);
    }

    public void Campaign(int season, int year) { s.phase = BoardState.PHASE_MOVE; s.season = season; s.year = year; Fresh(); }

    private void Fresh()
    {
        pad = new Adjudicator[m.powerCount]; money = new List<string>[m.powerCount];
        for (int p = 0; p < m.powerCount; p++) { pad[p] = new Adjudicator(); pad[p].Init(m); money[p] = new List<string>(); }
    }

    public int P(string power) { int p = m.FindPower(power); Assert.True(p >= 0, "no power " + power); return p; }
    public int Loc(string id) { int l = m.FindLoc(id); Assert.True(l >= 0, "no place " + id); return l; }
    public int Prov(string id) => m.locProv[Loc(id)];
    /// <summary>Index of the unit at a place, or -1.</summary>
    public int U(string id) => s.provUnit[Prov(id)];
    public bool Has(string power, char type, string id)
    {
        int u = U(id);
        return u >= 0 && s.unitPower[u] == P(power) && "AFG"[s.unitType[u]] == type;
    }
    public int Money(string power) => pr.Treasury(s, P(power));
    public void SetMoney(string power, int ducats) => pr.SetTreasury(s, P(power), ducats);
    public int Owner(string id) => s.owner[Prov(id)];
    public bool Flag(string id, int flag) => (s.provFlag[Prov(id)] & flag) != 0;
    public string Label => s.PhaseLabel();

    /// <summary>Puts a unit on the board (replacing whatever stood there) and updates who controls what.</summary>
    public PurseBoard Put(string power, char type, string id, int unitClass = 0)
    {
        Remove(id);
        int u = s.AddUnit(m, "AFG".IndexOf(type), P(power), Loc(id));
        Assert.True(u >= 0, $"cannot put {type} at {id}");
        s.unitFlag[u] = unitClass;
        pr.UpdateControl(s);
        return this;
    }
    public PurseBoard Remove(string id) { int u = U(id); if (u >= 0) s.RemoveUnit(m, u); return this; }
    public PurseBoard ClearUnits() { while (s.unitCount > 0) s.RemoveUnit(m, 0); return this; }

    // ---- unit orders: written by the unit's owner unless a power is named (for a unit being bought) ----
    private Adjudicator Pad(string id, string by) { int u = U(id); Assert.True(u >= 0, "no unit at " + id); return pad[by == null ? s.unitPower[u] : P(by)]; }
    public PurseBoard Move(string from, string to, string by = null, bool convoy = false) { Pad(from, by).SetMove(U(from), Loc(to), convoy); return this; }
    public PurseBoard Hold(string at) { Pad(at, null).SetHold(U(at)); return this; }
    public PurseBoard Support(string from, string unit, string to = null)
    {
        if (to == null) Pad(from, null).SetSupportHold(U(from), Prov(unit)); else Pad(from, null).SetSupportMove(U(from), Prov(unit), Loc(to));
        return this;
    }
    public PurseBoard Convoy(string fleet, string army, string to) { Pad(fleet, null).SetConvoy(U(fleet), Prov(army), Prov(to)); return this; }
    public PurseBoard Convert(string from, string to, char becomes) { Pad(from, null).SetConvert(U(from), Loc(to), "AFG".IndexOf(becomes)); return this; }
    public PurseBoard Besiege(string at) { Pad(at, null).SetBesiege(U(at)); return this; }
    public PurseBoard Lift(string at) { Pad(at, null).SetLiftSiege(U(at)); return this; }
    public PurseBoard Retreat(string from, string to)
    {
        for (int d = 0; d < s.disCount; d++) if (s.disLoc[d] == Loc(from)) { pad[s.disPower[d]].SetRetreat(d, Loc(to)); return this; }
        Assert.Fail("no dislodged unit at " + from); return this;
    }

    // ---- money orders ----
    public PurseBoard Spend(string power, int kind, string target, int ducats) { money[P(power)].Add(pr.ExpenseItem(kind, Loc(target), ducats)); return this; }
    public PurseBoard Kill(string power, string victim, int ducats) { money[P(power)].Add(pr.ExpenseItem(PurseRules.EX_ASSASSINATE, P(victim), ducats)); return this; }
    public PurseBoard PayBank(string power, int ducats) { money[P(power)].Add("P" + ducats); return this; }
    public PurseBoard Give(string power, string to, int ducats) { money[P(power)].Add("G" + pr.PowerChar(P(to)) + ducats); return this; }
    public PurseBoard Borrow(string power, int years, int ducats) { money[P(power)].Add("B" + years + ducats); return this; }
    public PurseBoard Token(string power, string against, string to) { money[P(power)].Add("T" + pr.PowerChar(P(against)) + pr.PowerChar(P(to))); return this; }
    public PurseBoard Build(string power, char type, string id, int unitClass = 0) { money[P(power)].Add("N" + "AFG".IndexOf(type) + unitClass + pr.PlaceChars(Loc(id))); return this; }
    public PurseBoard Dismiss(string power, string id) { money[P(power)].Add("D" + pr.PlaceChars(Loc(id))); return this; }

    public string SheetOf(string power)
    {
        int p = P(power);
        string units = g.Codec.EncodeOrders(s, pad[p], -1);
        return money[p].Count == 0 ? units : units + "!" + string.Join(",", money[p]);
    }

    /// <summary>Hands in every sheet and resolves the phase. Returns everything the announcement said in words.</summary>
    public string Go(string seed = "")
    {
        g.Rules.ClearOrders();
        for (int p = 0; p < m.powerCount; p++)
        {
            string sheet = SheetOf(m.powerId[p]);
            Assert.True(g.Rules.AddOrders(sheet, s, p), m.powerId[p] + ": " + g.Rules.error + " in " + sheet);
        }
        g.Rules.Resolve(s, seed == "" ? g.Seal.Sha256Hex("no dice expected") : seed, g.Announce, null);
        Fresh();
        string problem = PurseSim.Check(g);
        Assert.True(problem == null, problem);
        return Notes();
    }

    public string Notes()
    {
        var a = g.Announce; var lines = new List<string>();
        for (int i = 0; i < a.count; i++) if (a.kind[i] == Announcement.K_NOTE) lines.Add(a.note[i]);
        return string.Join("\n", lines);
    }

    /// <summary>A dice seed under which the named rolls come out as the test needs them.</summary>
    public string SeedWhere(Func<Func<string, int>, bool> wanted)
    {
        for (int i = 0; i < 200000; i++)
        {
            string seed = g.Seal.Sha256Hex("try" + i);
            g.Dice.Begin(seed);
            if (wanted(label => g.Dice.Roll(6, label))) return seed;
        }
        Assert.Fail("no seed found"); return "";
    }
}
