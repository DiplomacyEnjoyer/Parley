// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Parley.Tests;

/// <summary>
/// The scenarios of docs/vr-network-scenarios.md, run against the simulated VRChat
/// (<see cref="VrSim"/>). Each test runs a few seeds; set PARLEY_NET_SEEDS to run more
/// (the "done" bar in the spec is 200).
/// </summary>
public class NetScenarioTests
{
    private readonly ITestOutputHelper output;
    public NetScenarioTests(ITestOutputHelper output) { this.output = output; }

    private static readonly MapData Map = VrSim.ClassicMap();
    public static readonly string[] Names = { "Ada", "Bo", "Cy", "Di", "Ed", "Flo", "Gus", "Hal", "Ivy", "Jo" };

    public static IEnumerable<object[]> Seeds(int n)
    {
        string env = Environment.GetEnvironmentVariable("PARLEY_NET_SEEDS");
        if (int.TryParse(env, out int m) && m > 0) n = m;
        for (int i = 1; i <= n; i++) yield return new object[] { i };
    }
    public static IEnumerable<object[]> FewSeeds() => Seeds(3);
    public static IEnumerable<object[]> OneSeed() => Seeds(1);

    /// <summary>Everybody joins, takes a seat, and the first to arrive starts the game.</summary>
    public static VrSim Table(int seed, VrSim.Settings s = null, int players = -1)
    {
        var sim = new VrSim(Map, seed, s);
        if (players < 0) players = Map.powerCount;
        for (int i = 0; i < players; i++) { sim.Join(Names[i], i); sim.RunFor(500 + sim.Rng.Next(1500)); }
        Assert.True(sim.RunUntil(() => sim.Hub != null && sim.Hub.G.Flow.SeatedCount() == players, 30_000), "seats were not taken");
        Assert.True(sim.Hub.Node.HostStart((int)sim.Now), sim.Hub.G.Flow.error);
        return sim;
    }

    private void Done(VrSim sim, bool alarmsAllowed = false)
    {
        sim.Settle();
        sim.CheckSettled(alarmsAllowed);
        output.WriteLine(sim.Report());
        Assert.True(sim.Violations.Count == 0, sim.Report() + "\n" + string.Join("\n", sim.Log.TakeLast(30)));
    }

    private static int Phases(VrSim sim) => sim.Hub.G.History.count;

    private static bool Finished(VrSim sim) => sim.Hub != null && sim.Hub.G.Flow.stage == GameFlow.STAGE_FINISHED;

    // ------------------------------------------------------------------ A. ordinary play

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A1_SevenHonestPlayersPlayAWholeGame(int seed)
    {
        var sim = Table(seed);
        int startYear = sim.Hub.G.State.year;
        sim.RunUntil(() => Finished(sim) || sim.Hub.G.State.year >= startYear + 12, 6 * 3600_000);
        sim.RunFor(25_000);
        Done(sim);
        Assert.True(Phases(sim) > 20, "too few phases: " + Phases(sim) + " stage " + sim.Hub.G.Flow.stage + "\n" + sim.Report());
        sim.CheckSaves();
        Assert.Empty(sim.Violations);
        Assert.Empty(sim.LostSheets);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A2_SlowNetworkUpToThreeSeconds(int seed)
    {
        var s = new VrSim.Settings { MinDelayMs = 100, MaxDelayMs = 3000, AgreeAfterMs = 30_000 };
        var sim = Table(seed, s);
        int startYear = sim.Hub.G.State.year;
        sim.RunUntil(() => Finished(sim) || sim.Hub.G.State.year >= startYear + 4, 4 * 3600_000);
        sim.RunFor(40_000);
        Done(sim);
        Assert.True(Phases(sim) > 12);
        Assert.Empty(sim.LostSheets);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A3_EverybodySubmitsEarlyAndThePhaseEndsEarly(int seed)
    {
        var s = new VrSim.Settings { MoveSeconds = 900, RetreatSeconds = 300, AdjustSeconds = 300, SubmitMaxMs = 10_000 };
        var sim = Table(seed, s);
        long start = sim.Now;
        sim.RunUntil(() => Phases(sim) >= 8, 900_000);
        Assert.True(Phases(sim) >= 8, "phases did not end early");
        Assert.True(sim.Now - start < 900_000);
        sim.RunFor(10_000);
        Done(sim);
        // no device saw its phase frozen before it had pressed submit itself
        foreach (var d in sim.Devices)
            foreach (var kv in d.SubmittedAt)
                if (d.SawFrozenAt.TryGetValue(kv.Key, out long seen)) Assert.True(seen >= kv.Value, d.Name + " saw " + kv.Key + " close before its own submit");
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A4_ClockRunsOutAndUnsubmittedSheetsAreStillUsed(int seed)
    {
        var s = new VrSim.Settings { MoveSeconds = 60, RetreatSeconds = 20, AdjustSeconds = 20, MaxDelayMs = 2000 };
        var sim = Table(seed, s);
        sim.Find("Bo").NeverSubmit = true; sim.Find("Cy").NeverSubmit = true; sim.Find("Flo").NeverSubmit = true;
        sim.RunUntil(() => Phases(sim) >= 10, 3600_000);
        sim.RunFor(10_000);
        Done(sim);
        Assert.True(Phases(sim) >= 10);
        Assert.Empty(sim.LostSheets);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A5_SubmitOnOffEditOnCloseToTheDeadline(int seed)
    {
        var s = new VrSim.Settings { MoveSeconds = 60, RetreatSeconds = 20, AdjustSeconds = 20 };
        var sim = Table(seed, s);
        var ada = sim.Find("Ada");
        foreach (var d in sim.Present) d.NeverSubmit = true; // the others' devices seal at the deadline
        string lastSeal = ""; string lastSheet = ""; string donePid = "";
        ada.Script = (sm, d) =>
        {
            var f = d.G.Flow; int p = d.Node.MyPower();
            if (f.stage != GameFlow.STAGE_ORDERS || f.deadline < 0 || !f.NeedsOrders(p) || d.Node.PhaseId() == donePid) return;
            float left = f.deadline - d.Node.Now((int)sm.Now);
            string pid = d.Node.PhaseId();
            if (left < 8 && left >= 6 && !d.Node.IsSubmitted()) d.Node.SubmitOn();
            else if (left < 5 && left >= 3 && d.Node.IsSubmitted()) { d.Node.SubmitOff(); d.Node.SetSheet(d.Bot.Choose(p)); }
            else if (left < 2 && !d.Node.IsSubmitted()) { d.Node.SubmitOn(); lastSeal = d.Node.mySeal; lastSheet = d.Node.sheetText; donePid = pid; }
        };
        int checkedPhases = 0;
        for (int round = 0; round < 3; round++)
        {
            Assert.True(sim.RunUntil(() => donePid != "" && sim.Hub.G.Flow.stage == GameFlow.STAGE_REVEAL, 600_000), "no reveal");
            int p = ada.Node.MyPower();
            Assert.Equal(lastSeal, sim.Hub.G.Flow.sealText[p]);
            sim.RunUntil(() => sim.Hub.G.Flow.stage != GameFlow.STAGE_REVEAL, 60_000);
            Assert.Equal(lastSheet, sim.Hub.G.Flow.lastSheet[p]);
            // the device still holds the salts of its earlier seals of that phase
            Assert.True(ada.Node.keptSeal.Count(x => x != "") >= 2);
            checkedPhases++; donePid = "";
            sim.RunUntil(() => sim.Hub.G.State.phase == BoardState.PHASE_MOVE && sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS, 600_000);
        }
        Done(sim);
        Assert.Equal(3, checkedPhases);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A6_ReadyUpWaitsForEveryoneAndTheHostCanOverride(int seed)
    {
        var sim = Table(seed);
        var gus = sim.Find("Gus");
        gus.NeverReady = true;
        Assert.True(sim.RunUntil(() => sim.Hub.G.Flow.stage == GameFlow.STAGE_READY, 60_000));
        sim.RunFor(60_000);
        Assert.Equal(GameFlow.STAGE_READY, sim.Hub.G.Flow.stage);
        // everyone else pressed Ready
        for (int p = 0; p < Map.powerCount; p++) if (sim.Hub.G.History.seatName[p] != "Gus") Assert.True(sim.Hub.G.Flow.ready[p]);
        sim.Hub.Node.HostForceNext((int)sim.Now);
        Assert.Equal(GameFlow.STAGE_ORDERS, sim.Hub.G.Flow.stage);
        sim.RunFor(5_000);
        foreach (var d in sim.Present) Assert.Equal(GameFlow.STAGE_ORDERS, d.G.Flow.stage);
        // and a slow (but not absent) player is waited for
        Assert.True(sim.RunUntil(() => sim.Hub.G.Flow.stage == GameFlow.STAGE_READY, 3600_000));
        sim.RunFor(20_000);
        Assert.Equal(GameFlow.STAGE_READY, sim.Hub.G.Flow.stage);
        gus.NeverReady = false;
        Assert.True(sim.RunUntil(() => sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS, 30_000));
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void A7_PauseAndResumeKeepEveryClockTogether(int seed)
    {
        var s = new VrSim.Settings { MoveSeconds = 600, SubmitMinMs = 400_000, SubmitMaxMs = 500_000, MaxDelayMs = 1500 };
        var sim = Table(seed, s);
        Assert.True(sim.RunUntil(() => sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS, 60_000));
        sim.RunFor(30_000);
        sim.Hub.Node.HostPause((int)sim.Now);
        sim.RunFor(45_000);
        foreach (var d in sim.Present) Assert.Equal(GameFlow.STAGE_PAUSED, d.G.Flow.stage);
        sim.Hub.Node.HostResume((int)sim.Now);
        sim.RunFor(10_000);
        float hubLeft = sim.Hub.G.Flow.SecondsLeft(sim.Hub.Node.Now((int)sim.Now));
        Assert.InRange(hubLeft, 600 - 30 - 10 - 2, 600 - 30 - 10 + 2);
        foreach (var d in sim.Present)
        {
            Assert.Equal(GameFlow.STAGE_ORDERS, d.G.Flow.stage);
            Assert.InRange(d.G.Flow.SecondsLeft(d.Node.Now((int)sim.Now)), hubLeft - 1, hubLeft + 1);
        }
        Done(sim);
    }

    // ------------------------------------------------------------------ C. the hub leaving

    private static VrSim.Settings HandoverSettings() => new VrSim.Settings { MoveSeconds = 90, RetreatSeconds = 30, AdjustSeconds = 30, MaxDelayMs = 800 };

    /// <summary>Plays on after the hub left and checks the game went on under the new master.</summary>
    private void AfterHandover(VrSim sim, string oldHub, int phasesMore = 6)
    {
        Assert.True(sim.RunUntil(() => sim.Hub != null && sim.Hub.Node.isHub, 10_000), "nobody took over");
        Assert.NotEqual(oldHub, sim.Hub.Name);
        int c = Phases(sim);
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + phasesMore, 3600_000), "the game did not go on after the handover");
        sim.RunFor(20_000);
        Done(sim);
        Assert.Empty(sim.LostSheets);
        // no phase was resolved twice into the archive
        var labels = VrSim.Bodies(sim.Hub.G.Flow.Archive()).Select(b => b.Split('.')[0]).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C1_HubLeavesWhileTheClockRuns(int seed)
    {
        var s = HandoverSettings(); s.SubmitMinMs = 20_000; s.SubmitMaxMs = 70_000;
        var sim = Table(seed, s);
        Assert.True(sim.RunUntil(() => sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS && sim.Hub.G.State.phase == BoardState.PHASE_MOVE, 600_000));
        sim.RunFor(25_000 + sim.Rng.Next(10_000));
        var old = sim.Hub; var f = old.G.Flow;
        Assert.Equal(GameFlow.STAGE_ORDERS, f.stage);
        float deadline = f.deadline;
        var seals = (string[])f.sealText.Clone();
        sim.Leave(old.Name);
        Assert.True(sim.RunUntil(() => sim.Hub != null && sim.Hub.Node.isHub && sim.Hub.Node.hubEpoch > 0, 5_000), "the new master did not take over within seconds");
        var nf = sim.Hub.G.Flow;
        if (nf.stage == GameFlow.STAGE_ORDERS) Assert.InRange(nf.deadline, deadline - 0.5f, deadline + 0.5f);
        for (int p = 0; p < Map.powerCount; p++)
        {
            var holder = sim.Find(nf.history.seatName[p]);
            if (seals[p] == "" || holder == null || nf.stage != GameFlow.STAGE_ORDERS) continue;
            Assert.True(nf.sealText[p] == seals[p] || nf.sealText[p] == holder.Node.mySeal, "a seal was lost with the hub");
        }
        AfterHandover(sim, old.Name);
    }

    private void LeaveInStage(int seed, int stage, int inFlight = VrSim.IN_FLIGHT_ARRIVE)
    {
        var s = HandoverSettings();
        var sim = Table(seed, s);
        foreach (var d in sim.Present) d.NeverSubmit = true; // so that the clock runs out and the phase closes
        Assert.True(sim.RunUntil(() => sim.Hub.G.Flow.stage == stage && sim.Hub.G.History.count > 1, 3600_000), "stage " + stage + " never came");
        var old = sim.Hub;
        sim.Leave(old.Name, inFlight);
        foreach (var d in sim.Present) d.NeverSubmit = false;
        AfterHandover(sim, old.Name);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C2_HubLeavesDuringClosing(int seed) => LeaveInStage(seed, GameFlow.STAGE_CLOSING);

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C3_HubLeavesDuringTheReveal(int seed) => LeaveInStage(seed, GameFlow.STAGE_REVEAL);

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C5_HubLeavesDuringTheAnnouncement(int seed) => LeaveInStage(seed, GameFlow.STAGE_ANNOUNCE);

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C4_HubLeavesRightAfterResolvingAndNobodyGotTheResult(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        Assert.True(sim.RunUntil(() => sim.Hub.G.History.count >= 3 && sim.Hub.G.Flow.stage == GameFlow.STAGE_REVEAL, 3600_000));
        var old = sim.Hub; int c = old.G.History.count;
        Assert.True(sim.RunUntil(() => old.G.History.count > c, 120_000), "the hub did not resolve");
        string lost = old.G.History.CodeAt(c);
        sim.Leave(old.Name, VrSim.IN_FLIGHT_LOST);
        Assert.True(sim.RunUntil(() => sim.Hub != null && sim.Hub.Node.isHub && sim.Hub.G.History.count > c, 120_000), "the new hub did not resolve the phase again");
        Assert.Equal(lost, sim.Hub.G.History.CodeAt(c));
        AfterHandover(sim, old.Name);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C6_TwoHubsInARowLeaveWithinTenSeconds(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        Assert.True(sim.RunUntil(() => sim.Hub.G.History.count >= 2 + seed % 3, 3600_000));
        var first = sim.Hub.Name;
        sim.Leave(first);
        sim.RunFor(1_000 + sim.Rng.Next(8_000));
        var second = sim.Present[0].Name;
        sim.Leave(second);
        AfterHandover(sim, second);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C7_ASlowNewMasterThenHostHere(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        sim.Find("Bo").ExtraDelayMs = 2500; // the next master is on weak Wi-Fi
        Assert.True(sim.RunUntil(() => sim.Hub.G.History.count >= 3, 3600_000));
        sim.Leave("Ada");
        Assert.True(sim.RunUntil(() => sim.Hub != null && sim.Hub.Name == "Bo" && sim.Hub.Node.isHub, 10_000));
        int c = Phases(sim);
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + 3, 3600_000), "the slow hub did not keep the game going");
        sim.RunFor(sim.Rng.Next(60_000));
        sim.Find("Cy").Node.HostHere();
        Assert.True(sim.RunUntil(() => sim.Hub != null && sim.Hub.Name == "Cy" && sim.Hub.Node.isHub, 10_000), "host here did not move the hub");
        AfterHandover(sim, "Bo");
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void C8_TheOldHubsLastSendArrivesAfterTheHandover(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        Assert.True(sim.RunUntil(() => sim.Hub.G.History.count >= 2 + seed % 4, 3600_000));
        sim.RunFor(sim.Rng.Next(20_000));
        var old = sim.Hub.Name;
        sim.Leave(old, VrSim.IN_FLIGHT_LATE);
        AfterHandover(sim, old);
    }

    // ------------------------------------------------------------------ B. people coming and going

    private static int PowerOf(VrSim sim, string name) => sim.Hub.G.Flow.SeatOf(name);

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void B1_ASpectatorJoinsMidPhase(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        Assert.True(sim.RunUntil(() => Phases(sim) >= 3 && sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS, 3600_000));
        sim.RunFor(sim.Rng.Next(30_000));
        var spec = sim.Join("Hal");
        Assert.True(sim.RunUntil(() => spec.G.Flow.Archive() == sim.Hub.G.Flow.Archive() && spec.G.Flow.stage == sim.Hub.G.Flow.stage, 30_000), "the spectator did not get the game");
        Assert.Equal(sim.Hub.G.Flow.CurrentCode(), spec.G.Flow.CurrentCode());
        Assert.Equal(-1, spec.Node.MyPower());
        for (int k = 0; k < 40; k++)
        {
            sim.Step();
            for (int p = 0; p < Map.powerCount; p++)
                if (!spec.G.Flow.opened[p]) Assert.Equal("", spec.G.Flow.sheet[p]);
        }
        int c = Phases(sim);
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + 3, 3600_000));
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void B2_AHeadsetRestartsBeforeTheFreezeAndWritesTheSheetAgain(int seed)
    {
        var s = HandoverSettings(); s.MoveSeconds = 150; s.SubmitMinMs = 1000; s.SubmitMaxMs = 20_000;
        var sim = Table(seed, s);
        string who = Names[1 + seed % (Map.powerCount - 1)];
        Assert.True(sim.RunUntil(() => Phases(sim) >= 3 && sim.Hub.G.State.phase == BoardState.PHASE_MOVE && sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS
            && sim.Hub.G.Flow.sealText[PowerOf(sim, who)] != "", 3600_000), "no sealed movement phase");
        int p = PowerOf(sim, who); int c = Phases(sim);
        foreach (var d in sim.Present) if (d.Name != who) { d.NeverSubmit = true; d.Node.SubmitOff(); } // the phase runs on its clock
        var back = sim.Restart(who, 5000);
        Assert.True(sim.RunUntil(() => back.Node.MyPower() == p, 30_000), "the seat did not come back by name");
        Assert.True(sim.RunUntil(() => Phases(sim) > c, 600_000));
        Assert.Equal(Announcement.S_ORDERS, sim.Hub.G.Announce.powerStatus[p]);
        Assert.Equal(back.Node.keptSheet.First(x => x != null && sim.Hub.G.Flow.lastSheet[p] == x), sim.Hub.G.Flow.lastSheet[p]);
        foreach (var d in sim.Present) d.NeverSubmit = false;
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + 4, 3600_000));
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void B3_AHeadsetRestartsAfterItsSealWasFrozen(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        string who = Names[1 + seed % (Map.powerCount - 1)];
        Assert.True(sim.RunUntil(() => Phases(sim) >= 3 && sim.Hub.G.Flow.stage == GameFlow.STAGE_REVEAL && sim.Hub.G.Flow.sealText[PowerOf(sim, who)] != ""
            && !sim.Hub.G.Flow.opened[PowerOf(sim, who)], 3600_000), "no reveal with that seal");
        int p = PowerOf(sim, who); int c = Phases(sim); long frozenAt = sim.Now;
        sim.Restart(who, 300);
        Assert.True(sim.RunUntil(() => Phases(sim) > c, 60_000));
        Assert.True(sim.Now - frozenAt <= 1000 * (20 + 3), "the others waited beyond the reveal window");
        Assert.Equal(Announcement.S_UNOPENED, sim.Hub.G.Announce.powerStatus[p]);
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + 4, 3600_000));
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void B4_APlayerLeavesForGoodAndSomebodyElseTakesTheSeat(int seed)
    {
        var s = HandoverSettings(); s.MoveSeconds = 150; s.SubmitMaxMs = 10_000;
        var sim = Table(seed, s);
        string who = Names[1 + seed % (Map.powerCount - 1)];
        Assert.True(sim.RunUntil(() => Phases(sim) >= 3 && sim.Hub.G.State.phase == BoardState.PHASE_MOVE && sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS
            && sim.Hub.G.Flow.sealText[PowerOf(sim, who)] != "", 3600_000), "no sealed movement phase");
        int p = PowerOf(sim, who); int c = Phases(sim);
        string oldSeal = sim.Hub.G.Flow.sealText[p];
        foreach (var d in sim.Present) if (d.Name != who) { d.NeverSubmit = true; d.Node.SubmitOff(); } // the phase runs on its clock
        sim.Leave(who);
        sim.RunFor(5_000);
        var hal = sim.Join("Hal", p);
        Assert.True(sim.RunUntil(() => sim.Hub.G.History.seatName[p] == "Hal", 30_000), "the seat was not taken over");
        Assert.NotEqual(oldSeal, sim.Hub.G.Flow.sealText[p]);
        Assert.True(sim.RunUntil(() => Phases(sim) > c, 600_000));
        Assert.Equal(Announcement.S_ORDERS, sim.Hub.G.Announce.powerStatus[p]);
        Assert.Contains(sim.Hub.G.Flow.lastSeal[p], hal.Node.keptSeal);
        foreach (var d in sim.Present) d.NeverSubmit = false;
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + 4, 3600_000));
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void B5_ThreePlayersDropAndRejoinThroughAWholeGame(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        var flaky = Names.Take(Map.powerCount).OrderBy(_ => sim.Rng.Next()).Take(3).ToList();
        var nextAt = flaky.ToDictionary(n => n, n => sim.Now + 20_000 + sim.Rng.Next(200_000));
        var backAt = new Dictionary<string, long>();
        int startYear = sim.Hub.G.State.year;
        int drops = 0;
        while (!Finished(sim) && sim.Hub.G.State.year < startYear + 8 && sim.Now < 1_000_000 + 8 * 3600_000L)
        {
            sim.Step();
            foreach (var n in flaky)
            {
                if (sim.Find(n) != null && sim.Now >= nextAt[n] && sim.Present.Count > 1)
                {
                    sim.Leave(n); drops++;
                    backAt[n] = sim.Now + 5_000 + sim.Rng.Next(60_000);
                    nextAt[n] = long.MaxValue;
                }
                else if (sim.Find(n) == null && backAt.TryGetValue(n, out long t) && sim.Now >= t)
                {
                    sim.Join(n);
                    nextAt[n] = sim.Now + 30_000 + sim.Rng.Next(300_000);
                }
            }
        }
        foreach (var n in flaky) if (sim.Find(n) == null) sim.Join(n);
        sim.RunFor(30_000);
        Assert.True(drops >= 6, "only " + drops + " drops");
        Assert.True(sim.Hub.G.State.year >= startYear + 8 || Finished(sim), "the game did not get far: " + sim.Hub.G.State.year);
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void B7_ALateJoinerGetsAWholeArchiveWhileAPhaseResolves(int seed)
    {
        var s = HandoverSettings(); s.MaxArchiveChars = 40_000;
        var sim = Table(seed, s);
        Assert.True(sim.RunUntil(() => Phases(sim) >= 10 && sim.Hub.G.Flow.stage == GameFlow.STAGE_REVEAL, 3600_000));
        var late = sim.Join("Hal", -1, 2500);
        int c = Phases(sim);
        Assert.True(sim.RunUntil(() => Phases(sim) > c, 60_000));
        Assert.True(sim.RunUntil(() => late.G.Flow.Archive() == sim.Hub.G.Flow.Archive(), 40_000), "the late joiner did not end with the new archive");
        Done(sim);
    }

    // ------------------------------------------------------------------ D. saves and resuming next week

    /// <summary>Plays some phases, then everybody leaves mid-phase (B6). Returns the archive the game had.</summary>
    private static string PlayThenAllLeave(VrSim sim, int phases)
    {
        Assert.True(sim.RunUntil(() => Phases(sim) >= phases, 3600_000));
        sim.RunUntil(() => sim.Hub.G.Flow.stage == GameFlow.STAGE_ORDERS, 600_000);
        sim.RunFor(10_000 + sim.Rng.Next(20_000));
        sim.Settle();
        string archive = sim.Hub.G.Flow.Archive();
        foreach (var d in sim.Present.ToList()) sim.Leave(d.Name); // within one moment, so no phase resolves meanwhile
        Assert.Empty(sim.Present);
        return archive;
    }

    /// <summary>Next week: a new instance with the same PlayerData; these players come back one by one.</summary>
    private static VrSim NextSession(VrSim old, int seed, IEnumerable<string> who, VrSim.Settings s = null)
    {
        var sim = new VrSim(Map, seed + 1000, s ?? old.S, old.Cloud);
        foreach (var n in who) { sim.Join(n); sim.RunFor(1000 + sim.Rng.Next(10_000)); }
        return sim;
    }

    private static void ResumeOffer(VrSim sim, int offer)
    {
        var hub = sim.Hub;
        Assert.True(hub.Node.HostLoad(hub.Node.offerArchive[offer], (int)sim.Now), hub.G.Flow.error);
        sim.RunFor(10_000);
        hub.Node.HostResume((int)sim.Now);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D1_EverybodyLeavesAndNextSessionEverybodyReturns(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        string archive = PlayThenAllLeave(sim, 6 + seed % 5);
        var next = NextSession(sim, seed, Names.Take(Map.powerCount));
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() == 1 && next.Hub.Node.offerHolders[0] == Map.powerCount, 60_000), "no resume offer held by everybody");
        Assert.Equal(archive.Split(';').Length, next.Hub.Node.offerArchive[0].Split(';').Length);
        Assert.Equal(VrSim.Bodies(archive), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
        ResumeOffer(next, 0);
        Assert.Equal(GameFlow.STAGE_ORDERS, next.Hub.G.Flow.stage);
        Assert.Equal(VrSim.Bodies(archive).Last(), VrSim.Bodies(next.Hub.G.Flow.Archive()).Last());
        for (int p = 0; p < Map.powerCount; p++) Assert.Equal(Names[p], next.Hub.G.History.seatName[p]);
        int c = Phases(next);
        Assert.True(next.RunUntil(() => Phases(next) >= c + 4, 3600_000), "the resumed game did not go on");
        Done(next);
        next.CheckSaves();
        Assert.Empty(next.Violations);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D2_OnlyTwoReturnOneByOne(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        string archive = PlayThenAllLeave(sim, 5);
        var next = NextSession(sim, seed, new[] { Names[3], Names[5] });
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() == 1 && next.Hub.Node.offerHolders[0] == 2, 60_000), "no offer once both saves loaded");
        Assert.Equal(VrSim.Bodies(archive), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
        ResumeOffer(next, 0);
        int c = Phases(next);
        Assert.True(next.RunUntil(() => Phases(next) >= c + 3, 3600_000), "the resumed game did not go on");
        Done(next);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D3_OneMissedTheLastPhaseAndTheNewerSaveWins(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        Assert.True(sim.RunUntil(() => Phases(sim) >= 5, 3600_000));
        sim.S.SaveFailNames.Add("Bo");
        int c = Phases(sim);
        string archive = PlayThenAllLeave(sim, c + 2);
        Assert.NotEqual(VrSim.Bodies(archive).Count, VrSim.Bodies(sim.Cloud["Bo"]).Count);
        sim.S.SaveFailNames.Clear();
        var next = NextSession(sim, seed, Names.Take(Map.powerCount).OrderBy(_ => sim.Rng.Next()));
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() == 1 && next.Hub.Node.offerHolders[0] == Map.powerCount, 60_000));
        Assert.Equal(VrSim.Bodies(archive), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D4_AForgedSaveIsShownButNotPreferred(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        string archive = PlayThenAllLeave(sim, 6);
        // Cy edits an old position in their save and fixes the checksum
        var parts = sim.Cloud["Cy"].Split(';');
        int plus = parts[5].IndexOf('+'); int at = (plus < 0 ? parts[5].Length : plus) / 2;
        var b = parts[5].ToCharArray(); b[at] = b[at] == 'a' ? 'b' : 'a'; parts[5] = new string(b);
        string content = string.Join(";", parts.Take(parts.Length - 1));
        sim.Cloud["Cy"] = content + ";" + sim.Devices[0].G.Codec.Checksum(content);
        var next = NextSession(sim, seed, Names.Take(Map.powerCount));
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() == 2 && next.Hub.Node.offerHolders[0] + next.Hub.Node.offerHolders[1] == Map.powerCount, 60_000), "the forged save was not shown separately");
        Assert.Equal(Map.powerCount - 1, next.Hub.Node.offerHolders[0]);
        Assert.Equal(1, next.Hub.Node.offerHolders[1]);
        Assert.Equal(VrSim.Bodies(archive), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D5_PlayersFromTwoGamesAreInTheRoom(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        string gameX = PlayThenAllLeave(sim, 5);
        // another group plays another game in another week, with the same world
        var other = new VrSim(Map, seed + 500, sim.S, sim.Cloud);
        other.Join("Hal", 0); other.RunFor(1000); other.Join("Ivy", 1); other.RunFor(1000); other.Join("Jo", 2);
        Assert.True(other.RunUntil(() => other.Hub.G.Flow.SeatedCount() == 3, 30_000));
        other.Hub.Node.HostStart((int)other.Now);
        string gameY = PlayThenAllLeave(other, 4);
        string halBefore = sim.Cloud["Hal"];
        var next = NextSession(sim, seed, new[] { "Ada", "Hal", "Bo", "Ivy" });
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() == 2 && next.Hub.Node.offerHolders[0] == 2 && next.Hub.Node.offerHolders[1] == 2, 60_000), "both games were not offered");
        int x = VrSim.Bodies(next.Hub.Node.offerArchive[0]).SequenceEqual(VrSim.Bodies(gameX)) ? 0 : 1;
        Assert.Equal(VrSim.Bodies(gameY), VrSim.Bodies(next.Hub.Node.offerArchive[1 - x]));
        ResumeOffer(next, x);
        int c = Phases(next);
        Assert.True(next.RunUntil(() => Phases(next) >= c + 2, 3600_000));
        Assert.Equal(halBefore, next.Cloud["Hal"]);
        Assert.NotEqual(gameX, next.Cloud["Ada"]);
        Done(next);
    }

    [Theory]
    [MemberData(nameof(OneSeed))]
    public void D6_ALongGameIsTrimmedToTheSaveSize(int seed)
    {
        var s = HandoverSettings(); s.MaxArchiveChars = 4000; s.PlayerDataLimitBytes = 2 * 5000;
        var sim = Table(seed, s);
        int startYear = sim.Hub.G.State.year;
        string archive = PlayThenAllLeave(sim, 30);
        var entries = archive.Split(';').Skip(4).SkipLast(2).ToList();
        Assert.True(entries.Count(e => e.Contains('+')) < entries.Count, "nothing was trimmed");
        Assert.True(archive.Length <= 5000);
        var next = NextSession(sim, seed, Names.Take(Map.powerCount));
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() == 1, 60_000));
        Assert.Equal(VrSim.Bodies(archive), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
        ResumeOffer(next, 0);
        int c = Phases(next);
        Assert.True(next.RunUntil(() => Phases(next) >= c + 3 || next.Hub.G.History.CodeAt(next.Hub.G.History.count - 1) != sim.Devices[0].G.Flow.CurrentCode(), 3600_000));
        Done(next);
        next.CheckSaves();
        Assert.Empty(next.Violations);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D7_ASaveWriteFailsAndTheGameGoesOn(int seed)
    {
        var s = HandoverSettings();
        s.SaveFailNames.Add("Di");
        var sim = Table(seed, s);
        Assert.True(sim.RunUntil(() => Phases(sim) >= 6, 3600_000));
        sim.RunFor(10_000);
        var di = sim.Find("Di");
        Assert.Contains(Enumerable.Range(0, di.Node.noticeCount).Select(i => di.Node.notices[i]), n => n.Contains("could not be stored"));
        Assert.False(sim.Cloud.ContainsKey("Di"));
        foreach (var d in sim.Present) if (d.Name != "Di") Assert.True(sim.Cloud.ContainsKey(d.Name));
        Done(sim);
    }

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void D8_RollBackThenTheSessionEnds(int seed)
    {
        var sim = Table(seed, HandoverSettings());
        Assert.True(sim.RunUntil(() => Phases(sim) >= 8, 3600_000));
        sim.Settle();
        int back = Phases(sim) - 4;
        string discarded = sim.Hub.G.Flow.Archive();
        Assert.True(sim.Hub.Node.HostRollBack(back, (int)sim.Now), sim.Hub.G.Flow.error);
        sim.RunFor(10_000);
        sim.Hub.Node.HostResume((int)sim.Now);
        sim.RunFor(5_000);
        Assert.True(sim.Settle(), "the devices did not follow the roll-back");
        var kept = VrSim.Bodies(discarded).Take(back + 1).ToList();
        foreach (var d in sim.Present) Assert.Equal(kept, VrSim.Bodies(d.G.Flow.Archive()).Take(back + 1).ToList());
        Assert.True(sim.Hub.G.History.count <= back + 3);
        string archive = PlayThenAllLeave(sim, back + 1);
        var next = NextSession(sim, seed, Names.Take(Map.powerCount));
        Assert.True(next.RunUntil(() => next.Hub.Node.ComputeOffers() >= 1 && next.Hub.Node.offerHolders[0] == Map.powerCount, 60_000));
        Assert.Equal(1, next.Hub.Node.offerCount);
        Assert.Equal(VrSim.Bodies(archive), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
        Assert.NotEqual(VrSim.Bodies(discarded), VrSim.Bodies(next.Hub.Node.offerArchive[0]));
        Assert.Empty(sim.AllAlarms());
        Assert.Empty(sim.Violations);
    }

    // ------------------------------------------------------------------ F. limits

    [Theory]
    [MemberData(nameof(FewSeeds))]
    public void F1_F2_BandwidthAndLargestSendInTheWorstCasePhase(int seed)
    {
        // everybody submits at the same moment, so all seals, then all openings, arrive together
        var s = new VrSim.Settings { SubmitMinMs = 3000, SubmitMaxMs = 3000, ThinkMinMs = 1000, ThinkMaxMs = 1000 };
        var sim = Table(seed, s);
        sim.RunUntil(() => Phases(sim) >= 20, 3600_000);
        Done(sim);
        output.WriteLine("F1 peak bytes per second on any device: " + sim.PeakBytesPerSecond + " (budget " + s.BudgetBytesPerSecond + ")");
        output.WriteLine("F2 largest single send: " + sim.LargestSendBytes + " bytes (" + sim.LargestSendObj + "), limit " + s.ObjectLimitBytes);
        Assert.Equal(0, sim.OverBudgetSeconds);
        Assert.True(sim.PeakBytesPerSecond <= s.BudgetBytesPerSecond);
        Assert.True(sim.LargestSendBytes < s.ObjectLimitBytes);
    }

    /// <summary>A long game played by the engine alone (no network), for a full-size archive.</summary>
    private static string LongArchive(int seed, int chars)
    {
        var g = Game.FromMap(Map);
        g.Flow.SetTimers(0, 0, 0);
        g.Flow.maxArchiveChars = 40_000;
        for (int p = 0; p < Map.powerCount; p++) g.Flow.ClaimSeat(p, Names[p]);
        g.Flow.StartGame(0);
        var bot = new Bot(g, new Random(seed), false);
        for (int i = 0; i < 2000 && g.Flow.Archive().Length < chars && g.Flow.stage != GameFlow.STAGE_FINISHED; i++)
        {
            for (int p = 0; p < Map.powerCount; p++) if (g.Flow.NeedsOrders(p)) g.Flow.Submit(p, bot.Choose(p));
            g.Flow.ForceNext(i);
        }
        return g.Flow.Archive();
    }

    [Theory]
    [MemberData(nameof(OneSeed))]
    public void F3_AFullSizeArchiveReachesEveryoneAndALateJoiner(int seed)
    {
        string archive = LongArchive(seed, 36_000);
        Assert.True(archive.Length >= 30_000, "the long game is only " + archive.Length + " characters");
        var sim = Table(seed);
        sim.RunFor(5_000);
        long t0 = sim.Now;
        Assert.True(sim.Hub.Node.HostLoad(archive, (int)sim.Now), sim.Hub.G.Flow.error);
        Assert.True(sim.Settle(120_000), "the archive did not reach everyone");
        double spread = (sim.Now - t0) / 1000.0;
        var late = sim.Join("Hal");
        long t1 = sim.Now;
        Assert.True(sim.RunUntil(() => late.G.Flow.Archive() == sim.Hub.G.Flow.Archive(), 60_000), "the late joiner did not get the archive");
        double join = (sim.Now - t1) / 1000.0;
        output.WriteLine("F3 archive of " + archive.Length + " characters (" + 2 * archive.Length + " bytes): published to everyone in " + spread
            + " s at the hub's budget; a late joiner had it after " + join + " s (VRChat's own late-join delivery; at the ~11 KB/s budget it would take about "
            + Math.Round(2.0 * archive.Length / sim.S.BudgetBytesPerSecond, 1) + " s)");
        sim.Hub.Node.HostResume((int)sim.Now);
        int c = Phases(sim);
        Assert.True(sim.RunUntil(() => Phases(sim) >= c + 2 || Finished(sim), 3600_000));
        Done(sim);
    }
}
