// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// A simulated VRChat instance for testing the world's networking (<see cref="NetNode"/>),
/// as specified in docs/vr-network-scenarios.md. Several headsets, each running the real
/// node and its own engine; owner-only writes; delayed delivery that never reorders one
/// sender's writes to one object and may skip intermediate values (latest value wins);
/// ownership passing to the instance master when an owner leaves, with old sends still
/// in flight; late joiners receiving the latest value of every object; PlayerData that
/// survives sessions; a spy over all traffic; size and bandwidth counters. Everything
/// random comes from one seed.
/// </summary>
public sealed class VrSim
{
    public sealed class Settings
    {
        public int StepMs = 250;
        public int MinDelayMs = 30, MaxDelayMs = 400;
        public double CoalesceChance = 0.5;
        public int StaleWindowMs = 2000;
        public int ObjectLimitBytes = 280_000;     // per send (remembered value; to be confirmed)
        // per device: VRChat documents about 11 KB/s, players report throttling at 4-6 KB/s,
        // so the harness holds the world to 4 KB/s, averaged over 5 seconds
        public int BudgetBytesPerSecond = 4_000;
        public double LateJoinDropChance = 0.3;    // a late joiner misses an object's current value (open VRChat bug)
        public double OwnerEventChance = 0.7;      // OnOwnershipTransferred sometimes does not fire
        public int PlayerDataLimitBytes = 100_000;
        public int PlayerDataMinDelayMs = 500, PlayerDataMaxDelayMs = 4000;
        public double SaveFailChance = 0;
        public HashSet<string> SaveFailNames = new();   // these players' saves always fail
        public int AgreeAfterMs = 20_000;          // invariant 1: how long a device may lag the hub
        // world constants every device's engine uses
        public int MoveSeconds = 120, RetreatSeconds = 30, AdjustSeconds = 30;
        public int ReadyMode = GameFlow.READY_MOVEMENT;
        public int MaxArchiveChars = 40_000;
        // what the simulated players do
        public int ThinkMinMs = 1000, ThinkMaxMs = 8000;
        public int SubmitMinMs = 1000, SubmitMaxMs = 15000;
        public int ReadyMinMs = 500, ReadyMaxMs = 5000;
    }

    public sealed class Device
    {
        public string Name;
        public Game G;
        public NetNode Node;
        public Bot Bot;
        public bool Present;
        public bool Honest = true;
        public int ExtraDelayMs;
        public long JoinedAt;
        public int Incarnation;
        // simulated player
        public bool NeverSubmit, NeverReady, Passive;
        public string SeenPid = "";
        public long ChooseAt = -1, SubmitAt = -1, ReadyAt = -1;
        public bool Chosen;
        public Dictionary<string, long> SubmittedAt = new();
        public Dictionary<string, long> SawFrozenAt = new();
        public Action<VrSim, Device> Script;
        /// <summary>A modified client: rewrites what this device sends (object id, text) -> text, or null to send nothing.</summary>
        public Func<string, string, string> OutFilter;
        // counters
        public long BytesThisSecond; public long Second = -1; public long PeakBytesPerSecond;
        public long[] Ring = new long[5]; public long PeakAverage;
        public Dictionary<string, string> LocalOwner = new();
        public long MismatchSince = -1;
        public bool ReportedLag;
    }

    private sealed class Ev
    {
        public long Time; public long Seq;
        public int Kind; // 0 deliver, 1 owner, 2 join, 3 leave, 4 playerdata, 5 save result, 6 transfer acknowledged
        public Device From;
        public string Obj, Text, Name;
        public bool Ok, Cancelled;
        public Device To; public int Inc;
        public (string, string, Device) Key; public bool HasKey;
    }

    public readonly Settings S;
    public readonly int Seed;
    public readonly Random Rng;
    public readonly MapData Map;
    public long Now = 1_000_000;
    public readonly List<Device> Devices = new();       // everyone who ever joined (all incarnations)
    public readonly List<Device> Present = new();       // in join order; [0] is the instance master
    public readonly Dictionary<string, string> Cloud;   // PlayerData: player name -> stored text
    public readonly List<string> Violations = new();
    public readonly List<string> Log = new();
    public long LargestSendBytes; public string LargestSendObj = "";
    public long LargestSaveBytes;
    public long PeakBytesPerSecond;
    /// <summary>Highest 5-second average of bytes sent per second by any device.</summary>
    public long PeakAverage;
    public int OverBudgetSeconds;
    public int DroppedNonOwnerWrites, RefusedOversize, FailedSaves;
    public int Resolutions;
    /// <summary>Sheets of present players that were not used: "phase power status" (status: Announcement.S_NONE or S_UNOPENED).</summary>
    public readonly List<string> LostSheets = new();

    private readonly PriorityQueue<Ev, (long, long)> queue = new();
    private long seq;
    private readonly Dictionary<string, string> ownerOf = new();
    private readonly Dictionary<string, string> staleOwner = new();
    private readonly Dictionary<string, long> staleUntil = new();
    private readonly Dictionary<string, (string text, string from)> latest = new();
    private readonly Dictionary<(string, string, Device), Ev> pending = new();
    private readonly Dictionary<(string, string, Device), long> lastSched = new();
    // spy: salts not yet allowed to be seen, by phase id
    private readonly Dictionary<string, string> secretSalt = new(); // salt -> phase id
    private readonly HashSet<string> frozen = new();
    private readonly Game reference;
    private long lastProgress; private string lastProgressSig = "";

    public VrSim(MapData map, int seed, Settings settings = null, Dictionary<string, string> cloud = null)
    {
        Map = map; Seed = seed; Rng = new Random(seed); S = settings ?? new Settings();
        Cloud = cloud ?? new Dictionary<string, string>();
        reference = Game.FromMap(map);
        reference.Flow.SetTimers(0, 0, 0);
        for (int p = 0; p < map.powerCount; p++) reference.Flow.ClaimSeat(p, "ref" + p);
        lastProgress = Now;
    }

    public static MapData ClassicMap()
    {
        string file = Path.Combine(Game.RepoRoot(), "maps-local", "standard.pmap");
        if (!File.Exists(file)) file = Path.Combine(Game.RepoRoot(), "maps", "ring7.pmap");
        var m = new MapData();
        if (!m.Load(File.ReadAllText(file))) throw new InvalidDataException(m.error);
        return m;
    }

    private int R(int lo, int hi) => hi <= lo ? lo : lo + Rng.Next(hi - lo + 1);

    // joins, leaves and ownership changes reach each device in the order they happened
    private readonly Dictionary<Device, long> lastControl = new();

    private void Push(Ev e)
    {
        if (e.Kind >= 1 && e.Kind <= 3)
        {
            if (lastControl.TryGetValue(e.To, out long last) && e.Time < last) e.Time = last;
            lastControl[e.To] = e.Time;
        }
        e.Seq = seq++; queue.Enqueue(e, (e.Time, e.Seq));
    }

    private int Delay(Device to) => R(S.MinDelayMs, S.MaxDelayMs) + to.ExtraDelayMs;

    public Device Hub => Present.FirstOrDefault(d => ownerOf.TryGetValue(NetNode.HUB, out var o) && o == d.Name);
    public Device Find(string name) => Present.FirstOrDefault(d => d.Name == name);
    public string OwnerOf(string obj) => ownerOf.TryGetValue(obj, out var o) ? o : "";

    // =====================================================================
    // People coming and going
    // =====================================================================

    public Device Join(string name, int seat = -1, int extraDelayMs = 0)
    {
        if (Find(name) != null) throw new InvalidOperationException(name + " is already here");
        var g = Game.FromMap(Map);
        Configure(g.Flow);
        var d = new Device
        {
            Name = name, G = g, Present = true, JoinedAt = Now, ExtraDelayMs = extraDelayMs,
            Incarnation = Devices.Count(x => x.Name == name) + 1,
        };
        d.Bot = new Bot(g, new Random(Rng.Next()), true);
        d.Node = new NetNode();
        d.Node.Init(g.Flow, name);
        d.Node.Stir(name + "|" + Rng.Next() + "|" + Rng.Next() + "|" + Now);
        Devices.Add(d);
        bool first = Present.Count == 0;
        Present.Add(d);
        if (first)
        {
            latest.Clear(); pending.Clear(); lastSched.Clear(); ownerOf.Clear();
            foreach (var o in WorldObjects()) ownerOf[o] = name;
        }
        string slot = SlotId(name);
        ownerOf[slot] = name;

        // to the newcomer: everybody present, every owner, the latest value of every object, then live traffic
        long t = Now + Delay(d);
        foreach (var other in Present) Push(new Ev { Time = t, Kind = 2, Name = other.Name, To = d, Inc = d.Incarnation });
        foreach (var kv in ownerOf) Push(new Ev { Time = t, Kind = 1, Obj = kv.Key, Name = kv.Value, To = d, Inc = d.Incarnation });
        foreach (var kv in latest)
        {
            if (!first && Rng.NextDouble() < S.LateJoinDropChance) continue;
            var key = (kv.Key, kv.Value.from, d);
            var e = new Ev { Time = t, Kind = 0, Obj = kv.Key, Text = kv.Value.text, Name = kv.Value.from, To = d, Inc = d.Incarnation, Key = key, HasKey = true };
            lastSched[key] = t;
            Push(e);
        }
        foreach (var other in Present)
            Push(new Ev { Time = Now + R(S.PlayerDataMinDelayMs, S.PlayerDataMaxDelayMs), Kind = 4, Name = other.Name, Text = Cloud.GetValueOrDefault(other.Name, ""), To = d, Inc = d.Incarnation });
        // to everybody else
        foreach (var other in Present)
        {
            if (other == d) continue;
            long t2 = Now + Delay(other);
            Push(new Ev { Time = t2, Kind = 2, Name = name, To = other, Inc = other.Incarnation });
            Push(new Ev { Time = t2, Kind = 1, Obj = slot, Name = name, To = other, Inc = other.Incarnation });
            Push(new Ev { Time = Now + R(S.PlayerDataMinDelayMs, S.PlayerDataMaxDelayMs), Kind = 4, Name = name, Text = Cloud.GetValueOrDefault(name, ""), To = other, Inc = other.Incarnation });
        }
        if (seat >= 0) d.Node.RequestSeat(seat);
        Note(name + " joins" + (first ? " (master)" : ""));
        return d;
    }

    /// <summary>What happens to a leaving hub's last sends of the hub objects that are still on their way.</summary>
    public const int IN_FLIGHT_ARRIVE = 0, IN_FLIGHT_LOST = 1, IN_FLIGHT_LATE = 2;

    public void Leave(string name, int inFlight = IN_FLIGHT_ARRIVE, int lateMs = 4000)
    {
        var d = Find(name);
        if (d == null) return;
        if (inFlight != IN_FLIGHT_ARRIVE)
        {
            var world = WorldObjects().ToHashSet();
            foreach (var (e, _) in queue.UnorderedItems.ToList())
            {
                if (e.Cancelled || e.Kind != 0 || e.Name != name || !world.Contains(e.Obj)) continue;
                e.Cancelled = true;
                if (e.HasKey && pending.TryGetValue(e.Key, out var pe) && pe == e) pending.Remove(e.Key);
                if (inFlight == IN_FLIGHT_LATE)
                    Push(new Ev { Time = e.Time + lateMs, Kind = 0, Obj = e.Obj, Text = e.Text, Name = e.Name, To = e.To, Inc = e.Inc });
            }
        }
        d.Present = false;
        Present.Remove(d);
        latest.Remove(SlotId(name));
        ownerOf.Remove(SlotId(name));
        Note(name + " leaves");
        if (Present.Count == 0) { latest.Clear(); ownerOf.Clear(); return; }
        string master = Present[0].Name;
        foreach (var obj in ownerOf.Keys.ToList())
        {
            if (ownerOf[obj] != name) continue;
            ownerOf[obj] = master;
            foreach (var other in Present) Push(new Ev { Time = Now + Delay(other), Kind = 1, Obj = obj, Name = master, To = other, Inc = other.Incarnation });
        }
        foreach (var other in Present) Push(new Ev { Time = Now + Delay(other), Kind = 3, Name = name, To = other, Inc = other.Incarnation });
    }

    /// <summary>A headset restarts: leaves, and the same player joins again with a fresh device.</summary>
    public Device Restart(string name, int awayMs = 3000)
    {
        var old = Find(name);
        Leave(name);
        RunFor(awayMs);
        var d = Join(name, -1, old?.ExtraDelayMs ?? 0);
        if (old != null) { d.NeverSubmit = old.NeverSubmit; d.NeverReady = old.NeverReady; d.Passive = old.Passive; }
        return d;
    }

    /// <summary>A modified client takes an object it should not own (if VRChat allowed it).</summary>
    public void ForceOwner(string obj, Device to) => Transfer(obj, to);

    private void Transfer(string obj, Device to)
    {
        string old = OwnerOf(obj);
        if (old == to.Name) return;
        ownerOf[obj] = to.Name;
        staleOwner[obj] = old; staleUntil[obj] = Now + S.StaleWindowMs;
        foreach (var other in Present)
            Push(new Ev { Time = other == to ? Now : Now + Delay(other), Kind = 1, Obj = obj, Name = to.Name, To = other, Inc = other.Incarnation });
    }

    private IEnumerable<string> WorldObjects()
    {
        yield return NetNode.HUB;
        yield return NetNode.RECORDS;
        for (int i = 0; i < NetNode.MAX_PAGES; i++) yield return NetNode.PAGE + i;
    }

    public string SlotId(string name) => NetNode.SLOT + reference.History.EscapeName(name);

    private void Configure(GameFlow f)
    {
        f.UseLiveSettings();
        f.SetTimers(S.MoveSeconds, S.RetreatSeconds, S.AdjustSeconds);
        f.readyMode = S.ReadyMode;
        f.announceLeadSeconds = 1; f.announcePowerSeconds = 0.2f; f.announceOrderSeconds = 0.3f; f.announceResultSeconds = 2;
        f.maxArchiveChars = S.MaxArchiveChars;
    }

    // =====================================================================
    // Writes
    // =====================================================================

    /// <summary>A device writes one of its objects (RequestSerialization).</summary>
    public bool Write(Device from, string obj, string text)
    {
        string own = OwnerOf(obj);
        bool stale = staleOwner.TryGetValue(obj, out var so) && so == from.Name && Now < staleUntil[obj];
        if (own != from.Name && !stale) { DroppedNonOwnerWrites++; return false; }
        long bytes = 2L * text.Length;
        if (bytes > S.ObjectLimitBytes) { RefusedOversize++; Violation("send of " + obj + " by " + from.Name + " refused: " + bytes + " bytes"); return false; }
        if (bytes > LargestSendBytes) { LargestSendBytes = bytes; LargestSendObj = obj; }
        Count(from, bytes);
        Spy(from, obj, text);
        latest[obj] = (text, from.Name);
        foreach (var d in Present)
        {
            if (d == from) continue;
            var key = (obj, from.Name, d);
            if (pending.TryGetValue(key, out var p) && Rng.NextDouble() < S.CoalesceChance) { p.Text = text; continue; }
            long t = Now + Delay(d);
            if (lastSched.TryGetValue(key, out var last) && last > t) t = last;
            lastSched[key] = t;
            var e = new Ev { Time = t, Kind = 0, Obj = obj, Text = text, Name = from.Name, To = d, Inc = d.Incarnation, Key = key, HasKey = true };
            pending[key] = e;
            Push(e);
        }
        return true;
    }

    private void Count(Device d, long bytes)
    {
        long sec = Now / 1000;
        if (sec != d.Second)
        {
            for (long k = Math.Max(d.Second + 1, sec - 4); k <= sec; k++) d.Ring[k % 5] = 0;
            d.Second = sec; d.BytesThisSecond = 0;
        }
        d.Ring[sec % 5] += bytes;
        long avg = d.Ring.Sum() / 5;
        if (avg > d.PeakAverage) d.PeakAverage = avg;
        if (avg > PeakAverage) PeakAverage = avg;
        d.BytesThisSecond += bytes;
        if (d.BytesThisSecond > d.PeakBytesPerSecond) d.PeakBytesPerSecond = d.BytesThisSecond;
        if (d.BytesThisSecond > PeakBytesPerSecond) PeakBytesPerSecond = d.BytesThisSecond;
        if (avg > S.BudgetBytesPerSecond && avg - bytes / 5 <= S.BudgetBytesPerSecond) OverBudgetSeconds++;
    }

    private void Spy(Device from, string where, string text)
    {
        foreach (var kv in secretSalt)
            if (!frozen.Contains(kv.Value) && text.Contains(kv.Key))
                Violation("secrecy: a salt of phase " + kv.Value + " appeared in " + where + " written by " + from.Name + " before the freeze");
    }

    private void SavePlayerData(Device d, string text)
    {
        long bytes = 2L * text.Length;
        if (bytes > LargestSaveBytes) LargestSaveBytes = bytes;
        Spy(d, "PlayerData of " + d.Name, text);
        bool ok = bytes <= S.PlayerDataLimitBytes && !S.SaveFailNames.Contains(d.Name) && !(S.SaveFailChance > 0 && Rng.NextDouble() < S.SaveFailChance);
        Push(new Ev { Time = Now + R(50, 500), Kind = 5, Ok = ok, To = d, Inc = d.Incarnation });
        if (!ok) { FailedSaves++; return; }
        Cloud[d.Name] = text;
        foreach (var other in Present)
            if (other != d) Push(new Ev { Time = Now + R(S.PlayerDataMinDelayMs, S.PlayerDataMaxDelayMs), Kind = 4, Name = d.Name, Text = text, To = other, Inc = other.Incarnation });
    }

    // =====================================================================
    // Running
    // =====================================================================

    public void RunFor(long ms) { long end = Now + ms; while (Now < end) Step(); }

    /// <summary>Runs until the condition holds or the time runs out; returns whether it held.</summary>
    public bool RunUntil(Func<bool> done, long maxMs)
    {
        long end = Now + maxMs;
        while (Now < end) { if (done()) return true; Step(); }
        return done();
    }

    public void Step()
    {
        Now += S.StepMs;
        while (queue.Count > 0 && queue.Peek().Time <= Now)
        {
            var e = queue.Dequeue();
            if (e.Cancelled) continue;
            if (e.HasKey && pending.TryGetValue(e.Key, out var pe) && pe == e) pending.Remove(e.Key);
            if (!e.To.Present || e.To.Incarnation != e.Inc) continue;
            var n = e.To.Node;
            switch (e.Kind)
            {
                case 0: n.OnReceive(e.Obj, e.Text); break;
                case 1:
                    e.To.LocalOwner[e.Obj] = e.Name;
                    if (Rng.NextDouble() < S.OwnerEventChance) n.OnOwner(e.Obj, e.Name);
                    break;
                case 2: n.OnPlayerJoined(e.Name); break;
                case 3: n.OnPlayerLeft(e.Name); break;
                case 4: n.OnPlayerData(e.Name, e.Text); break;
                case 5: n.OnSaveResult(e.Ok); break;
                case 6: if (ownerOf.ContainsKey(e.Obj)) Transfer(e.Obj, e.To); break;
            }
        }
        foreach (var d in Present.ToList())
        {
            if (!d.Present) continue;
            Play(d);
            d.Script?.Invoke(this, d);
            if (!d.Present) continue;
            foreach (var kv in d.LocalOwner) d.Node.OnOwner(kv.Key, kv.Value); // Networking.GetOwner, every tick
            int beforeCount = d.G.History.count; string beforeLine = d.G.Flow.timeline; string beforePid = d.Node.PhaseId();
            d.Node.Step((int)Now);
            Observe(d, beforeCount, beforeLine, beforePid);
            Drain(d);
        }
        if (Now % 1000 < S.StepMs) CheckContinuously();
    }

    // the simulated person wearing the headset
    private void Play(Device d)
    {
        var n = d.Node; var f = d.G.Flow;
        if (d.Passive || !n.haveSession) return;
        int p = n.MyPower();
        string pid = n.PhaseId();
        if (pid != d.SeenPid)
        {
            d.SeenPid = pid; d.Chosen = false; d.SubmitAt = -1;
            d.ChooseAt = Now + R(S.ThinkMinMs, S.ThinkMaxMs);
            d.ReadyAt = Now + R(S.ReadyMinMs, S.ReadyMaxMs);
        }
        if (p < 0) return;
        bool taking = f.stage == GameFlow.STAGE_ORDERS || f.stage == GameFlow.STAGE_PAUSED;
        if (taking && !d.Chosen && Now >= d.ChooseAt)
        {
            n.SetSheet(f.NeedsOrders(p) ? d.Bot.Choose(p) : "");
            d.Chosen = true;
            if (!d.NeverSubmit) d.SubmitAt = Now + R(S.SubmitMinMs, S.SubmitMaxMs);
        }
        if (taking && d.SubmitAt >= 0 && Now >= d.SubmitAt && !d.NeverSubmit)
        {
            d.SubmitAt = -1;
            if (f.NeedsSeal(p) && !n.IsSubmitted() && n.SubmitOn()) d.SubmittedAt[pid] = Now;
        }
        if (f.stage == GameFlow.STAGE_READY && !d.NeverReady && Now >= d.ReadyAt && !(n.readyPhase == pid && n.myReady)) n.PressReady(true);
    }

    private void Drain(Device d)
    {
        var n = d.Node;
        for (int i = 0; i < n.outCount; i++)
        {
            string text = d.OutFilter == null ? n.outText[i] : d.OutFilter(n.outId[i], n.outText[i]);
            if (text != null) Write(d, n.outId[i], text);
        }
        n.outCount = 0;
        for (int i = 0; i < n.ownWantCount; i++)
        {
            string obj = n.ownWant[i];
            if (OwnerOf(obj) != d.Name && ownerOf.ContainsKey(obj))
                Push(new Ev { Time = Now + Delay(d), Kind = 6, Obj = obj, To = d, Inc = d.Incarnation });
        }
        n.ownWantCount = 0;
        if (n.saveOut != "") { SavePlayerData(d, n.saveOut); n.saveOut = ""; }
        for (int i = 0; i < n.keptSalt.Length; i++)
            if (n.keptSalt[i] != "" && !frozen.Contains(n.keptPhase[i])) secretSalt.TryAdd(n.keptSalt[i], n.keptPhase[i]);
    }

    // after a device's step: freezes (for the spy), resolutions (engine truth, no silent loss)
    private void Observe(Device d, int beforeCount, string beforeLine, string beforePid)
    {
        var f = d.G.Flow; var n = d.Node;
        string pid = n.PhaseId();
        if (f.stage == GameFlow.STAGE_REVEAL && !d.SawFrozenAt.ContainsKey(pid)) d.SawFrozenAt[pid] = Now;
        if (d.G.History.count > beforeCount && !d.SawFrozenAt.ContainsKey(beforePid)) d.SawFrozenAt[beforePid] = Now;
        if (!n.isHub || !d.Honest) return;
        if (f.stage == GameFlow.STAGE_REVEAL) Freeze(pid);
        if (d.G.History.count == beforeCount + 1 && f.timeline == beforeLine)
        {
            Freeze(beforePid);
            Resolutions++;
            CheckResolution(d);
        }
    }

    private void Freeze(string pid)
    {
        if (!frozen.Add(pid)) return;
        foreach (var k in secretSalt.Where(kv => kv.Value == pid).Select(kv => kv.Key).ToList()) secretSalt.Remove(k);
    }

    // invariants 2 and 5 at the moment the hub resolves
    private void CheckResolution(Device hub)
    {
        var f = hub.G.Flow; var h = hub.G.History;
        var rf = reference.Flow;
        if (!rf.LoadCode(h.CodeAt(h.count - 2), 0)) { Violation("reference could not load: " + rf.error); return; }
        for (int p = 0; p < Map.powerCount; p++)
        {
            string sheet = f.lastSheet[p];
            if (f.lastSeal[p] != "")
            {
                string holder = h.seatName[p];
                var dev = Present.FirstOrDefault(x => x.Name == holder && x.Honest);
                if (dev != null)
                {
                    int k = Array.IndexOf(dev.Node.keptSeal, f.lastSeal[p]);
                    if (k >= 0 && f.lastSalt[p] != "" && dev.Node.keptSheet[k] != sheet)
                        Violation("no silent loss: " + Map.powerName[p] + " was resolved with a sheet other than the one sealed");
                    if (k >= 0 && f.lastSalt[p] == "" && f.unopened[p] == 0)
                        Violation("no silent loss: " + Map.powerName[p] + "'s seal was dropped without being counted as unopened");
                }
            }
            int status = hub.G.Announce.powerStatus[p];
            var seated = Present.FirstOrDefault(x => x.Name == h.seatName[p]);
            if ((status == Announcement.S_NONE || status == Announcement.S_UNOPENED) && seated != null && Now - seated.JoinedAt > 30_000)
                LostSheets.Add(h.count + " " + Map.powerName[p] + " " + status);
            if (sheet != "" && !rf.Submit(p, sheet)) Violation("reference refused " + Map.powerName[p] + "'s sheet: " + rf.error);
        }
        rf.ForceNext(0);
        if (rf.CurrentCode() != f.CurrentCode()) Violation("engine truth: the hub's result differs from the engine run directly, at " + h.count);
    }

    // invariants 1 and 4, every simulated second
    private void CheckContinuously()
    {
        var hub = Hub;
        if (hub == null || !hub.Node.isHub || !hub.Honest) return;
        var hf = hub.G.Flow; var hh = hub.G.History;
        string sig = hh.count + "|" + hf.stage + "|" + hf.timeline;
        if (sig != lastProgressSig) { lastProgressSig = sig; lastProgress = Now; }
        bool waiting = hf.stage == GameFlow.STAGE_PAUSED || hf.stage == GameFlow.STAGE_FINISHED || hf.stage == GameFlow.STAGE_LOBBY || hf.stage == GameFlow.STAGE_READY;
        long limit = 1000L * (Math.Max(S.MoveSeconds, Math.Max(S.RetreatSeconds, S.AdjustSeconds)) + 5 + 20 + 60);
        if (!waiting && Now - lastProgress > limit) { Violation("liveness: the game has not moved for " + (Now - lastProgress) / 1000 + " s (stage " + hf.stage + ")"); lastProgress = Now; }
        if (hh.count == 0) return;
        string code = hh.CodeAt(hh.count - 1);
        foreach (var d in Present)
        {
            if (d == hub || !d.Honest) continue;
            var dh = d.G.History;
            bool same = dh.count == hh.count && d.G.Flow.timeline == hf.timeline && dh.CodeAt(dh.count - 1) == code;
            if (same) { d.MismatchSince = -1; d.ReportedLag = false; continue; }
            if (d.MismatchSince < 0) d.MismatchSince = Now;
            if (!d.ReportedLag && Now - d.MismatchSince > S.AgreeAfterMs && Now - d.JoinedAt > S.AgreeAfterMs)
            {
                d.ReportedLag = true;
                Violation("agreement: " + d.Name + " has been at " + dh.count + " while the hub is at " + hh.count + " for " + (Now - d.MismatchSince) / 1000 + " s");
            }
        }
    }

    // =====================================================================
    // Final checks and reports
    // =====================================================================

    /// <summary>Runs until every honest present device holds the hub's archive (or the time runs out).</summary>
    public bool Settle(long maxMs = 30_000)
    {
        return RunUntil(() =>
        {
            if (Now % 1000 >= S.StepMs) return false;
            var hub = Hub;
            if (hub == null || !hub.Node.isHub) return false;
            string a = hub.G.Flow.Archive();
            return Present.All(d => !d.Honest || d == hub || d.G.Flow.Archive() == a);
        }, maxMs);
    }

    /// <summary>Checks every honest present device holds the hub's archive, and nobody raised an alarm.</summary>
    public void CheckSettled(bool alarmsAllowed = false)
    {
        var hub = Hub;
        if (hub == null) { Violation("nobody is hub"); return; }
        string a = hub.G.Flow.Archive();
        foreach (var d in Present)
            if (d.Honest && d != hub && d.G.Flow.Archive() != a)
            {
                var x = a.Split(';'); var y = d.G.Flow.Archive().Split(';');
                int i = 0;
                while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
                Violation("agreement: " + d.Name + "'s archive differs from the hub's at the end, entry " + i + " of " + x.Length + "/" + y.Length
                    + ": hub " + (i < x.Length ? x[i] : "-") + " / " + d.Name + " " + (i < y.Length ? y[i] : "-"));
            }
        if (!alarmsAllowed)
            foreach (var d in Devices)
                for (int i = 0; i < d.Node.alarmCount; i++) Violation("alarm in an honest run on " + d.Name + ": " + d.Node.alarms[i]);
    }

    /// <summary>Invariant 6: each seated present player's save is the agreed archive or an earlier part of it.</summary>
    public void CheckSaves()
    {
        var hub = Hub;
        if (hub == null) return;
        var bodies = Bodies(hub.G.Flow.Archive());
        foreach (var d in Present)
        {
            if (d.Node.MyPower() < 0) continue;
            if (!Cloud.TryGetValue(d.Name, out var save)) { Violation("saves: " + d.Name + " has no save"); continue; }
            if (2L * save.Length > S.PlayerDataLimitBytes) Violation("saves: " + d.Name + "'s save is over the limit");
            var mine = Bodies(save);
            int off = bodies.IndexOf(mine[0]);
            bool ok = off >= 0 && mine.Count + off <= bodies.Count && mine.Select((b, i) => bodies[off + i] == b).All(x => x);
            if (!ok) Violation("saves: " + d.Name + "'s save is not a prefix of the agreed archive");
        }
    }

    public static List<string> Bodies(string archive)
    {
        var p = archive.Split(';');
        var r = new List<string>();
        for (int i = 4; i < p.Length - 1; i++) { int plus = p[i].IndexOf('+'); r.Add(plus < 0 ? p[i] : p[i][..plus]); }
        return r;
    }

    public List<string> AllAlarms() => Devices.SelectMany(d => Enumerable.Range(0, d.Node.alarmCount).Select(i => d.Name + ": " + d.Node.alarms[i])).ToList();

    public void Violation(string text) { if (Violations.Count < 200) Violations.Add("t=" + (Now - 1_000_000) / 1000.0 + "s " + text); }

    public void Note(string text) { if (Log.Count < 2000) Log.Add("t=" + (Now - 1_000_000) / 1000.0 + "s " + text); }

    public string Report()
    {
        return "seed " + Seed + ": " + Resolutions + " resolutions, " + (Now - 1_000_000) / 1000 + " s simulated; peak " + PeakBytesPerSecond
            + " B in one second, " + PeakAverage + " B/s over 5 s (budget " + S.BudgetBytesPerSecond + ", over " + OverBudgetSeconds + " times); largest send " + LargestSendBytes + " B (" + LargestSendObj
            + "); largest save " + LargestSaveBytes + " B; dropped non-owner writes " + DroppedNonOwnerWrites
            + (Violations.Count > 0 ? "\n" + string.Join("\n", Violations.Take(20)) : "");
    }
}
