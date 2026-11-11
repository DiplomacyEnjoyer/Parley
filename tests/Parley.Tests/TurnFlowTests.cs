// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// The live turn flow: timer schedule, submit toggle, sealed orders (commit and reveal),
/// the announcement and the ready-up.
/// </summary>
public class TurnFlowTests
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

    /// <summary>Three seated players with sealed orders, no announcement and no ready-up unless asked for.</summary>
    private static (Game g, GameFlow f, SealedDevice[] dev) Sealed(int move = 0, int retreat = 0, int adjust = 0)
    {
        var g = Ring(3); var f = g.Flow;
        f.sealedOrders = true;
        f.SetTimers(move, retreat, adjust);
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        var dev = new[] { new SealedDevice(g, 0), new SealedDevice(g, 1), new SealedDevice(g, 2) };
        return (g, f, dev);
    }

    private static void ReactAll(SealedDevice[] dev) { foreach (var d in dev) d.React(); }

    // ---------------------------------------------------------------- timers

    [Fact]
    public void MovementClockFollowsTheSchedule()
    {
        var g = Ring(3); var f = g.Flow;
        f.SetMoveSchedule(new[] { 1800, 1800, 900 }, 3);       // 30 minutes twice, then 15
        g.History.retreatSeconds = 120; g.History.adjustSeconds = 180;
        f.ClaimSeat(0, "Ada");
        f.StartGame(0);
        Assert.Equal("S1M", g.State.PhaseLabel()); Assert.Equal(1800, f.SecondsLeft(0));
        f.SetSubmitted(0, true); Assert.True(f.Tick(10));
        Assert.Equal("F1M", g.State.PhaseLabel()); Assert.Equal(1800, f.SecondsLeft(10));
        f.SetSubmitted(0, true); Assert.True(f.Tick(20));
        Assert.Equal("S2M", g.State.PhaseLabel()); Assert.Equal(900, f.SecondsLeft(20));
        f.SetSubmitted(0, true); Assert.True(f.Tick(30));
        Assert.Equal("F2M", g.State.PhaseLabel()); Assert.Equal(900, f.SecondsLeft(30));

        // the schedule travels with the saved game
        var g2 = Ring(3);
        Assert.True(g2.Flow.LoadArchive(f.Archive(), 0), g2.Flow.error);
        Assert.Equal(3, g2.History.moveScheduleCount);
        Assert.Equal(900, g2.Flow.SecondsLeft(0));
        Assert.Equal(120, g2.History.retreatSeconds);
        Assert.Equal(f.Archive(), g2.Flow.Archive());
    }

    [Fact]
    public void LiveSettingsAreTheOwnersDesign()
    {
        var g = Ring(3); var f = g.Flow;
        f.UseLiveSettings();
        Assert.True(f.sealedOrders);
        Assert.Equal(GameFlow.READY_MOVEMENT, f.readyMode);
        Assert.Equal(1800, g.History.MoveSecondsFor(0));
        Assert.Equal(1800, g.History.MoveSecondsFor(1));
        Assert.Equal(900, g.History.MoveSecondsFor(2));
        Assert.Equal(900, g.History.MoveSecondsFor(57));
        Assert.Equal(120, g.History.retreatSeconds);
        Assert.Equal(180, g.History.adjustSeconds);
        Assert.True(f.announceOrderSeconds > 0);
    }

    [Fact]
    public void AnEmptyRoomStopsTheClockInsteadOfPlayingOn()
    {
        var g = Ring(3); var f = g.Flow;
        f.SetTimers(300, 60, 60);
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");
        f.StartGame(0);
        f.PlayerLeft("Ada"); f.PlayerLeft("Bo");
        Assert.True(f.Tick(300));
        Assert.Equal(GameFlow.STAGE_PAUSED, f.stage);
        Assert.Equal("S1M", g.State.PhaseLabel());
    }

    // ---------------------------------------------------------------- submit toggle (sealed)

    [Fact]
    public void SubmitToggleOnOffAndEarlyFinish()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "F alp - als");
        Assert.True(dev[0].SubmitOn(), f.error);
        Assert.True(f.submitted[0]);
        Assert.False(f.Tick(1));                                 // Bo and Cy are still working
        // changing my mind: toggle off withdraws the seal, edit, toggle on again
        Assert.False(dev[0].SubmitOn());                         // cannot re-seal while submitted
        Assert.True(dev[0].SubmitOff());
        Assert.False(f.submitted[0]); Assert.Equal("", f.sealText[0]);
        dev[0].Sheet = Orders(g, 0, "F alp - als", "A alg - alm");
        Assert.True(dev[0].SubmitOn());
        Assert.True(dev[1].SubmitOn()); Assert.False(f.Tick(2));
        Assert.True(dev[2].SubmitOn());
        // everyone is submitted with 15 minutes still on the clock: the phase ends early
        Assert.True(f.Tick(3));
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);            // all sealed already, so no closing grace is needed
        Assert.Equal(GameFlow.CLOSED_ALL_SUBMITTED, f.closeReason);
        Assert.False(dev[0].SubmitOff());                        // too late to change anything
        ReactAll(dev);
        Assert.True(f.Tick(4));
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(HasUnit(g, "alder", "als")); Assert.True(HasUnit(g, "alder", "alm"));
        Assert.False(f.submitted[0]);                            // new phase: toggles are off again
    }

    [Fact]
    public void NobodysOrdersReachTheFlowBeforeEverySealIsFrozen()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "F alp - als"); dev[0].SubmitOn();
        dev[1].Sheet = Orders(g, 1, "A brg - brm"); dev[1].SubmitOn();
        // the flow (the host's machine, the server) holds seals only
        for (int p = 0; p < 3; p++) Assert.Equal("", f.sheet[p]);
        Assert.False(f.Submit(0, dev[0].Sheet));                 // plain hand-in is refused in a sealed game
        Assert.False(f.Open(0, dev[0].Salt, dev[0].Sheet));      // and nothing can be opened while orders are still being taken
        Assert.Contains("not being opened", f.error);
        Assert.DoesNotContain(dev[0].Sheet, f.Archive());
        dev[2].SubmitOn();
        f.Tick(1);
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        for (int p = 0; p < 3; p++) Assert.Equal("", f.sheet[p]);
        // seals can no longer be added, withdrawn or replaced
        Assert.False(f.SetSeal(0, g.Seal.Make(SealedDevice.NewSalt(), 0, f.SealKey(), "")));
        Assert.False(f.SetSubmitted(0, false));
    }

    [Fact]
    public void OpeningWithDifferentOrdersIsRefused()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        string honest = Orders(g, 0, "A alc - alk"), cheat = Orders(g, 0, "A alc - brk");
        dev[0].Sheet = honest;
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1);
        dev[1].React(); dev[2].React();                          // Ada sees the others' orders, then tries to swap hers
        Assert.False(f.Open(0, dev[0].Salt, cheat));
        Assert.Contains("do not match the seal", f.error);
        Assert.False(f.Open(0, SealedDevice.NewSalt(), cheat));  // a fresh salt does not help
        Assert.False(f.Open(1, dev[0].Salt, honest));            // nor does opening somebody else's seat
        Assert.False(f.Tick(2));                                 // still waiting for Ada
        Assert.True(f.Open(0, dev[0].Salt, honest), f.error);    // the sealed orders are the only ones that open
        Assert.True(f.Tick(3));
        Assert.True(HasUnit(g, "alder", "alk"));
        // afterwards every device can check every sheet against the seal it saw
        for (int p = 0; p < 3; p++)
            Assert.True(g.Seal.Check(f.lastSeal[p], f.lastSalt[p], p, f.lastSealKey, f.lastSheet[p]));
    }

    [Fact]
    public void TimerZeroTurnsWhateverIsOnTheSheetIntoOrders()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.closeGraceSeconds = 5;
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "F alp - als"); dev[0].SubmitOn();   // Ada submitted properly
        dev[1].Sheet = Orders(g, 1, "A brg - brm");                       // Bo entered an order but never flipped submit
        // Cy entered nothing at all
        Assert.False(f.Tick(899));
        Assert.True(f.Tick(900));
        Assert.Equal(GameFlow.STAGE_CLOSING, f.stage);
        Assert.Equal(GameFlow.CLOSED_TIME_UP, f.closeReason);
        Assert.False(dev[0].SubmitOff());                        // the clock has run out: no more editing
        ReactAll(dev);                                           // devices seal what they have (Cy's is an empty sheet)
        Assert.True(f.Tick(900.5f));
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        ReactAll(dev);
        Assert.True(f.Tick(901));
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(HasUnit(g, "alder", "als"));                 // submitted order
        Assert.True(HasUnit(g, "brann", "brm"));                 // unsubmitted sheet became orders automatically
        Assert.True(HasUnit(g, "corvin", "coc"));                // units with nothing entered hold
        Assert.True(HasUnit(g, "brann", "brc"));
        for (int p = 0; p < 3; p++) Assert.Equal(Announcement.S_ORDERS, g.Announce.powerStatus[p]);
    }

    [Fact]
    public void ADeviceThatNeverSealsMissesTheTurnAfterAShortGrace()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.closeGraceSeconds = 5;
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "F alp - als");
        f.Tick(900);
        dev[0].React(); dev[1].React();                          // Cy's device is frozen and does nothing
        Assert.False(f.Tick(904));
        Assert.True(f.Tick(905));                                // grace over: seals are frozen without Cy
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        Assert.False(f.SetSeal(2, g.Seal.Make(SealedDevice.NewSalt(), 2, f.SealKey(), ""))); // too late now
        ReactAll(dev);
        Assert.True(f.Tick(906));
        Assert.Equal(Announcement.S_NONE, g.Announce.powerStatus[2]);
        Assert.True(HasUnit(g, "corvin", "coc"));
    }

    [Fact]
    public void DisconnectBetweenSealAndOpenMeansTheUnitsHoldAndEveryoneIsTold()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.revealSeconds = 20;
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "F alp - als");
        dev[1].Sheet = Orders(g, 1, "A brg - brm");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1);
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        dev[0].React(); dev[2].React();                          // Bo's connection hangs: still "here", but silent
        Assert.False(f.Tick(20));
        Assert.Equal(1, f.SecondsLeft(20));
        Assert.True(f.Tick(21));                                 // the opening window is over
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(HasUnit(g, "alder", "als"));
        Assert.True(HasUnit(g, "brann", "brg"));                 // sealed but unopened orders are not used: the units hold
        Assert.Equal(Announcement.S_UNOPENED, g.Announce.powerStatus[1]);
        Assert.Equal("Sealed orders were never opened", g.Announce.StatusText(g.Announce.powerStatus[1]));
        Assert.Equal("", f.lastSheet[1]);
    }

    [Fact]
    public void APlayerWhoLeftAfterSealingIsNotWaitedFor()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        dev[1].Sheet = Orders(g, 1, "A brg - brm");
        foreach (var d in dev) d.SubmitOn();
        f.PlayerLeft("Bo");                                      // Bo's headset dies after submitting
        f.Tick(1);
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        dev[0].React(); dev[2].React();
        Assert.True(f.Tick(2));                                  // no 20-second wait for somebody who is gone
        Assert.True(HasUnit(g, "brann", "brg"));
        Assert.Equal(Announcement.S_UNOPENED, g.Announce.powerStatus[1]);
    }

    [Fact]
    public void APlayerWhoComesBackInTimeCanStillOpen()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        dev[1].Sheet = Orders(g, 1, "A brg - brm");
        foreach (var d in dev) d.SubmitOn();
        f.PlayerLeft("Bo");
        Assert.True(f.Tick(1));                                  // everyone who is here has submitted: seals are frozen
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        dev[0].React(); dev[2].React();
        f.PlayerJoined("Bo");                                    // Bo's browser reconnects with its sheet still stored...
        Assert.False(f.Tick(2));                                 // ...so the opening now waits for Bo
        dev[1].React();
        Assert.True(f.Tick(3));
        Assert.True(HasUnit(g, "brann", "brm"));
    }

    [Fact]
    public void ALeaversUnitsStandStillAndTheSeatCanBeTakenOver()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        dev[1].Sheet = Orders(g, 1, "A brg - brm"); dev[1].SubmitOn();
        f.PlayerLeft("Bo");
        Assert.True(f.ClaimSeat(1, "Dee"));                      // Dee takes the abandoned seat mid-phase
        Assert.Equal("", f.sealText[1]);                         // Bo's seal is void: Dee could never open it
        Assert.False(f.submitted[1]);
        var dee = new SealedDevice(g, 1) { Sheet = Orders(g, 1, "A brc - brk") };
        dev[0].SubmitOn(); dev[2].SubmitOn();
        Assert.False(f.Tick(1));                                 // now waiting for Dee
        dee.SubmitOn();
        f.Tick(2); dev[0].React(); dev[2].React(); dee.React();
        Assert.True(f.Tick(3));
        Assert.True(HasUnit(g, "brann", "brk"));
        Assert.True(HasUnit(g, "brann", "brg"));
    }

    [Fact]
    public void PauseAndResumeWithSealedOrders()
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        f.Pause(100);
        Assert.Equal(GameFlow.STAGE_PAUSED, f.stage);
        Assert.Equal(800, f.SecondsLeft(5000));
        dev[0].Sheet = Orders(g, 0, "F alp - als");
        Assert.True(dev[0].SubmitOn());                          // the toggle still works while paused
        Assert.True(dev[1].SubmitOn()); Assert.True(dev[2].SubmitOn());
        Assert.False(f.Tick(5000));                              // but nothing ends while paused, not even "everyone submitted"
        Assert.True(dev[1].SubmitOff());
        f.Resume(5000);
        Assert.Equal(800, f.SecondsLeft(5000));
        Assert.False(f.Tick(5799));
        Assert.True(f.Tick(5800));
        Assert.Equal(GameFlow.STAGE_CLOSING, f.stage);
    }

    [Fact]
    public void HostCanMoveAStuckStageOn()
    {
        var (g, f, dev) = Sealed();
        f.revealSeconds = 0;                                     // wait for sheets for as long as it takes
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "F alp - als");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1); dev[0].React();
        Assert.False(f.Tick(99999));                             // Bo and Cy never open
        Assert.Equal(-1, f.SecondsLeft(99999));
        f.ForceNext(100000);
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(HasUnit(g, "alder", "als"));
        f.ForceNext(100001);                                     // "resolve now" during orders goes through closing, so sheets are still collected
        Assert.Equal(GameFlow.STAGE_CLOSING, f.stage);
    }

    [Fact]
    public void RetreatAndBuildPhasesMoveStraightOnWhenNobodyHereIsInvolved()
    {
        var g = Ring(2); var f = g.Flow; var map = g.Map;
        f.SetTimers(0, 120, 180);
        f.ClaimSeat(0, "Ada");                                   // Brann has no player
        g.State.Clear(); g.State.year = 5; g.State.season = BoardState.FALL;
        g.State.AddUnit(map, MapData.ARMY, 0, map.FindLoc("brk"));
        g.State.AddUnit(map, MapData.ARMY, 0, map.FindLoc("brc"));
        g.State.AddUnit(map, MapData.ARMY, 1, map.FindLoc("brl"));
        g.State.owner[map.FindProv("alp")] = 0; g.State.owner[map.FindProv("brp")] = 1; g.State.owner[map.FindProv("alc")] = 0;
        Assert.True(f.LoadCode(g.Codec.EncodeState(g.State), 0), f.error);
        f.PlayerJoined("Ada"); f.Resume(0);
        f.Submit(0, Orders(g, 0, "A brk - brl", "A brc S brk - brl")); f.SetSubmitted(0, true);
        Assert.True(f.Tick(1));
        Assert.Equal("F5R", g.State.PhaseLabel());               // only the unplayed power has a retreat
        Assert.Equal(120, f.SecondsLeft(1));
        Assert.True(f.Tick(2));                                  // no two-minute clock for nobody
        Assert.NotEqual("F5R", g.State.PhaseLabel());
    }

    // ---------------------------------------------------------------- announcement

    [Fact]
    public void AnnouncementListsEveryOrderByCountryWithIntendedMovesAndResults()
    {
        var g = Ring(3); var f = g.Flow; var map = g.Map; var an = g.Announce;
        f.announceLeadSeconds = 3; f.announcePowerSeconds = 1; f.announceOrderSeconds = 2; f.announceResultSeconds = 6;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        f.StartGame(0);
        // Alder and Corvin both go for Alder Narrows with equal strength, so they bounce
        f.Submit(0, Orders(g, 0, "A alc - alk", "F alp - als"));
        f.Submit(2, Orders(g, 2, "A coc - alk"));
        for (int p = 0; p < 3; p++) f.SetSubmitted(p, true);
        Assert.True(f.Tick(100));
        Assert.Equal(GameFlow.STAGE_ANNOUNCE, f.stage);
        Assert.Equal("S1M", an.label);
        Assert.Equal("F1M", g.State.PhaseLabel());               // the referee has already ruled; the board shows it after the reading

        // nine units, three countries, read in country order
        Assert.Equal(9, an.count); Assert.Equal(3, an.groupCount);
        var read = Enumerable.Range(0, an.count).Select(k => an.seq[k]).ToList();
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1, 2, 2, 2 }, read.Select(i => an.power[i]));
        var text = read.Select(an.Text).ToList();
        Assert.Contains("Army Alder Hold moves to Alder Narrows", text);
        Assert.Contains("Fleet Alder Haven moves to Alder Bay", text);
        Assert.Contains("Army Alder Gate holds", text);
        Assert.Contains("Army Corvin Hold moves to Alder Narrows", text);
        Assert.Contains("Army Corvin Gate holds", text);
        Assert.Contains("Army Brann Hold holds", text);

        // intended moves: both armies head for the same place, whatever the result will be
        int alc = read.First(i => an.fromLoc[i] == map.FindLoc("alc")), coc = read.First(i => an.fromLoc[i] == map.FindLoc("coc"));
        int alp = read.First(i => an.fromLoc[i] == map.FindLoc("alp")), alg = read.First(i => an.fromLoc[i] == map.FindLoc("alg"));
        Assert.Equal(map.FindLoc("alk"), an.moveTo[alc]);
        Assert.Equal(map.FindLoc("alk"), an.moveTo[coc]);
        Assert.Equal(-1, an.moveTo[alg]);                        // a holding piece stays where it is
        // results: one against one, both bounce
        Assert.False(an.moved[coc]); Assert.Equal(Announcement.O_BOUNCED, an.outcome[coc]);
        Assert.False(an.moved[alc]); Assert.Equal(Announcement.O_BOUNCED, an.outcome[alc]); Assert.Equal("bounced", an.OutcomeText(alc));
        Assert.True(an.moved[alp]);
        // one fight, at Alder Narrows: two units went for it, no supports, left empty (standoff)
        Assert.Equal(1, an.battleCount);
        Assert.Equal(map.FindProv("alk"), an.battleProv[0]);
        Assert.Equal(2, an.battleMovers[0]); Assert.Equal(0, an.battleSupports[0]);
        Assert.False(an.battleDefended[0]); Assert.Equal(-1, an.battleWinner[0]);

        // the reading: 3 s lead-in, then per country 1 s for its name and 2 s per order, then 6 s of results
        Assert.Equal(3 + 3 * 1 + 9 * 2 + 6, f.SecondsLeft(100));
        f.LocateAnnouncement(101); Assert.Equal(Announcement.AT_LEAD, an.at);
        f.LocateAnnouncement(103.5f); Assert.Equal(Announcement.AT_POWER, an.at); Assert.Equal(0, an.atPower);
        f.LocateAnnouncement(104.5f); Assert.Equal(Announcement.AT_ORDER, an.at); Assert.Equal(read[0], an.atLine);
        f.LocateAnnouncement(108.5f); Assert.Equal(read[2], an.atLine); Assert.Equal(2, an.ReadIndex(an.atLine));
        f.LocateAnnouncement(110.5f); Assert.Equal(Announcement.AT_POWER, an.at); Assert.Equal(1, an.atPower);
        f.LocateAnnouncement(125); Assert.Equal(Announcement.AT_RESULT, an.at);
        Assert.False(f.Tick(129.9f));
        Assert.True(f.Tick(130));
        Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);            // next clock starts only after the announcement
    }

    [Fact]
    public void AnnouncementCoversRetreatsBuildsAndRemovals()
    {
        var g = Ring(2); var f = g.Flow; var map = g.Map; var an = g.Announce;
        f.announceOrderSeconds = 1;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");
        g.State.Clear(); g.State.year = 5; g.State.season = BoardState.FALL;
        g.State.AddUnit(map, MapData.ARMY, 0, map.FindLoc("brk"));
        g.State.AddUnit(map, MapData.ARMY, 0, map.FindLoc("brc"));
        g.State.AddUnit(map, MapData.ARMY, 1, map.FindLoc("brl"));
        g.State.owner[map.FindProv("alp")] = 0; g.State.owner[map.FindProv("brp")] = 1;
        Assert.True(f.LoadCode(g.Codec.EncodeState(g.State), 0), f.error);
        f.PlayerJoined("Ada"); f.PlayerJoined("Bo"); f.Resume(0);
        f.Submit(0, Orders(g, 0, "A brk - brl", "A brc S brk - brl"));
        f.SetSubmitted(0, true); f.SetSubmitted(1, true);
        f.Tick(1);
        Assert.Equal(GameFlow.STAGE_ANNOUNCE, f.stage);
        int brl = Enumerable.Range(0, an.count).First(i => an.fromLoc[i] == map.FindLoc("brl"));
        Assert.True(an.dislodged[brl]); Assert.Equal("dislodged", an.OutcomeText(brl));
        Assert.Equal(1, an.battleCount); Assert.True(an.battleDefended[0]); Assert.Equal(0, an.battleWinner[0]);
        f.ForceNext(2);                                          // host skips the rest of the reading
        Assert.Equal("F5R", g.State.PhaseLabel()); Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);

        f.Submit(1, Orders(g, 1, "A brl - brp")); f.SetSubmitted(1, true);
        f.Tick(3);
        Assert.Equal("F5R", an.label); Assert.Equal(1, an.count);
        Assert.Equal("Army Brann Marsh retreats to Brann Haven", an.Text(0));
        Assert.Equal(map.FindLoc("brp"), an.moveTo[0]); Assert.True(an.moved[0]);
        f.ForceNext(4);

        Assert.Equal("W5A", g.State.PhaseLabel());
        f.Submit(0, Orders(g, 0, "Build F alp")); f.SetSubmitted(0, true);
        f.Tick(5);
        Assert.Equal("W5A", an.label);
        Assert.Equal("Builds a fleet in Alder Haven", an.Text(0));
        Assert.Equal(Announcement.O_OK, an.outcome[0]);
    }

    // ---------------------------------------------------------------- ready-up

    [Fact]
    public void TheClockStartsOnlyWhenEveryoneHereIsReady()
    {
        var g = Ring(3); var f = g.Flow;
        f.readyMode = GameFlow.READY_MOVEMENT;
        f.SetTimers(900, 120, 180);
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        f.StartGame(0);
        Assert.Equal(GameFlow.STAGE_READY, f.stage);             // even the first clock waits for everyone
        Assert.Equal(-1, f.SecondsLeft(0));
        Assert.False(f.Submit(0, ""));                           // no orders during the break
        Assert.True(f.SetReady(0, true)); f.SetReady(1, true);
        Assert.False(f.Tick(500));
        f.SetReady(1, false); f.SetReady(2, true);               // Bo steps away again
        Assert.False(f.Tick(600));
        f.SetReady(1, true);
        Assert.True(f.Tick(700));
        Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);
        Assert.Equal(900, f.SecondsLeft(700));                   // a full clock from the moment the last player was ready
        Assert.False(f.SetReady(0, true));

        for (int p = 0; p < 3; p++) f.SetSubmitted(p, true);
        Assert.True(f.Tick(800));
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.Equal(GameFlow.STAGE_READY, f.stage);             // and again before the next negotiation
        Assert.False(f.ready[0]);
        f.PlayerLeft("Cy");                                      // someone who has left is not waited for
        f.SetReady(0, true); f.SetReady(1, true);
        Assert.True(f.Tick(801));
        Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);
    }

    [Fact]
    public void HostCanStartTheClockWithoutEveryone()
    {
        var g = Ring(3); var f = g.Flow;
        f.readyMode = GameFlow.READY_EVERY;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");
        f.StartGame(0);
        f.SetReady(0, true);
        Assert.False(f.Tick(1));
        f.ForceNext(2);
        Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);
    }

    // ---------------------------------------------------------------- everything together

    [Fact]
    public void AWholeLiveTurnEndToEnd()
    {
        var g = Ring(3); var f = g.Flow;
        f.UseLiveSettings();
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        var dev = new[] { new SealedDevice(g, 0), new SealedDevice(g, 1), new SealedDevice(g, 2) };
        float t = 0;
        f.StartGame(t);
        Assert.Equal(GameFlow.STAGE_READY, f.stage);
        for (int p = 0; p < 3; p++) f.SetReady(p, true);
        Assert.True(f.Tick(t)); Assert.Equal(1800, f.SecondsLeft(t));
        dev[0].Sheet = Orders(g, 0, "A alc - alk"); dev[2].Sheet = Orders(g, 2, "A coc - alk");
        dev[0].SubmitOn();
        t = 1800; Assert.True(f.Tick(t)); Assert.Equal(GameFlow.STAGE_CLOSING, f.stage);
        ReactAll(dev); t += 1; Assert.True(f.Tick(t)); Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        ReactAll(dev); t += 1; Assert.True(f.Tick(t)); Assert.Equal(GameFlow.STAGE_ANNOUNCE, f.stage);
        float reading = f.SecondsLeft(t);
        Assert.Equal(3 + 3 * 1.5f + 9 * 3 + 6, reading);
        t += reading; Assert.True(f.Tick(t));
        Assert.Equal(GameFlow.STAGE_READY, f.stage);
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(HasUnit(g, "alder", "alc")); Assert.True(HasUnit(g, "corvin", "coc")); // they bounced
        // a save taken now loads, with the schedule intact
        var g2 = Ring(3);
        Assert.True(g2.Flow.LoadArchive(f.Archive(), 0), g2.Flow.error);
        Assert.Equal(1800, g2.Flow.SecondsLeft(0));
    }

    [Fact]
    public void SealedBotGamesPlayToTheEndAndEverySheetChecksOut()
    {
        foreach (int players in new[] { 2, 3, 5 })
        {
            var g = Ring(players); var f = g.Flow;
            f.sealedOrders = true; f.readyMode = GameFlow.READY_EVERY;
            var dev = new SealedDevice[players];
            for (int p = 0; p < players; p++) { f.ClaimSeat(p, "bot" + p); dev[p] = new SealedDevice(g, p); }
            var bot = new Bot(g, new Random(11 + players), true);
            float t = 0;
            f.StartGame(t);
            int phases = 0;
            for (int step = 0; step < 4000 && f.stage != GameFlow.STAGE_FINISHED && g.State.year < 30; step++)
            {
                t += 1;
                if (f.stage == GameFlow.STAGE_READY) for (int p = 0; p < players; p++) f.SetReady(p, true);
                else if (f.stage == GameFlow.STAGE_ORDERS)
                    for (int p = 0; p < players; p++)
                        if (f.NeedsOrders(p) && !f.submitted[p]) { dev[p].Sheet = bot.Choose(p); Assert.True(dev[p].SubmitOn(), f.error); }
                int serial = f.phaseSerial;
                ReactAll(dev);
                f.Tick(t);
                if (f.phaseSerial != serial)
                {
                    phases++;
                    for (int p = 0; p < players; p++)
                        if (f.lastSeal[p] != "") Assert.True(g.Seal.Check(f.lastSeal[p], f.lastSalt[p], p, f.lastSealKey, f.lastSheet[p]));
                }
            }
            Assert.True(phases > 20 || f.stage == GameFlow.STAGE_FINISHED, players + " players: only " + phases + " phases were played; stage " + f.stage + " " + g.State.PhaseLabel() + " " + f.error);
        }
    }
}
