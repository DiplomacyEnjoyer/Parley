// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

public class FlowTests
{
    private static Game Ring(int n = 3) => Game.FromPmap(RingMapGenerator.Generate(n));

    private static string Orders(Game g, int power, params string[] lines)
    {
        var pad = new Adjudicator(); pad.Init(g.Map);
        foreach (var l in lines)
        {
            var o = g.Notation.Parse(l);
            bool ok = g.State.phase == BoardState.PHASE_MOVE ? g.Notation.ApplyMovement(g.State, pad, power, o)
                : g.State.phase == BoardState.PHASE_RETREAT ? g.Notation.ApplyRetreat(g.State, pad, power, o)
                : g.Notation.ApplyAdjust(g.State, pad, power, o);
            Assert.True(ok, "could not enter order: " + l);
        }
        return g.Codec.EncodeOrders(g.State, pad, power);
    }

    private static bool HasUnit(Game g, string power, string loc)
    {
        int l = g.Map.FindLoc(loc);
        int u = g.State.provUnit[g.Map.locProv[l]];
        return u >= 0 && g.State.unitLoc[u] == l && g.State.unitPower[u] == g.Map.FindPower(power);
    }

    [Fact]
    public void LobbySeatsAndStart()
    {
        var g = Ring(3); var f = g.Flow;
        Assert.Equal(GameFlow.STAGE_LOBBY, f.stage);
        Assert.True(f.ClaimSeat(0, "Ada"));
        Assert.False(f.ClaimSeat(0, "Bo"));            // taken
        Assert.True(f.ClaimSeat(1, "Bo"));
        Assert.True(f.ClaimSeat(2, "Ada"));            // in the lobby you may change seats...
        Assert.Equal(2, f.SeatOf("Ada"));
        Assert.Equal("", g.History.seatName[0]);       // ...and the old one is freed
        Assert.False(f.Submit(0, ""));                 // no orders before the game starts
        Assert.True(f.StartGame(100));                 // an unclaimed power just holds
        Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);
        Assert.False(f.StartGame(100));
        Assert.Equal("S1M", g.State.PhaseLabel());
        Assert.Equal(1, g.History.count);
    }

    [Fact]
    public void PhaseResolvesWhenEveryoneWhoIsHereHasLockedIn()
    {
        var g = Ring(3); var f = g.Flow;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        f.StartGame(0);
        Assert.True(f.Submit(0, Orders(g, 0, "F alp - als", "A alc - all")));
        f.SetLocked(0, true);
        Assert.False(f.Tick(1));                       // Bo and Cy still thinking
        Assert.False(f.Submit(0, ""));                 // locked means locked
        Assert.Contains("locked", f.error);
        f.SetLocked(0, false);
        Assert.True(f.Submit(0, Orders(g, 0, "F alp - als", "A alc - alr")));  // changed my mind
        f.SetLocked(0, true); f.SetLocked(1, true);
        Assert.False(f.Tick(2));
        f.PlayerLeft("Cy");                            // Cy drops: no longer waited for
        Assert.True(f.Tick(3));
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(HasUnit(g, "alder", "als"));
        Assert.True(HasUnit(g, "alder", "alr"));
        Assert.True(HasUnit(g, "corvin", "coc"));      // absent player's units held
        Assert.False(f.locked[0]);                     // new phase, nobody is locked
        Assert.Equal(2, g.History.count);
        Assert.NotEqual("", g.History.orders[0]);
    }

    [Fact]
    public void TimerRunsOutAndUsesWhateverWasHandedIn()
    {
        var g = Ring(3); var f = g.Flow;
        f.SetTimers(300, 60, 90);
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");
        f.StartGame(1000);
        Assert.Equal(300, f.SecondsLeft(1000));
        f.Submit(0, Orders(g, 0, "A alg - alm"));       // handed in but never locked
        Assert.False(f.Tick(1299));
        Assert.Equal(1, f.SecondsLeft(1299));
        Assert.True(f.Tick(1300));
        Assert.True(HasUnit(g, "alder", "alm"));
        Assert.Equal(300, f.SecondsLeft(1300));         // next phase got a fresh clock
    }

    [Fact]
    public void PauseKeepsTheRemainingTime()
    {
        var g = Ring(3); var f = g.Flow;
        f.SetTimers(300, 60, 90);
        f.ClaimSeat(0, "Ada");
        f.StartGame(0);
        f.Pause(100);
        Assert.Equal(GameFlow.STAGE_PAUSED, f.stage);
        Assert.False(f.Tick(5000));                     // nothing happens while paused
        Assert.Equal(200, f.SecondsLeft(5000));
        Assert.True(f.Submit(0, ""));                   // orders can still be entered while paused
        f.Resume(5000);
        Assert.Equal(200, f.SecondsLeft(5000));
        Assert.False(f.Tick(5199));
        Assert.True(f.Tick(5200));
    }

    [Fact]
    public void LeavingAndComingBackKeepsYourPower()
    {
        var g = Ring(3); var f = g.Flow;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        f.StartGame(0);
        f.PlayerLeft("Bo");
        Assert.False(f.seatPresent[1]);
        Assert.Equal("Bo", g.History.seatName[1]);      // seat is reserved
        Assert.Equal(1, f.PlayerJoined("Bo"));          // same name gets it back
        Assert.True(f.seatPresent[1]);
        Assert.Equal(-1, f.PlayerJoined("Stranger"));
        f.PlayerLeft("Bo");
        Assert.True(f.ClaimSeat(1, "Dee"));             // a replacement may take over an abandoned seat
        Assert.Equal(1, f.SeatOf("Dee"));
        Assert.False(f.ClaimSeat(2, "Dee"));            // but not two powers mid-game
    }

    [Fact]
    public void ResumeFromArchiveInAFreshInstance()
    {
        var g = Ring(3); var f = g.Flow;
        f.SetTimers(300, 60, 90);
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        f.StartGame(0);
        f.Submit(0, Orders(g, 0, "F alp - als")); f.SetLocked(0, true); f.SetLocked(1, true); f.SetLocked(2, true);
        Assert.True(f.Tick(1));
        string archive = f.Archive();

        // the instance dies; somebody else pastes the archive into a new one
        var g2 = Ring(3); var f2 = g2.Flow;
        Assert.True(f2.LoadArchive(archive, 777), f2.error);
        Assert.Equal(GameFlow.STAGE_PAUSED, f2.stage);  // comes back paused
        Assert.Equal("F1M", g2.State.PhaseLabel());
        Assert.False(f2.seatPresent[0]);
        Assert.Equal(0, f2.PlayerJoined("Ada"));        // people get their powers back by name
        Assert.Equal(2, f2.PlayerJoined("Cy"));
        Assert.Equal(300, f2.SecondsLeft(777));         // full clock for the interrupted phase
        f2.Resume(800);
        f2.SetLocked(0, true); f2.SetLocked(2, true);
        Assert.True(f2.Tick(801));                      // Bo has not come back and is not waited for
        Assert.Equal(3, g2.History.count);
    }

    [Fact]
    public void ResumeFromASingleCodeAndRollBack()
    {
        var g = Ring(3); var f = g.Flow;
        for (int p = 0; p < 3; p++) f.ClaimSeat(p, "bot" + p);
        f.StartGame(0);
        var bot = new Bot(g, new Random(3), true);
        var codes = new List<string> { f.CurrentCode() };
        for (int i = 0; i < 8; i++)
        {
            for (int p = 0; p < 3; p++) if (f.NeedsOrders(p)) { f.Submit(p, bot.Choose(p)); f.SetLocked(p, true); }
            Assert.True(f.Tick(i + 1));
            codes.Add(f.CurrentCode());
        }
        Assert.Equal(codes[^1], g.History.LatestCode());

        // roll back three phases
        Assert.True(f.RollBack(5, 50), f.error);
        Assert.Equal(codes[5], f.CurrentCode());
        Assert.Equal(6, g.History.count);
        Assert.Equal(GameFlow.STAGE_PAUSED, f.stage);
        Assert.False(f.RollBack(99, 50));

        // any single code starts a game elsewhere, seats to be claimed again
        var g2 = Ring(3);
        Assert.True(g2.Flow.LoadCode(codes[3], 0), g2.Flow.error);
        Assert.Equal(codes[3], g2.Flow.CurrentCode());
        Assert.Equal(1, g2.History.count);
        Assert.False(g2.Flow.LoadCode("nonsense", 0));
        Assert.Equal(codes[3], g2.Flow.CurrentCode());  // a failed load changes nothing
    }

    [Fact]
    public void DrawNeedsEverySurvivingSeatedPlayer()
    {
        var g = Ring(3); var f = g.Flow;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");    // power 2 has no player
        f.StartGame(0);
        Assert.False(f.VoteDraw(0, true));
        Assert.False(f.VoteDraw(0, false));
        Assert.False(f.VoteDraw(1, true));
        Assert.True(f.VoteDraw(0, true));
        Assert.Equal(GameFlow.STAGE_FINISHED, f.stage);
        Assert.True(g.State.drawn);
        Assert.False(f.Tick(10));
        // a finished game still saves and loads
        var g2 = Ring(3);
        Assert.True(g2.Flow.LoadArchive(f.Archive(), 0), g2.Flow.error);
        Assert.Equal(GameFlow.STAGE_FINISHED, g2.Flow.stage);
        Assert.True(g2.State.drawn);
    }

    [Fact]
    public void RetreatAndBuildPhasesOnlyWaitForThePowersInvolved()
    {
        var g = Ring(2); var f = g.Flow; var map = g.Map;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");
        // start from a hand-made position: Alder dislodges Brann's army in Brann Marsh
        g.State.Clear(); g.State.year = 5; g.State.season = BoardState.FALL;
        g.State.AddUnit(map, MapData.ARMY, 0, map.FindLoc("brk"));
        g.State.AddUnit(map, MapData.ARMY, 0, map.FindLoc("brc"));
        g.State.AddUnit(map, MapData.ARMY, 1, map.FindLoc("brl"));
        g.State.owner[map.FindProv("alp")] = 0; g.State.owner[map.FindProv("brp")] = 1;
        Assert.True(f.LoadCode(g.Codec.EncodeState(g.State), 0), f.error);
        f.PlayerJoined("Ada"); f.PlayerJoined("Bo"); f.Resume(0);

        f.Submit(0, Orders(g, 0, "A brk - brl", "A brc S brk - brl"));
        f.SetLocked(0, true); f.SetLocked(1, true);
        Assert.True(f.Tick(1));
        Assert.Equal("F5R", g.State.PhaseLabel());
        Assert.False(f.NeedsOrders(0));                 // only Brann has a unit to retreat
        Assert.True(f.NeedsOrders(1));
        Assert.False(f.Tick(2));
        f.Submit(1, Orders(g, 1, "A brl - brp"));
        f.SetLocked(1, true);
        Assert.True(f.Tick(3));
        Assert.True(HasUnit(g, "brann", "brp"));
        // Alder now stands on Brann Marsh and Brann Hold (both centres) and owns Alder Haven:
        // 3 centres, 2 units -> one build. Brann has 1 centre and 1 unit -> nothing to do.
        Assert.Equal("W5A", g.State.PhaseLabel());
        Assert.True(f.NeedsOrders(0));
        Assert.False(f.NeedsOrders(1));
        f.Submit(0, Orders(g, 0, "Build F alp"));
        f.SetLocked(0, true);
        Assert.True(f.Tick(4));                         // Brann is not waited for
        Assert.True(HasUnit(g, "alder", "alp"));
        Assert.Equal("S6M", g.State.PhaseLabel());
    }
}
