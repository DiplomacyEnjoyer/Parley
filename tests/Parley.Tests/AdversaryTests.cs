// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Security.Cryptography;
using System.Text;
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// Somebody is trying to cheat: a player, the host, or whoever takes over when the host
/// leaves. Each test is one attempt and what the engine does about it. The reasoning is
/// in docs/secret-orders-and-dice.md.
/// </summary>
public class AdversaryTests
{
    private static Game Ring(int n = 3) => Game.FromPmap(RingMapGenerator.Generate(n));

    private static string Orders(Game g, int power, params string[] lines)
    {
        var pad = new Adjudicator(); pad.Init(g.Map);
        foreach (var l in lines) Assert.True(g.Notation.ApplyMovement(g.State, pad, power, g.Notation.Parse(l)), "could not enter order: " + l);
        return g.Codec.EncodeOrders(g.State, pad, power);
    }

    private static bool HasUnit(Game g, string power, string loc)
    {
        int l = g.Map.FindLoc(loc);
        int u = g.State.provUnit[g.Map.locProv[l]];
        return u >= 0 && g.State.unitLoc[u] == l && g.State.unitPower[u] == g.Map.FindPower(power);
    }

    private static (Game g, GameFlow f, SealedDevice[] dev) Sealed(int move = 900)
    {
        var g = Ring(3); var f = g.Flow;
        f.sealedOrders = true;
        f.SetTimers(move, 0, 0);
        f.Stir(SealedDevice.NewSalt());
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        var dev = new[] { new SealedDevice(g, 0), new SealedDevice(g, 1), new SealedDevice(g, 2) };
        return (g, f, dev);
    }

    // ------------------------------------------------------------ the hash itself

    [Fact]
    public void Sha256MatchesTheNistVectorsIncludingBlockBoundariesAndAMillionLetters()
    {
        var s = new OrderSeal(); s.Init();
        // FIPS 180-4 / NIST CAVP example messages
        Assert.Equal("cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1",
            s.Sha256Hex("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu"));
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", s.Sha256Hex(new string('a', 1000000)));
        // every length around the places where padding spills into another block
        foreach (int len in new[] { 54, 55, 56, 57, 62, 63, 64, 65, 118, 119, 120, 121, 127, 128, 129, 183, 184, 247, 248, 8191, 8192, 8193 })
        {
            string text = new string('q', len);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), s.Sha256Hex(text));
        }
        // one changed bit changes about half of the output: no two of these may be close
        string a = s.Sha256Hex("PS1|salt|0|key|orders"), b = s.Sha256Hex("PS1|salt|0|key|orderr");
        int same = 0; for (int i = 0; i < 64; i++) if (a[i] == b[i]) same++;
        Assert.InRange(same, 0, 16);
    }

    [Fact]
    public void SealedTextMustBePlainSoThatEveryImplementationHashesTheSameBytes()
    {
        var s = new OrderSeal(); s.Init();
        const string salt = "0123456789abcdef0123456789abcdef", key = "k.1.S1M";
        // A lone surrogate is where UTF-8 encoders disagree; such text can be sealed by a
        // modified client but is never accepted at the opening.
        string odd = "0Am0B\ud800";
        Assert.False(s.Check(s.Make(salt, 0, key, odd), salt, 0, key, odd));
        // A '|' could make two different (key, orders) pairs read as the same sealed text.
        Assert.Equal(s.Make(salt, 0, "a|b", "c"), s.Make(salt, 0, "a", "b|c"));
        Assert.False(s.Check(s.Make(salt, 0, "a|b", "c"), salt, 0, "a|b", "c"));
        Assert.False(s.Check(s.Make(salt, 0, "a", "b|c"), salt, 0, "a", "b|c"));
        Assert.False(s.SaltOk("0123456789abcdef"));               // 64 bits is no longer accepted
        Assert.False(s.SaltOk("0123456789abcdef|123456789abcdef"));
    }

    // ------------------------------------------------------------ peeking

    [Fact]
    public void AGuessableSaltLetsOthersReadASealWhichIsWhyDevicesKeepAPool()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        // A careless device salts with something an eavesdropper can enumerate (here: one of 1000 values).
        var careless = new OrderSeal(); careless.Init();
        string secretSheet = Orders(g, 0, "A alc - alk");
        string weakSalt = new string('0', 28) + "0417";
        string published = careless.Make(weakSalt, 0, f.SealKey(), secretSheet);
        // The eavesdropper tries every likely sheet under every possible salt and finds it.
        string[] likely = { "", Orders(g, 0, "A alc - brk"), Orders(g, 0, "F alp - als"), secretSheet };
        string found = null;
        for (int i = 0; i < 1000 && found == null; i++)
            foreach (var sheet in likely)
                if (careless.Make(new string('0', 28) + i.ToString("0000"), 0, f.SealKey(), sheet) == published) found = sheet;
        Assert.Equal(secretSheet, found);

        // The pool: salts never repeat, and a published salt says nothing about the next one.
        var a = new OrderSeal(); a.Init(); var b = new OrderSeal(); b.Init();
        a.Stir("head 0.31 1.62 -0.05 tap 1731364800.4411"); b.Stir("head 0.31 1.62 -0.05 tap 1731364800.4411");
        string s1 = a.NextSalt(), s2 = a.NextSalt();
        Assert.True(a.SaltOk(s1)); Assert.NotEqual(s1, s2);
        Assert.Equal(s1, b.NextSalt());                   // same noise, same salt: the noise is all there is ...
        b.Stir("one more tap");
        Assert.NotEqual(s2, b.NextSalt());                // ... so every bit of fresh noise sends the device somewhere new
        var c = new OrderSeal(); c.Init();
        c.Stir(s1);                                       // an attacker who only has the published salt cannot continue the series
        Assert.NotEqual(s2, c.NextSalt());
    }

    [Fact]
    public void TheLiveStateAHostPublishesNeverContainsAnUnopenedSheet()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "A alc - alk"); dev[1].Sheet = Orders(g, 1, "A brg - brm");
        foreach (var d in dev) d.SubmitOn();
        Assert.DoesNotContain(dev[0].Sheet, f.LiveState(1)); Assert.DoesNotContain(dev[0].Salt, f.LiveState(1));
        f.Tick(1);
        dev[0].React();
        string live = f.LiveState(2);
        Assert.Contains(dev[0].Sheet, live);              // opened: public from now on
        Assert.DoesNotContain(dev[1].Sheet, live); Assert.DoesNotContain(dev[1].Salt, live);
    }

    // ------------------------------------------------------------ changing

    [Fact]
    public void TheHostCannotCutTheOpeningShortToLeaveALatecomerHolding()
    {
        var (g, f, dev) = Sealed();
        f.revealSeconds = 0;                              // "wait until the host moves on"
        f.StartGame(0);
        dev[1].Sheet = Orders(g, 1, "A brg - brm");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(100);
        dev[0].React(); dev[2].React();                   // the host has seen two sheets and would rather Bo's never arrived
        f.ForceNext(101);
        Assert.Equal(GameFlow.STAGE_REVEAL, f.stage);
        Assert.Contains("still coming in", f.error);
        dev[1].React();                                   // Bo's device opens a moment later, as honest devices do
        f.Tick(102);
        Assert.True(HasUnit(g, "brann", "brm"));
        // A device that really is stuck can still be skipped once everyone has had a fair chance.
        var (g2, f2, dev2) = Sealed();
        f2.revealSeconds = 0; f2.StartGame(0);
        foreach (var d in dev2) d.SubmitOn();
        f2.Tick(100); dev2[0].React();
        f2.ForceNext(100 + f2.revealFloorSeconds);
        Assert.NotEqual(GameFlow.STAGE_REVEAL, f2.stage);
    }

    [Fact]
    public void HandingASeatToSomeoneElseAfterTheFreezeDoesNotTakeOrdersBack()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "A alc - alk");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1);
        // Ada sees that her move turned out badly, leaves, and a friend takes her seat before the result.
        dev[1].React(); dev[0].React();
        f.PlayerLeft("Ada");
        Assert.True(f.ClaimSeat(0, "Dee"), f.error);
        Assert.NotEqual("", f.sealText[0]);
        dev[2].React(); f.Tick(2);
        Assert.True(HasUnit(g, "alder", "alk"));          // the opened sheet stood
        Assert.True(f.VerifyLast(), f.error);
    }

    [Fact]
    public void AMalformedSheetCannotBeSmuggledPastTheSeal()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        dev[0].Sheet = "0Am";                              // cut off in the middle of an order
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1);
        Assert.False(f.Open(0, dev[0].Salt, dev[0].Sheet));
        Assert.False(f.opened[0]);
    }

    // ------------------------------------------------------------ replaying

    [Fact]
    public void ASealFromAnotherGameOrFromBeforeARollBackDoesNotFit()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        string key1 = f.SealKey();
        dev[0].Sheet = Orders(g, 0, "A alc - alk");
        foreach (var d in dev) d.SubmitOn();
        string oldSeal = f.sealText[0], oldSalt = dev[0].Salt, oldSheet = dev[0].Sheet;
        f.Tick(1); foreach (var d in dev) d.React(); f.Tick(2);
        Assert.True(f.VerifyLast(), f.error);

        // The host rolls the game back to that same phase: the position is identical ...
        Assert.True(f.RollBack(0, 10)); f.PlayerJoined("Ada"); f.PlayerJoined("Bo"); f.PlayerJoined("Cy"); f.Resume(10);
        Assert.NotEqual(key1, f.SealKey());
        // ... and somebody who can write in Ada's name replays her old seal and, at the reveal, her old sheet.
        Assert.True(f.SetSeal(0, oldSeal));
        dev[1].SubmitOn(); dev[2].SubmitOn();
        f.Tick(11);
        Assert.False(f.Open(0, oldSalt, oldSheet));
        Assert.Contains("do not match the seal", f.error);

        // A second game on the same board starts from the same position, with another name.
        var (g2, f2, _) = Sealed();
        f2.StartGame(0);
        Assert.NotEqual(key1, f2.SealKey());
        Assert.Equal(key1.Substring(key1.IndexOf('.')), f2.SealKey().Substring(f2.SealKey().IndexOf('.')));
        // Resuming from a saved code or archive starts a new run too.
        string before = f2.SealKey();
        Assert.True(f2.LoadCode(f2.CurrentCode(), 5));
        Assert.NotEqual(before, f2.SealKey());
        // And a seal cannot be moved to another seat: it names its power.
        Assert.False(g.Seal.Check(oldSeal, oldSalt, 1, key1, oldSheet));
        Assert.True(g.Seal.Check(oldSeal, oldSalt, 0, key1, oldSheet));
    }

    // ------------------------------------------------------------ withholding

    [Fact]
    public void WithholdingCostsTheWholeTurnAndIsCounted()
    {
        var (g, f, dev) = Sealed();
        f.revealSeconds = 20;
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "A alc - alk");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1);
        dev[1].React(); dev[2].React();                   // Ada reads the others' sheets and decides not to open
        f.Tick(22);
        Assert.False(HasUnit(g, "alder", "alk"));         // none of her orders happened
        Assert.Equal(Announcement.S_UNOPENED, g.Announce.powerStatus[0]);
        Assert.Equal(1, f.unopened[0]); Assert.Equal(0, f.unopened[1]);
        Assert.True(f.VerifyLast(), f.error);
    }

    // ------------------------------------------------------------ a host that lies about the result

    [Fact]
    public void EveryDeviceCanCheckTheResolvedPhaseForItself()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "A alc - alk");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1); foreach (var d in dev) d.React(); f.Tick(2);
        Assert.True(f.VerifyLast(), f.error);
        string real = f.lastSheet[0];
        f.lastSheet[0] = Orders(g, 0);                    // the host claims Ada ordered nothing
        Assert.False(f.VerifyLast());
        Assert.Contains("does not match its seal", f.error);
        f.lastSheet[0] = real; f.lastSalt[1] = ""; f.lastSheet[1] = "0Am0B";   // or invents orders nobody opened
        Assert.False(f.VerifyLast());
        Assert.Contains("nobody opened", f.error);
    }

    // ------------------------------------------------------------ the host leaves mid-phase

    [Theory]
    [InlineData(GameFlow.STAGE_ORDERS)]
    [InlineData(GameFlow.STAGE_PAUSED)]
    [InlineData(GameFlow.STAGE_CLOSING)]
    [InlineData(GameFlow.STAGE_REVEAL)]
    public void ANewHostCarriesOnMidPhaseFromArchiveAndLiveState(int when)
    {
        var (g, f, dev) = Sealed(move: 900);
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "A alc - alk"); dev[1].Sheet = Orders(g, 1, "A brg - brm");
        dev[0].SubmitOn();
        float now = 100;
        if (when == GameFlow.STAGE_PAUSED) f.Pause(now);
        if (when == GameFlow.STAGE_CLOSING || when == GameFlow.STAGE_REVEAL) { now = 900; f.Tick(now); dev[1].React(); }
        if (when == GameFlow.STAGE_REVEAL) { dev[2].React(); f.Tick(now); dev[0].React(); }
        Assert.Equal(when, f.stage);
        string archive = f.Archive(), live = f.LiveState(now);

        // The old host is gone. Another machine, with its own clock, picks the game up.
        var h = Ring(3); var hf = h.Flow;
        hf.sealedOrders = true; hf.revealSeconds = f.revealSeconds;
        float t = 5000;
        Assert.True(hf.LoadArchive(archive, t), hf.error);
        Assert.True(hf.LoadLiveState(live, t), hf.error);
        hf.PlayerJoined("Ada"); hf.PlayerJoined("Bo"); hf.PlayerJoined("Cy");
        Assert.Equal(when, hf.stage);
        Assert.Equal(f.SealKey(), hf.SealKey());          // same run: the devices' seals still fit
        Assert.Equal(f.sealText[0], hf.sealText[0]);
        if (when == GameFlow.STAGE_ORDERS) Assert.Equal(800, hf.SecondsLeft(t), 1);
        // The players' devices are the same devices; only the flow they talk to has changed.
        var d2 = new[] { new SealedDevice(h, 0), new SealedDevice(h, 1), new SealedDevice(h, 2) };
        for (int p = 0; p < 3; p++) d2[p].Adopt(dev[p]);
        if (when == GameFlow.STAGE_PAUSED) hf.Resume(t);
        if (when == GameFlow.STAGE_ORDERS || when == GameFlow.STAGE_PAUSED) { d2[1].SubmitOn(); d2[2].SubmitOn(); }
        for (int i = 0; i < 6 && h.State.PhaseLabel() == "S1M"; i++) { t += 1; hf.Tick(t); foreach (var d in d2) d.React(); }
        Assert.Equal("F1M", h.State.PhaseLabel());
        Assert.True(HasUnit(h, "alder", "alk")); Assert.True(HasUnit(h, "brann", "brm"));
        Assert.True(hf.VerifyLast(), hf.error);
    }

    [Fact]
    public void AForgedOrStaleLiveStateIsRefused()
    {
        var (g, f, dev) = Sealed();
        f.StartGame(0);
        dev[0].Sheet = Orders(g, 0, "A alc - alk");
        foreach (var d in dev) d.SubmitOn();
        f.Tick(1); dev[0].React();
        string archive = f.Archive(), live = f.LiveState(1);

        Game Fresh() { var h = Ring(3); h.Flow.sealedOrders = true; Assert.True(h.Flow.LoadArchive(archive, 0)); return h; }
        // The stand-in host edits Ada's opened sheet in the hand-over.
        string forged = live.Replace(dev[0].Sheet, Orders(g, 0, "A alc - brk"));
        Assert.NotEqual(live, forged);
        var h1 = Fresh();
        Assert.False(h1.Flow.LoadLiveState(forged, 0));
        Assert.Equal(GameFlow.STAGE_PAUSED, h1.Flow.stage);   // nothing was taken from it
        // ... or marks an unopened seal as opened with a sheet of their own.
        var parts = live.Split('|'); var bo = parts[9].Split(':');
        parts[9] = "3:" + bo[1] + ":" + SealedDevice.NewSalt() + ":" + Orders(g, 1) + ":0";
        Assert.False(Fresh().Flow.LoadLiveState(string.Join("|", parts), 0));
        // A live state from another point of the game does not fit.
        foreach (var d in dev) d.React(); f.Tick(2);
        var h2 = Ring(3); h2.Flow.sealedOrders = true; Assert.True(h2.Flow.LoadArchive(f.Archive(), 0));
        Assert.False(h2.Flow.LoadLiveState(live, 0));
        Assert.Contains("different point", h2.Flow.error);
        Assert.False(h2.Flow.LoadLiveState("PL1|nonsense", 0));
        // The genuine one loads.
        Assert.True(Fresh().Flow.LoadLiveState(live, 0));
    }

    // ------------------------------------------------------------ late joiners

    [Fact]
    public void ALateJoinerGetsNothingSecretAndStartsAFreshSheet()
    {
        var g = Ring(3); var f = g.Flow;
        f.sealedOrders = true; f.SetTimers(900, 0, 0);
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo");
        var a = new SealedDevice(g, 0); var b = new SealedDevice(g, 1);
        f.StartGame(0);
        a.Sheet = Orders(g, 0, "A alc - alk"); a.SubmitOn(); b.SubmitOn();
        // Cy arrives in the middle of the phase and takes the empty seat: the phase now waits for Cy too.
        Assert.True(f.ClaimSeat(2, "Cy"));
        Assert.False(f.Tick(2));
        Assert.DoesNotContain(a.Sheet, f.LiveState(2) + f.Archive());
        var c = new SealedDevice(g, 2); c.SubmitOn();
        f.Tick(3); a.React(); b.React(); c.React(); f.Tick(4);
        Assert.Equal("F1M", g.State.PhaseLabel());
        Assert.True(f.VerifyLast(), f.error);
    }
}
