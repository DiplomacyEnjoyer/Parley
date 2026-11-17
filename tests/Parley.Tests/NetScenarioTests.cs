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
}
