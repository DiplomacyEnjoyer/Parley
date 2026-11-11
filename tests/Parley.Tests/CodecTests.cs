// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

public class CodecTests
{
    private static Game Ring(int n = 3, bool isthmus = false) => Game.FromPmap(RingMapGenerator.Generate(n, isthmus));

    [Theory]
    [InlineData(2)] [InlineData(7)] [InlineData(17)] [InlineData(70)]
    public void StartPositionSurvivesSaveAndLoad(int players)
    {
        var g = Ring(players);
        string code = g.Codec.EncodeState(g.State);
        Assert.Matches("^[A-Za-z0-9.-]+$", code); // only chat-safe characters
        var g2 = Ring(players);
        g2.State.Clear();
        Assert.True(g2.Codec.DecodeState(code, g2.State), g2.Codec.error);
        Assert.Equal(code, g2.Codec.EncodeState(g2.State));
        Assert.Equal(g.State.unitCount, g2.State.unitCount);
        Assert.Equal(g.Map.startYear, g2.State.year);
    }

    [Fact]
    public void DamagedOrForeignCodesAreRefusedWithAReason()
    {
        var g = Ring(3);
        string code = g.Codec.EncodeState(g.State);
        var s = g.Scratch;

        // any single changed character is caught
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i] == 'A' ? 'B' : 'A';
            string bad = code.Substring(0, i) + c + code.Substring(i + 1);
            Assert.False(g.Codec.DecodeState(bad, s), "accepted damaged code at position " + i);
        }
        Assert.False(g.Codec.DecodeState(code.Substring(0, code.Length - 7), s));   // cut off
        Assert.Contains("damaged", g.Codec.error);
        Assert.False(g.Codec.DecodeState("hello there", s));
        Assert.Contains("not a game code", g.Codec.error);
        Assert.False(g.Codec.DecodeState("", s));
        Assert.False(g.Codec.DecodeState(null, s));
        Assert.False(g.Codec.DecodeState("P9" + code.Substring(2), s));
        Assert.Contains("different version", g.Codec.error);

        // a code from another map says so
        var other = Ring(4);
        Assert.False(other.Codec.DecodeState(code, other.Scratch));
        Assert.Contains("ring3", other.Codec.error);
        Assert.Equal("ring3." + g.Map.hash, other.Codec.PeekMap(code));

        // same id, different connections -> different fingerprint -> refused
        var changed = Game.FromPmap(RingMapGenerator.Generate(3).Replace("army alp: alr alc", "army alp: alr"));
        Assert.NotEqual(g.Map.hash, changed.Map.hash);
        Assert.False(changed.Codec.DecodeState(code, changed.Scratch));
        Assert.Contains("different version of the map", changed.Codec.error);

        // surrounding whitespace from copy/paste is fine
        Assert.True(g.Codec.DecodeState("  " + code + "\n", s), g.Codec.error);
    }

    [Fact]
    public void OrdersSurviveSaveAndLoadAndCannotTouchOtherPowersUnits()
    {
        var g = Ring(3);
        var s = g.State; var n = g.Notation; var map = g.Map;
        int alder = map.FindPower("alder"), brann = map.FindPower("brann");
        g.Adj.ClearOrders();
        Assert.True(n.ApplyMovement(s, g.Adj, alder, n.Parse("F alp - als")));
        Assert.True(n.ApplyMovement(s, g.Adj, alder, n.Parse("A alc S alg - alm")));
        Assert.True(n.ApplyMovement(s, g.Adj, alder, n.Parse("A alg - alm")));
        Assert.True(n.ApplyMovement(s, g.Adj, brann, n.Parse("A brc - brl")));
        string all = g.Codec.EncodeOrders(s, g.Adj, -1);
        string mine = g.Codec.EncodeOrders(s, g.Adj, alder);
        Assert.Matches("^[A-Za-z0-9]+$", all);
        Assert.True(mine.Length < all.Length);

        // decode everything: identical orders
        var adj2 = new Adjudicator(); adj2.Init(map);
        Assert.True(g.Codec.DecodeOrders(all, s, adj2, -1));
        Assert.Equal(all, g.Codec.EncodeOrders(s, adj2, -1));

        // Brann hands in the full text, but only Brann's own unit is affected
        var adj3 = new Adjudicator(); adj3.Init(map);
        Assert.True(g.Codec.DecodeOrders(all, s, adj3, brann));
        Assert.Equal(g.Codec.EncodeOrders(s, g.Adj, brann), g.Codec.EncodeOrders(s, adj3, -1));

        // rubbish is refused, not half-applied silently
        Assert.False(g.Codec.DecodeOrders("zz!", s, adj3, -1));
        Assert.False(g.Codec.DecodeOrders(all.Substring(0, all.Length - 1), s, adj3, -1));
    }

    [Fact]
    public void ArchiveKeepsHistorySeatsAndTimers()
    {
        var g = Ring(3);
        var flow = g.Flow;
        flow.SetTimers(600, 120, 180);
        flow.ClaimSeat(0, "Ada");
        flow.ClaimSeat(1, "Björn the Bold; 3rd+");   // awkward characters on purpose
        flow.ClaimSeat(2, "名前");
        flow.StartGame(0);
        var bot = new Bot(g, new Random(5), true);
        for (int i = 0; i < 9; i++)
        {
            for (int p = 0; p < 3; p++) if (flow.NeedsOrders(p)) { flow.Submit(p, bot.Choose(p)); flow.SetLocked(p, true); }
            Assert.True(flow.Tick(i + 1));
        }
        string archive = flow.Archive();
        Assert.Matches("^[A-Za-z0-9.;,+!-]+$", archive);
        Assert.Equal(10, g.History.count);

        var g2 = Ring(3);
        Assert.True(g2.Flow.LoadArchive(archive, 0), g2.Flow.error);
        Assert.Equal(archive, g2.Flow.Archive());
        Assert.Equal("Björn the Bold; 3rd+", g2.History.seatName[1]);
        Assert.Equal("名前", g2.History.seatName[2]);
        Assert.Equal(600, g2.History.moveSeconds);
        Assert.Equal(g.Codec.EncodeState(g.State), g2.Codec.EncodeState(g2.State));
        // every past phase can be pulled out as a stand-alone code
        for (int i = 0; i < g2.History.count; i++)
            Assert.True(g2.Codec.DecodeState(g2.History.CodeAt(i), g2.Scratch), g2.Codec.error);

        // damage anywhere is caught
        string bad = archive.Substring(0, 60) + (archive[60] == '0' ? '1' : '0') + archive.Substring(61);
        Assert.False(Ring(3).Flow.LoadArchive(bad, 0));
        Assert.False(Ring(4).Flow.LoadArchive(archive, 0));
    }

    [Fact]
    public void ArchiveCanBeKeptUnderASizeBudget()
    {
        var g = Ring(3);
        var flow = g.Flow;
        for (int p = 0; p < 3; p++) flow.ClaimSeat(p, "bot" + p);
        flow.maxArchiveChars = 1500;
        flow.StartGame(0);
        var bot = new Bot(g, new Random(9), true);
        for (int i = 0; i < 60 && flow.stage == GameFlow.STAGE_ORDERS; i++)
        {
            for (int p = 0; p < 3; p++) if (flow.NeedsOrders(p)) { flow.Submit(p, bot.Choose(p)); flow.SetLocked(p, true); }
            flow.Tick(i + 1);
        }
        string archive = flow.Archive();
        Assert.True(archive.Length <= 1600, "archive is " + archive.Length);
        var g2 = Ring(3);
        Assert.True(g2.Flow.LoadArchive(archive, 0), g2.Flow.error);
        Assert.Equal(g.Codec.EncodeState(g.State), g2.Codec.EncodeState(g2.State)); // the current position is never lost
    }
}
