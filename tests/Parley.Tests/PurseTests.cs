// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;
using Parley.Tools;
using Xunit;
using static Parley.Engine.PurseRules;

namespace Parley.Tests;

/// <summary>
/// Tricky situations in the Purse and Dagger ruleset, in the spirit of the DATC: each
/// test is one situation and the ruling. The case numbers (A.1 ...) are the ones used
/// in docs/machiavelli-rulings.md, which gives the reasoning.
///
/// The board is Three Valleys (tests/purse/valleys.pmap): Red (rcap walled port city
/// worth 2, rfield, rtown open port city), Blue (bcap walled, bfield, bport walled
/// port), Green (gcap walled port worth 3, gfield), neutral mid (walled, autonomous
/// garrison), pass, isle (city and province in one space), seas bay and sound.
/// </summary>
public class PurseTests
{
    // =============================================================== A. money

    [Fact]
    public void A1_TheGameOpensWithIncomeAndAnAdjustmentPhase()
    {
        var g = Game.FromPmap(PurseBoard.Valleys);
        Assert.Equal("W1399A", g.State.PhaseLabel());
        Assert.Equal("Purse and Dagger", g.Rules.Title());
        // Red: 3 provinces + city 2 + city 1, and the fixed opening roll of 3 on its income line = 3 ducats
        Assert.Equal(9, g.Purse.Treasury(g.State, 0));
        Assert.Equal(8, g.Purse.Treasury(g.State, 1));   // Blue: 3 + 1 + 1, and 3
        Assert.Equal(9, g.Purse.Treasury(g.State, 2));   // Green: 2 + 3, and two dice worth 2 each
        Assert.Equal(0, g.Purse.Treasury(g.State, 3));   // nobody collects for the autonomous garrison
        Assert.False(g.Rules.Playable(3));
        Assert.False(g.Rules.UsesDice(g.State));          // no dice outside campaigns
    }

    private static PurseBoard Winter(int options = -1)
    {
        var b = new PurseBoard(options);
        b.SameDate = false;
        b.s.phase = BoardState.PHASE_ADJUST; b.s.season = 3; b.s.year = 1399;
        return b;
    }

    [Fact]
    public void A2_EveryUnitMustBePaidForAndTheUnpaidGoHome()
    {
        var b = Winter();
        b.SetMoney("red", 7);                 // three units cost 9
        b.Go();
        Assert.Equal("S1400M", b.Label);
        Assert.True(b.Has("red", 'A', "rcap")); Assert.True(b.Has("red", 'A', "rfield"));
        Assert.True(b.U("rtown") < 0);        // paid in board order; the money ran out at the third
        Assert.Equal(1, b.Money("red"));
        Assert.True(b.Has("auto", 'G', "mid_c")); // autonomous garrisons cost nobody anything

        // Choosing which unit to lose: dismiss it, and the others are paid for.
        b = Winter(); b.SetMoney("red", 6);
        b.Dismiss("red", "rcap").Go();
        Assert.True(b.U("rcap") < 0); Assert.True(b.Has("red", 'F', "rtown")); Assert.Equal(0, b.Money("red"));
        // You cannot dismiss somebody else's unit.
        b = Winter();
        b.Dismiss("red", "bcap").Go();
        Assert.True(b.Has("blue", 'A', "bcap"));
    }

    [Fact]
    public void A3_BuildsOnlyInYourOwnHomeCitiesOnePerProvince()
    {
        var b = Winter(); b.SetMoney("red", 30);
        b.Remove("rcap").Remove("rtown");      // two units left to pay: 3 ducats for rfield
        b.Build("red", 'A', "rcap")            // home province with a city: allowed
         .Build("red", 'G', "rcap_c")          // a second new unit in the same province: refused
         .Build("red", 'A', "rfield")          // no city (and occupied): refused
         .Build("red", 'F', "rtown")           // an open port city: a fleet may be built
         .Build("red", 'A', "mid")             // not Red's home country: refused
         .Build("red", 'G', "bcap_c");         // certainly not Blue's
        b.Go();
        Assert.True(b.Has("red", 'A', "rcap")); Assert.True(b.U("rcap_c") < 0);
        Assert.True(b.Has("red", 'F', "rtown")); Assert.True(b.U("mid") < 0); Assert.True(b.U("bcap_c") < 0);
        Assert.Equal(30 - 3 - 3 - 3, b.Money("red"));

        // An army may not be swapped for a fleet on the spot, but a garrison may make way for one.
        b = Winter(); b.SetMoney("red", 30);
        b.Put("red", 'A', "rtown").Put("red", 'G', "rcap_c").Remove("rcap");
        b.Dismiss("red", "rtown").Build("red", 'F', "rtown").Dismiss("red", "rcap_c").Build("red", 'F', "rcap").Go();
        Assert.True(b.U("rtown") < 0);
        Assert.True(b.Has("red", 'F', "rcap")); Assert.True(b.U("rcap_c") < 0);

        // No money, no unit: the units already in the field are paid first.
        b = Winter(); b.SetMoney("blue", 8);
        b.Remove("bport");
        b.Build("blue", 'F', "bport").Go();
        Assert.True(b.U("bport") < 0); Assert.Equal(2, b.Money("blue"));
        b = Winter(); b.SetMoney("blue", 9);
        b.Remove("bport");
        b.Build("blue", 'F', "bport").Build("blue", 'F', "bcap").Go();   // and an inland city builds no fleet
        Assert.True(b.Has("blue", 'F', "bport")); Assert.Equal(0, b.Money("blue"));
    }

    [Fact]
    public void A4_LoansInterestAndTheLimit()
    {
        var b = new PurseBoard();
        b.SetMoney("red", 0);
        b.Borrow("red", 2, 15).Borrow("red", 1, 10);
        b.Go();
        Assert.Equal(25, b.Money("red"));
        Assert.Equal(10, b.pr.LoanPrincipal(b.s, 0, 0)); Assert.Equal(2, b.pr.LoanInterest(b.s, 0, 0));   // one year: 20%
        Assert.Equal(3, b.pr.LoanDue(b.s, 0, 0));                                                       // three campaigns on: next spring
        Assert.Equal(15, b.pr.LoanPrincipal(b.s, 0, 1)); Assert.Equal(8, b.pr.LoanInterest(b.s, 0, 1));   // two years: 50%, rounded up
        Assert.Equal(6, b.pr.LoanDue(b.s, 0, 1));
        Assert.Equal(35, b.pr.Debt(b.s, 0));
        // At the limit of 25 owed, the bank lends no more.
        string said = b.Borrow("red", 1, 1).Go();
        Assert.Contains("refuses", said);
        Assert.Equal(25, b.Money("red"));
        // When a loan falls due the bank takes it, interest and all.
        b.Campaign(0, 1401);
        said = b.Go();
        Assert.Contains("collects 12", said);
        Assert.Equal(13, b.Money("red"));
        Assert.Equal(-1, b.pr.LoanDue(b.s, 0, 0)); Assert.Equal(6, b.pr.LoanDue(b.s, 0, 1));
        // With the loans option off there is no bank.
        b = new PurseBoard(0);
        Assert.Contains("refuses", b.Borrow("red", 1, 5).Go());
    }

    [Fact]
    public void A5_TheBankersExampleFromTheJudgeRules()
    {
        // "If you have 20 and a loan of 25+13 due, pay the 20 towards it (leaving 18+0), borrow 7,
        //  and hope another player gives you 11."
        var b = new PurseBoard();
        b.SetMoney("red", 0);
        b.Borrow("red", 2, 25).Go();
        Assert.Equal(13, b.pr.LoanInterest(b.s, 0, 0));
        b.Campaign(0, 1402);
        b.SetMoney("red", 20); b.SetMoney("blue", 11);
        b.PayBank("red", 20).Borrow("red", 1, 7).Give("blue", "red", 11);
        string said = b.Go();
        Assert.DoesNotContain("cannot repay", said);
        Assert.Equal(0, b.Money("red")); Assert.Equal(0, b.Money("blue"));
        Assert.Equal(7 + 2, b.pr.Debt(b.s, 0));          // only the new loan is left
        Assert.False(b.pr.Barred(b.s, 0));

        // Borrowing 18 instead would put 43 on the books for a moment: refused, and the ruler pays with his life.
        b = new PurseBoard();
        b.SetMoney("red", 0);
        b.Borrow("red", 2, 25).Go();
        b.Campaign(0, 1402); b.SetMoney("red", 20);
        b.Borrow("red", 1, 18).Move("rfield", "pass");
        said = b.Go(b.SeedWhere(roll => true));
        Assert.Contains("cannot repay", said);
        Assert.True(b.pr.Barred(b.s, 0));
        Assert.Equal(0, b.Money("red")); Assert.Equal(0, b.pr.Debt(b.s, 0));
        Assert.True(b.Has("red", 'A', "rfield"));        // an assassinated ruler's units only hold
        Assert.Contains("refuses", b.Borrow("red", 1, 3).Go(b.SeedWhere(roll => true)));  // and the bank never lends to that house again
    }

    [Fact]
    public void A6_GiftsLeaveBeforeTheyArriveAndDebtorsCannotGive()
    {
        var b = new PurseBoard();
        b.SetMoney("red", 0); b.SetMoney("green", 5); b.SetMoney("blue", 0);
        b.Give("green", "red", 5).Give("red", "blue", 5).Go();
        Assert.Equal(5, b.Money("red")); Assert.Equal(0, b.Money("blue")); // Red had nothing to give when the gifts left
        // A power in debt to the bank cannot give money away.
        b.Borrow("red", 1, 5).Go();
        b.Give("red", "blue", 4);
        Assert.Contains("in debt", b.Go());
        Assert.Equal(0, b.Money("blue"));
        // Paying the debt off first, in the same sheet, frees the gift.
        b.PayBank("red", 6).Give("red", "blue", 4).Go();
        Assert.Equal(4, b.Money("blue")); Assert.Equal(0, b.Money("red"));
    }

    // =============================================================== B. bribes

    [Fact]
    public void B1_ABribeWorksAndACounterBribeEatsIntoIt()
    {
        var b = new PurseBoard(); b.SetMoney("red", 30); b.SetMoney("blue", 9);
        b.Spend("red", EX_DISBAND, "bfield", 12).Go();
        Assert.True(b.U("bfield") < 0); Assert.Equal(18, b.Money("red"));

        b = new PurseBoard(); b.SetMoney("red", 30); b.SetMoney("blue", 9);
        string said = b.Spend("red", EX_DISBAND, "bfield", 12).Spend("blue", EX_COUNTER, "bfield", 3).Go();
        Assert.True(b.Has("blue", 'A', "bfield")); Assert.Contains("fails", said);
        Assert.Equal(18, b.Money("red")); Assert.Equal(6, b.Money("blue"));       // both paid all the same

        b = new PurseBoard(); b.SetMoney("red", 30); b.SetMoney("blue", 9);
        b.Spend("red", EX_DISBAND, "bfield", 15).Spend("blue", EX_COUNTER, "bfield", 3).Go();
        Assert.True(b.U("bfield") < 0);                                           // 15 - 3 still makes the 12 needed
    }

    [Fact]
    public void B2_OfSeveralBribesOnlyTheLargestCountsAndATieSpoilsAll()
    {
        var b = new PurseBoard(); b.SetMoney("red", 30); b.SetMoney("green", 30);
        b.Spend("red", EX_BUY, "bfield", 21).Spend("green", EX_DISBAND, "bfield", 12).Go();
        Assert.True(b.Has("red", 'A', "bfield")); Assert.Equal(18, b.Money("green"));

        b = new PurseBoard(); b.SetMoney("red", 30); b.SetMoney("green", 30);
        b.Spend("red", EX_BUY, "bfield", 18).Spend("green", EX_DISBAND, "bfield", 18).Go();
        Assert.True(b.Has("blue", 'A', "bfield"));
        Assert.Equal(12, b.Money("red")); Assert.Equal(12, b.Money("green"));

        // The largest bribe counts even when it is too small for what it asks, and it still blocks the smaller one.
        b = new PurseBoard(); b.SetMoney("red", 30); b.SetMoney("green", 30);
        b.Spend("red", EX_BUY, "bfield", 15).Spend("green", EX_DISBAND, "bfield", 12).Go();
        Assert.True(b.Has("blue", 'A', "bfield"));
    }

    [Fact]
    public void B3_ABoughtUnitObeysItsBuyerAtOnceAndBringsItsProvince()
    {
        var b = new PurseBoard(); b.SetMoney("red", 18);
        b.Spend("red", EX_BUY, "bfield", 18)
         .Move("bfield", "gfield")                     // Blue's order for the unit it is about to lose
         .Move("bfield", "mid", by: "red");            // Red's order for the unit it is about to own
        b.Go();
        Assert.True(b.Has("red", 'A', "mid"));
        Assert.Equal(b.P("red"), b.Owner("bfield"));   // control changed hands before the unit marched off
        Assert.Equal(b.P("red"), b.Owner("mid"));
        Assert.Equal(b.P("auto"), b.Owner("mid_c"));   // the garrison still holds the city
        // An order written for a unit that is not bought after all is simply ignored.
        b = new PurseBoard(); b.SetMoney("red", 15);
        b.Spend("red", EX_BUY, "bfield", 15).Move("bfield", "mid", by: "red").Go();
        Assert.True(b.Has("blue", 'A', "bfield"));
    }

    [Fact]
    public void B4_GarrisonsAutonomousOrNotAndGreatCitiesCostDouble()
    {
        // Autonomous garrison: 6 to disband, 9 to buy.
        var b = new PurseBoard(); b.SetMoney("red", 30);
        b.Spend("red", EX_BUY, "mid_c", 6).Go();
        Assert.True(b.Has("auto", 'G', "mid_c"));
        b.Spend("red", EX_BUY, "mid_c", 9).Go();
        Assert.True(b.Has("red", 'G', "mid_c"));
        Assert.Equal(b.P("red"), b.Owner("mid")); Assert.Equal(b.P("red"), b.Owner("mid_c")); // an empty province follows its garrison
        Assert.Equal(15, b.Money("red"));
        // Somebody's garrison: 9 turns it autonomous, 12 disbands it.
        b.SetMoney("blue", 30);
        b.Spend("blue", EX_AUTONOMOUS, "mid_c", 9).Go();
        Assert.True(b.Has("auto", 'G', "mid_c")); Assert.Equal(b.P("auto"), b.Owner("mid"));
        b.Spend("blue", EX_DISBAND, "mid_c", 6).Go();
        Assert.True(b.U("mid_c") < 0);
        // In a city worth more than one ducat every price doubles.
        b = new PurseBoard(); b.SetMoney("blue", 60);
        b.Put("red", 'G', "rcap_c");
        Assert.Equal(24, b.pr.MinBribe(b.s, b.P("blue"), EX_DISBAND, b.Loc("rcap_c")));
        Assert.Equal(36, b.pr.MinBribe(b.s, b.P("blue"), EX_BUY, b.Loc("rcap_c")));
        Assert.Equal(0, b.pr.MinBribe(b.s, b.P("red"), EX_DISBAND, b.Loc("rcap_c")));    // not your own
        Assert.Equal(0, b.pr.MinBribe(b.s, b.P("blue"), EX_AUTONOMOUS, b.Loc("rcap")));  // an army cannot be made autonomous
        b.Spend("blue", EX_DISBAND, "rcap_c", 21).Go();
        Assert.True(b.Has("red", 'G', "rcap_c"));
        b.Spend("blue", EX_DISBAND, "rcap_c", 24).Go();
        Assert.True(b.U("rcap_c") < 0);
    }

    [Fact]
    public void B5_WithTheAdjacencyOptionABribeNeedsAUnitNextDoor()
    {
        var b = new PurseBoard(OPT_ADJACENCY); b.SetMoney("red", 40);
        string said = b.Spend("red", EX_DISBAND, "gfield", 12).Go();   // no Red unit touches Greenfields
        Assert.True(b.Has("green", 'A', "gfield")); Assert.Contains("fails", said);
        Assert.Equal(28, b.Money("red"));                              // the money is gone all the same
        b.Put("red", 'A', "pass");
        b.Spend("red", EX_DISBAND, "gfield", 12).Go();
        Assert.True(b.U("gfield") < 0);
        // A garrison is not enough to bribe from, but your own unit can always be counter-bribed.
        b = new PurseBoard(OPT_ADJACENCY); b.SetMoney("red", 40); b.SetMoney("green", 12);
        b.ClearUnits().Put("red", 'G', "mid_c").Put("green", 'A', "gfield").Put("blue", 'A', "bfield");
        b.Spend("red", EX_DISBAND, "gfield", 12).Go();
        Assert.True(b.Has("green", 'A', "gfield"));
        b.Spend("blue", EX_DISBAND, "gfield", 12).Spend("green", EX_COUNTER, "gfield", 3).Go();
        Assert.True(b.Has("green", 'A', "gfield"));
    }

    [Fact]
    public void B6_FourExpensesInOrderAndAnEmptyPurseStopsThem()
    {
        var b = new PurseBoard(); b.SetMoney("red", 13);
        b.Spend("red", EX_DISBAND, "bfield", 12).Spend("red", EX_DISBAND, "bcap", 12).Go();
        Assert.True(b.U("bfield") < 0); Assert.True(b.Has("blue", 'A', "bcap"));
        Assert.Equal(1, b.Money("red"));                 // one ducat is not a bribe
        b = new PurseBoard(); b.SetMoney("red", 100);
        for (int i = 0; i < 5; i++) b.Spend("red", EX_COUNTER, "rcap", 3);
        b.Go();
        Assert.Equal(88, b.Money("red"));                // the fifth expense does not exist
    }

    // =============================================================== C. garrisons and sieges

    [Fact]
    public void C1_ASiegeTakesTwoCampaignsAndDestroysTheGarrison()
    {
        var b = new PurseBoard();
        b.Put("blue", 'A', "mid");
        Assert.Equal(b.P("blue"), b.Owner("mid")); Assert.Equal(b.P("auto"), b.Owner("mid_c"));
        b.Besiege("mid").Go();
        Assert.True(b.Flag("mid", F_SIEGE)); Assert.True(b.Has("auto", 'G', "mid_c"));
        string said = b.Besiege("mid").Go();
        Assert.Contains("siege of Midmarch ends", said);
        Assert.True(b.U("mid_c") < 0); Assert.False(b.Flag("mid", F_SIEGE));
        Assert.Equal(b.P("blue"), b.Owner("mid_c"));    // the city falls to the besieger
    }

    [Fact]
    public void C2_ABesiegerIsPinnedUntilItLiftsTheSiege()
    {
        var b = new PurseBoard();
        b.Put("blue", 'A', "mid");
        b.Besiege("mid").Go();
        b.Move("mid", "pass").Go();                     // "any other order becomes a hold"
        Assert.True(b.Has("blue", 'A', "mid"));
        Assert.False(b.Flag("mid", F_SIEGE));           // and holding is not besieging: the siege has lapsed
        b.Besiege("mid").Go();                          // start again
        b.Lift("mid").Go();
        Assert.False(b.Flag("mid", F_SIEGE));
        b.Move("mid", "pass").Go();
        Assert.True(b.Has("blue", 'A', "pass"));
        Assert.True(b.Has("auto", 'G', "mid_c"));
    }

    [Fact]
    public void C3_AGarrisonHelpsItsRelieversAndADislodgedBesiegerLosesTheSiege()
    {
        var b = new PurseBoard();
        b.Put("red", 'G', "mid_c").Put("blue", 'A', "mid");
        b.Besiege("mid").Go();
        b.Besiege("mid").Move("rfield", "mid").Support("mid_c", "rfield", "mid");
        b.Go();
        Assert.Equal(BoardState.PHASE_RETREAT, b.s.phase);
        Assert.True(b.Has("red", 'A', "mid")); Assert.True(b.Has("red", 'G', "mid_c"));
        Assert.False(b.Flag("mid", F_SIEGE));
        b.Retreat("mid", "bfield");                      // occupied: the retreat fails
        b.Go();
        Assert.Equal(BoardState.PHASE_MOVE, b.s.phase);
        Assert.Equal(1, b.s.CountUnits(b.P("blue")) - 2);  // the besieger is gone; Blue's three other units remain
    }

    [Fact]
    public void C4_ABesiegedGarrisonCannotComeOutButAFreshlyThreatenedOneCan()
    {
        var b = new PurseBoard();
        b.Put("red", 'G', "mid_c").Put("blue", 'A', "mid").Remove("rfield");
        // First campaign of the siege: the garrison may still try (and bounces off the besieger).
        b.Besiege("mid").Convert("mid_c", "mid", 'A').Go();
        Assert.True(b.Has("red", 'G', "mid_c")); Assert.True(b.Flag("mid", F_SIEGE));
        // Now it is besieged: the order becomes a hold, even with help from outside that would have won.
        b.Put("red", 'A', "rfield");
        b.Besiege("mid").Convert("mid_c", "mid", 'A').Support("rfield", "mid_c", "mid").Go();
        Assert.True(b.U("mid_c") < 0);                   // and the siege ran its course
        Assert.True(b.Has("blue", 'A', "mid"));
    }

    [Fact]
    public void C5_YouCannotBesiegeYourselfOrAnEmptyCity()
    {
        var b = new PurseBoard();
        b.Put("red", 'G', "rcap_c");
        b.Besiege("rcap").Besiege("bcap").Go();          // Red's own garrison; Blue's own empty city
        Assert.False(b.Flag("rcap", F_SIEGE)); Assert.False(b.Flag("bcap", F_SIEGE));
        Assert.True(b.Has("red", 'G', "rcap_c"));
        // A unit bought in the same campaign turns the siege of its city into a hold: nothing to besiege any more.
        b = new PurseBoard(); b.SetMoney("blue", 18);
        b.Put("blue", 'A', "mid");
        b.Besiege("mid").Go();
        b.Besiege("mid").Spend("blue", EX_BUY, "mid_c", 9).Go();
        Assert.True(b.Has("blue", 'G', "mid_c")); Assert.False(b.Flag("mid", F_SIEGE));
    }

    [Fact]
    public void C6_AFleetBesiegesAPortAndProvinceAndCityCanHaveDifferentMasters()
    {
        var b = new PurseBoard();
        b.Put("blue", 'G', "bport_c").Put("green", 'F', "bport");
        Assert.Equal(b.P("green"), b.Owner("bport")); Assert.Equal(b.P("blue"), b.Owner("bport_c"));
        // Green collects for the province, Blue for the city inside it.
        int green = b.pr.FixedIncome(b.s, b.P("green")), blue = b.pr.FixedIncome(b.s, b.P("blue"));
        b.Remove("bport"); b.s.owner[b.Prov("bport")] = b.P("blue");
        Assert.Equal(green - 1, b.pr.FixedIncome(b.s, b.P("green")));
        Assert.Equal(blue + 1, b.pr.FixedIncome(b.s, b.P("blue")));
        b.Put("green", 'F', "bport");
        b.Besiege("bport").Go(); b.Besiege("bport").Go();
        Assert.True(b.U("bport_c") < 0);
        Assert.Equal(b.P("green"), b.Owner("bport_c"));
        // When the fleet sails away from an empty city it keeps both; had the garrison lived, the province would follow the garrison.
        b.Move("bport", "bay").Go();
        Assert.Equal(b.P("green"), b.Owner("bport"));
        b = new PurseBoard();
        b.Put("blue", 'G', "bport_c").Put("green", 'F', "bport");
        b.Move("bport", "bay").Go();
        Assert.Equal(b.P("blue"), b.Owner("bport"));
    }

    [Fact]
    public void C7_ADislodgedUnitMayShutItselfInItsCityOnlyAsALastResort()
    {
        var b = new PurseBoard();
        b.ClearUnits().Put("blue", 'A', "bcap").Put("red", 'A', "pass").Put("red", 'A', "bfield").Put("red", 'A', "bport");
        b.Move("pass", "bcap").Support("bfield", "pass", "bcap").Go();
        Assert.Equal(BoardState.PHASE_RETREAT, b.s.phase);
        Assert.True(b.g.Adj.RetreatValid(b.s, 0, b.Loc("bcap_c")));      // every neighbour is closed
        b.Retreat("bcap", "bcap_c").Go();
        Assert.True(b.Has("blue", 'G', "bcap_c")); Assert.True(b.Has("red", 'A', "bcap"));
        Assert.Equal(b.P("blue"), b.Owner("bcap_c")); Assert.Equal(b.P("red"), b.Owner("bcap"));
        // With open country to run to, the city gate is not an option.
        b = new PurseBoard();
        b.ClearUnits().Put("blue", 'A', "bcap").Put("red", 'A', "pass").Put("red", 'A', "bfield");
        b.Move("pass", "bcap").Support("bfield", "pass", "bcap").Go();
        Assert.False(b.g.Adj.RetreatValid(b.s, 0, b.Loc("bcap_c")));
        Assert.True(b.g.Adj.RetreatValid(b.s, 0, b.Loc("bport")));
    }

    // =============================================================== D. rebellions

    [Fact]
    public void D1_ARebellionCostsMoreInTheEnemysHomeCountry()
    {
        var b = new PurseBoard(); b.SetMoney("red", 60);
        Assert.Equal(15, b.pr.RebellionCost(b.s, b.P("red"), b.Prov("bfield")));
        Assert.Equal(0, b.pr.RebellionCost(b.s, b.P("red"), b.Prov("rfield")));     // not against yourself
        Assert.Equal(0, b.pr.RebellionCost(b.s, b.P("red"), b.Prov("pass")));       // nobody rules it
        b.Spend("red", EX_REBEL, "bfield", 9).Go();
        Assert.False(b.Flag("bfield", F_REBEL)); Assert.Equal(51, b.Money("red"));
        b.Spend("red", EX_REBEL, "bfield", 15).Go();
        Assert.True(b.Flag("bfield", F_REBEL));
        // A conquered province is cheaper to raise.
        b.Put("blue", 'A', "pass");
        Assert.Equal(9, b.pr.RebellionCost(b.s, b.P("red"), b.Prov("pass")));
        // Rebels take an ungarrisoned city with the countryside, but never a garrisoned one.
        b = new PurseBoard(); b.SetMoney("red", 60);
        b.Put("blue", 'G', "bport_c").Remove("bport");
        b.Spend("red", EX_REBEL, "bcap", 15).Spend("red", EX_REBEL, "bport", 15).Go();
        Assert.True(b.Flag("bcap", F_REBEL)); Assert.True(b.Flag("bcap_c", F_REBEL));
        Assert.True(b.Flag("bport", F_REBEL)); Assert.False(b.Flag("bport_c", F_REBEL));
        Assert.Equal(b.P("blue"), b.Owner("bcap"));     // control does not change
    }

    [Fact]
    public void D2_RebelsFightForOneOutsiderButNotForTwo()
    {
        var b = new PurseBoard();
        b.s.provFlag[b.Prov("bfield")] = F_REBEL;
        b.Put("red", 'A', "mid");
        b.Move("mid", "bfield").Go();                    // 1 + the rebels against 1 (the loser has nowhere to go)
        Assert.True(b.Has("red", 'A', "bfield"));
        Assert.False(b.Flag("bfield", F_REBEL));         // liberated by the newcomer

        b = new PurseBoard();
        b.s.provFlag[b.Prov("bfield")] = F_REBEL;
        b.Put("red", 'A', "mid");
        b.Move("mid", "bfield").Move("gfield", "bfield").Support("bfield", "bcap").Go();   // two suitors: the rebels help neither
        Assert.True(b.Has("blue", 'A', "bfield")); Assert.True(b.Flag("bfield", F_REBEL));
        // The ruler's own troops get no help from people who are rebelling against him: the one outsider beats them to it.
        b = new PurseBoard();
        b.Remove("bfield");
        b.s.provFlag[b.Prov("bfield")] = F_REBEL;
        b.Move("bcap", "bfield").Move("gfield", "bfield").Go();
        Assert.True(b.Has("green", 'A', "bfield")); Assert.True(b.Has("blue", 'A', "bcap"));
    }

    [Fact]
    public void D3_TheRulerPutsRebelsDownByStandingStillAndACityBySiege()
    {
        var b = new PurseBoard();
        b.s.provFlag[b.Prov("rcap")] = F_REBEL; b.s.provFlag[b.Prov("rcap_c")] = F_REBEL;
        Assert.Equal(1 + 1 + 1, b.pr.FixedIncome(b.s, b.P("red")));    // Redfields, Redhaven and its open city; nothing from rebel Redkeep
        b.Support("rcap", "rfield").Go();                // supporting is not holding
        Assert.True(b.Flag("rcap", F_REBEL));
        string said = b.Hold("rcap").Go();
        Assert.Contains("put down", said);
        Assert.False(b.Flag("rcap", F_REBEL)); Assert.True(b.Flag("rcap_c", F_REBEL));
        Assert.Equal(4, b.pr.FixedIncome(b.s, b.P("red")));            // the province pays again, the city not yet
        b.Convert("rcap", "rcap_c", 'G').Go();           // the gates are shut
        Assert.True(b.Has("red", 'A', "rcap"));
        b.Besiege("rcap").Go(); b.Besiege("rcap").Go();
        Assert.False(b.Flag("rcap_c", F_REBEL));
        Assert.Equal(6, b.pr.FixedIncome(b.s, b.P("red")));
        // Twelve ducats do the same without soldiers.
        b = new PurseBoard(); b.SetMoney("red", 12);
        b.s.provFlag[b.Prov("rcap")] = F_REBEL; b.s.provFlag[b.Prov("rcap_c")] = F_REBEL;
        b.Spend("red", EX_PACIFY, "rcap", 12).Go();
        Assert.False(b.Flag("rcap", F_REBEL)); Assert.False(b.Flag("rcap_c", F_REBEL));
    }

    // =============================================================== E. assassins

    [Fact]
    public void E1_AnAssassinNeedsATokenAndLuck()
    {
        var b = new PurseBoard(); b.SetMoney("red", 100); b.SetMoney("blue", 12);
        Assert.Equal(1, b.pr.Chits(b.s, b.P("red"), b.P("blue")));
        string miss = b.SeedWhere(roll => roll("kill.0.1.0") == 3);     // 24 ducats: succeeds on 1 or 2
        string said = b.Kill("red", "blue", 24).Move("bfield", "gfield").Go(miss);
        Assert.Contains("fails", said); Assert.Equal(76, b.Money("red"));
        Assert.Equal(0, b.pr.Chits(b.s, b.P("red"), b.P("blue")));      // the token is spent either way
        said = b.Kill("red", "blue", 36).Go(miss);
        Assert.Contains("no assassin", said);
        // A token can be handed on; the receiver may use it in the same campaign.
        b = new PurseBoard(); b.SetMoney("green", 36);
        string hit = b.SeedWhere(roll => roll("kill.2.1.0") == 3 && roll("rise.1.bcap") > 1 && roll("rise.1.bfield") > 2 && roll("rise.1.bport") > 1);
        b.Token("red", "blue", "green").Kill("green", "blue", 36);      // 36 ducats: succeeds on 1 to 3
        b.Move("bfield", "mid").Spend("blue", EX_COUNTER, "bcap", 3);
        b.SetMoney("blue", 3);
        said = b.Go(hit);
        Assert.Contains("is assassinated", said);
        Assert.Equal(0, b.pr.Chits(b.s, b.P("red"), b.P("blue"))); Assert.Equal(1, b.pr.Chits(b.s, b.P("green"), b.P("blue")));
        Assert.True(b.Has("blue", 'A', "bfield"));       // the dead ruler's orders are not carried out ...
        Assert.Equal(0, b.Money("blue"));                // ... but his expenses were
        // With the option off nobody can be assassinated for money.
        b = new PurseBoard(OPT_LOANS); b.SetMoney("red", 36);
        Assert.Contains("no assassin", b.Kill("red", "blue", 36).Go());
    }

    [Fact]
    public void E2_AMurderedRulersLandsMayRiseAndHisBesiegedGarrisonsGiveUp()
    {
        var b = new PurseBoard(); b.SetMoney("red", 36);
        b.Put("blue", 'G', "mid_c").Put("green", 'A', "mid").Put("blue", 'A', "pass");
        b.Besiege("mid").Go();
        Assert.True(b.Flag("mid", F_SIEGE));
        // home province with troops rebels on 1, without on 1-2; conquered with troops on 1-3, without on 1-5
        string seed = b.SeedWhere(roll => roll("kill.0.1.0") <= 3 && roll("rise.1.bcap") == 2 && roll("rise.1.bfield") == 1 && roll("rise.1.bport") == 2 && roll("rise.1.pass") == 3);
        b.Remove("bport");
        string said = b.Kill("red", "blue", 36).Besiege("mid").Go(seed);
        Assert.False(b.Flag("bcap", F_REBEL));           // home, garrisoned by an army, rolled 2
        Assert.True(b.Flag("bfield", F_REBEL));          // home with troops, rolled 1
        Assert.True(b.Flag("bport", F_REBEL));           // home without troops, rolled 2
        Assert.True(b.Flag("pass", F_REBEL));            // conquered with troops, rolled 3
        Assert.Contains("surrenders", said);
        Assert.True(b.U("mid_c") < 0);
        Assert.Equal(b.P("green"), b.Owner("mid_c"));
    }

    // =============================================================== F. famine and plague

    [Fact]
    public void F1_FamineIsRolledInTheFallAndBitesInTheSpring()
    {
        var b = new PurseBoard(OPT_FAMINE, season: 2);
        b.SameDate = false;
        // a row-only year (4 or 6 on two dice), row 7: Midmarch, Redkeep, Greenfields
        string seed = b.SeedWhere(roll => (roll("famine.year.a") + roll("famine.year.b") == 4 || roll("famine.year.a") + roll("famine.year.b") == 6) && roll("famine.row.a") + roll("famine.row.b") == 7);
        b.SetMoney("red", 0); b.SetMoney("green", 0);
        string said = b.Go(seed);
        Assert.Contains("Famine will strike next year: ", said);
        Assert.True(b.Flag("mid", F_FAMINE)); Assert.True(b.Flag("rcap", F_FAMINE)); Assert.True(b.Flag("gfield", F_FAMINE));
        Assert.False(b.Flag("rfield", F_FAMINE));
        Assert.Equal("W1400A", b.Label);
        // Income (1401 is odd: the fixed roll is 4): Red loses Redkeep's province and city income.
        Assert.Equal(2 + 1 + 4, b.Money("red"));          // two provinces, the open city, and 4 on its income line
        Assert.Equal(1 + 3 + 2 + 2, b.Money("green"));     // Greenkeep and its city, two dice worth 2 each; nothing from Greenfields
        // Nothing can be built in a starving province.
        b.SetMoney("red", 30);
        b.Remove("rcap").Build("red", 'A', "rcap").Go();
        Assert.True(b.U("rcap") < 0);
        Assert.Equal("S1401M", b.Label);
        // Spring: Red buys relief; Green marches out of the famine; Blue marches into it.
        b.Put("red", 'A', "rcap");
        b.Spend("red", EX_RELIEF, "rcap", 3).Move("gfield", "pass").Move("bfield", "mid");
        said = b.Go(b.SeedWhere(roll => true));
        Assert.True(b.Has("red", 'A', "rcap")); Assert.True(b.Has("green", 'A', "pass"));
        Assert.True(b.U("mid") < 0); Assert.True(b.U("mid_c") < 0);    // the newcomer and the garrison both starve
        Assert.Contains("starves", said);
        Assert.False(b.Flag("mid", F_FAMINE));           // and with that the famine is over
    }

    [Fact]
    public void F2_AGarrisonedCityPaysEvenInAFamine()
    {
        var b = new PurseBoard();
        b.Put("red", 'G', "rcap_c");
        int before = b.pr.FixedIncome(b.s, b.P("red"));
        b.s.provFlag[b.Prov("rcap")] = F_FAMINE;
        Assert.Equal(before - 1, b.pr.FixedIncome(b.s, b.P("red")));   // only the province's ducat is lost
        b.Remove("rcap_c");
        Assert.Equal(before - 3, b.pr.FixedIncome(b.s, b.P("red")));
    }

    [Fact]
    public void F3_PlagueStrikesAtOnceWhenTheSpringSheetsAreOpened()
    {
        var b = new PurseBoard(OPT_PLAGUE);
        string seed = b.SeedWhere(roll => (roll("plague.year.a") + roll("plague.year.b") == 5 || roll("plague.year.a") + roll("plague.year.b") == 7) && roll("plague.col.a") + roll("plague.col.b") == 7);
        // column 7 of the plague table: Bluekeep and Midmarch. Red walks into Midmarch this very campaign.
        b.Move("rfield", "mid");
        string said = b.Go(seed);
        Assert.Contains("Plague strikes: ", said);
        Assert.True(b.U("bcap") < 0); Assert.True(b.U("mid") < 0); Assert.True(b.U("mid_c") < 0);
        Assert.True(b.Has("blue", 'A', "bfield"));
        Assert.Equal(0, b.s.provFlag[b.Prov("mid")]);    // plague leaves no mark
        // Summer and fall have no plague.
        b = new PurseBoard(OPT_PLAGUE, season: 1);
        Assert.DoesNotContain("lague", b.Go(seed));
    }

    // =============================================================== G. control, elimination, victory

    [Fact]
    public void G1_ControlFollowsTheLastTroopsToPassThrough()
    {
        var b = new PurseBoard();
        b.Move("rfield", "pass").Go();
        Assert.Equal(b.P("red"), b.Owner("pass")); Assert.Equal(b.P("red"), b.Owner("rfield"));
        b.Move("pass", "mid").Go();
        Assert.Equal(b.P("red"), b.Owner("pass"));       // an empty province remembers its last visitor
        Assert.Equal(b.P("red"), b.Owner("mid")); Assert.Equal(b.P("auto"), b.Owner("mid_c"));
        b.Move("mid", "pass").Go();
        Assert.Equal(b.P("auto"), b.Owner("mid"));       // but a garrisoned province goes back to the garrison
    }

    [Fact]
    public void G2_AHomeCountryIsLostWholeAndItsRulerWithIt()
    {
        var b = new PurseBoard();
        b.ClearUnits();
        b.Put("blue", 'A', "pass").Put("green", 'A', "gcap");
        b.Put("red", 'A', "bcap").Put("red", 'A', "bfield").Put("red", 'A', "rfield");
        b.Put("blue", 'G', "bport_c");
        string said = b.Go();
        Assert.True(b.pr.Alive(b.s, b.P("blue")));       // Blue still holds Blueport's city
        Assert.Equal(b.P("blue"), b.pr.HomeHolder(b.s, b.P("blue")));
        b.Remove("bport_c").Put("red", 'F', "bport");
        said = b.Go();
        Assert.Contains("out of the game", said); Assert.Contains("now holds the whole", said);
        Assert.False(b.pr.Alive(b.s, b.P("blue")));
        Assert.True(b.U("pass") < 0);                    // its army is removed
        Assert.Equal(-1, b.Owner("pass"));               // and what it held elsewhere belongs to nobody
        Assert.Equal(b.P("red"), b.pr.HomeHolder(b.s, b.P("blue")));
        Assert.Equal(2, b.pr.HomesHeld(b.s, b.P("red")));
        Assert.False(b.g.Rules.NeedsOrders(b.s, b.P("blue")));
        // Red now collects Blue's variable income as well, and may build in Blue's cities.
        Assert.True(b.pr.BuildOk(b.s, b.P("red"), MapData.GARRISON, 0, b.Loc("bcap_c")));
    }

    [Fact]
    public void G3_SwappingHomeCountriesInOneCampaignEliminatesBoth()
    {
        // The judge rules warn: take a home country in the campaign in which you lose your own, and you are out.
        var b = new PurseBoard();
        b.ClearUnits();
        b.Put("red", 'A', "bcap").Put("red", 'A', "bfield").Put("red", 'A', "bport");
        b.Put("blue", 'A', "rcap").Put("blue", 'A', "rfield").Put("blue", 'A', "rtown");
        b.Put("green", 'A', "gcap");
        string said = b.Go();
        Assert.False(b.pr.Alive(b.s, b.P("red"))); Assert.False(b.pr.Alive(b.s, b.P("blue")));
        Assert.Equal(BoardState.PHASE_FINISHED, b.s.phase);
        Assert.Equal(b.P("green"), b.s.winner);          // the last ruler standing
    }

    [Fact]
    public void G4_VictoryNeedsCitiesAndTwoHomeCountries()
    {
        var b = new PurseBoard();
        b.ClearUnits();
        b.Put("blue", 'A', "bcap").Put("green", 'G', "gcap_c");
        // Red: Redkeep, Redhaven, Midmarch, Lagoon Isle, Blueport = 5 cities, but only its own home country.
        b.Put("red", 'A', "rcap").Put("red", 'A', "mid").Put("red", 'F', "isle").Put("red", 'F', "bport");
        b.Go();
        Assert.Equal(5, b.pr.CitiesHeld(b.s, b.P("red")));
        Assert.Equal(BoardState.PHASE_MOVE, b.s.phase);
        // Take all of Green's home as well and the game is over.
        b.Remove("gcap_c").Put("red", 'A', "gcap").Put("red", 'A', "gfield");
        b.Go();
        Assert.Equal(BoardState.PHASE_FINISHED, b.s.phase);
        Assert.Equal(b.P("red"), b.s.winner);
    }

    // =============================================================== V. where city and province are one space

    [Fact]
    public void V1_OneSpaceOneUnitAndAGarrisonThereCannotBeTaken()
    {
        var b = new PurseBoard();
        b.Put("blue", 'F', "isle").Put("red", 'F', "bay").Put("red", 'F', "sound");
        Assert.False(b.m.CanStand(MapData.GARRISON, b.Loc("rcap")));
        // The fleet turns into a garrison on the spot; an attack of two cannot shift a garrison.
        b.Convert("isle", "isle", 'G').Go();
        Assert.True(b.Has("blue", 'G', "isle"));
        b.Move("bay", "isle").Support("sound", "bay", "isle").Go();
        Assert.True(b.Has("blue", 'G', "isle")); Assert.Equal(BoardState.PHASE_MOVE, b.s.phase);
        // It cannot come out while under attack ...
        string said = b.Convert("isle", "isle", 'F').Move("bay", "isle").Go();
        Assert.True(b.Has("blue", 'G', "isle")); Assert.Contains("cannot come out", said);
        // ... and once out, it is as vulnerable as any fleet: converting back does not save it from a stronger attack.
        b.Convert("isle", "isle", 'F').Go();
        Assert.True(b.Has("blue", 'F', "isle"));
        b.Convert("isle", "isle", 'G').Move("bay", "isle").Support("sound", "bay", "isle").Go();
        Assert.True(b.Has("red", 'F', "isle"));
        Assert.Equal(0, b.s.disCount);                   // nowhere to retreat to: destroyed
        Assert.Equal(b.P("red"), b.Owner("isle"));
    }

    [Fact]
    public void V2_RebelsInTheOneSpaceKeepTheirRulerOut()
    {
        var b = new PurseBoard(); b.SetMoney("red", 30);
        b.Put("green", 'F', "isle"); b.Move("isle", "sound").Go();
        Assert.Equal(b.P("green"), b.Owner("isle"));
        b.Spend("red", EX_REBEL, "isle", 9).Go();
        Assert.True(b.Flag("isle", F_REBEL));
        Assert.Equal(0, b.pr.FixedIncome(b.s, b.P("green")) - 5 - 1);   // Greenkeep 1+3, Greenfields 1, the fleet's sea 1; nothing from the isle
        b.Move("sound", "isle").Go();                    // the ruler's own fleet is kept out
        Assert.True(b.Has("green", 'F', "sound"));
        b.Put("blue", 'F', "bay");
        b.Move("bay", "isle").Go();                      // anybody else is welcomed
        Assert.True(b.Has("blue", 'F', "isle")); Assert.False(b.Flag("isle", F_REBEL));
        // No rebellion can be raised there while a unit stands in it.
        Assert.Equal(0, b.pr.RebellionCost(b.s, b.P("red"), b.Prov("isle")));
    }

    // =============================================================== S. straits

    [Fact]
    public void S1_AFleetInTheGuardProvinceClosesTheStrait()
    {
        var b = new PurseBoard();
        b.Put("red", 'F', "bay");
        b.Move("bay", "sound").Go();                     // Blue's fleet in Blueport bars the way between Bay and Sound
        Assert.True(b.Has("red", 'F', "bay"));
        b.Remove("bport").Put("blue", 'A', "bport");     // an army does not
        b.Move("bay", "sound").Go();
        Assert.True(b.Has("red", 'F', "sound"));
        b.Remove("bport").Put("red", 'F', "bport").Put("red", 'F', "bay").Remove("sound");
        b.Move("bay", "sound").Go();                     // your own fleet lets you through
        Assert.True(b.Has("red", 'F', "sound"));
    }

    // =============================================================== H. the year, the dice, the whole stack

    [Fact]
    public void H1_AYearIsThreeCampaignsThenIncomeAndAdjustments()
    {
        var g = Game.FromPmap(PurseBoard.Valleys); var f = g.Flow;
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        Assert.False(f.ClaimSeat(3, "Dee"));             // nobody plays the autonomous garrisons
        f.SetMoveSchedule(new[] { 100, 200, 300, 400 }, 4);
        Assert.True(f.StartGame(0));
        var labels = new List<string>(); var dice = new List<bool>();
        for (int i = 0; i < 6; i++)
        {
            labels.Add(g.State.PhaseLabel()); dice.Add(g.Rules.UsesDice(g.State));
            f.diceNoise = "n" + i;
            f.ForceNext(0);
        }
        Assert.Equal(new[] { "W1399A", "S1400M", "U1400M", "F1400M", "W1400A", "S1401M" }, labels);
        Assert.Equal(new[] { false, true, true, true, false, true }, dice);
        Assert.Equal(400, f.SecondsLeft(0));             // the clock schedule counts campaigns
        // nobody paid in the first winter (empty sheets pay automatically while the money lasts)
        Assert.Equal(3, g.State.CountUnits(0));
    }

    [Fact]
    public void H2_ASealedGameRollsItsDiceFromEverybodysSecrets()
    {
        var g = Game.FromPmap(PurseBoard.Valleys); var f = g.Flow;
        f.sealedOrders = true; f.SetTimers(900, 0, 0); f.revealSeconds = 20;
        f.Stir(SealedDevice.NewSalt());
        f.ClaimSeat(0, "Ada"); f.ClaimSeat(1, "Bo"); f.ClaimSeat(2, "Cy");
        var dev = new[] { new SealedDevice(g, 0), new SealedDevice(g, 1), new SealedDevice(g, 2) };
        f.StartGame(0);
        float t = 0;
        void Round(int skipOpen = -1)
        {
            foreach (var d in dev) d.SubmitOn();
            t += 1; f.Tick(t);
            for (int p = 0; p < 3; p++) if (p != skipOpen) dev[p].React();
            t += 1; f.Tick(t);
            if (skipOpen >= 0) { t += 30; f.Tick(t); }
        }
        Round();                                         // the first winter: no dice
        Assert.Equal("S1400M", g.State.PhaseLabel()); Assert.Equal("", f.lastDiceSeed);
        Assert.True(f.VerifyLast(), f.error);
        // Spring: an assassination attempt, decided by dice that all three devices contributed to.
        g.Purse.SetTreasury(g.State, 0, 36);
        dev[0].Sheet = "!" + g.Purse.ExpenseItem(EX_ASSASSINATE, 1, 36);
        string key = f.SealKey();
        Round();
        Assert.Equal("U1400M", g.State.PhaseLabel());
        Assert.Equal(64, f.lastDiceSeed.Length);
        Assert.True(f.VerifyLast(), f.error);
        Assert.Equal(0, g.Purse.Treasury(g.State, 0));
        Assert.Equal(0, g.Purse.Chits(g.State, 0, 1));
        // Anybody can redo the roll from what was published.
        string seed = g.Seal.Sha256Hex("PD1|seed|" + key + "|0:" + f.lastSalt[0] + "|1:" + f.lastSalt[1] + "|2:" + f.lastSalt[2]);
        Assert.Equal(seed, f.lastDiceSeed);
        var check = new Dice(); check.Init(g.Seal); check.Begin(seed);
        Assert.Equal(g.Dice.rollValue[0], check.Roll(6, g.Dice.rollLabel[0]));
        Assert.Equal("kill.0.1.0", g.Dice.rollLabel[0]);
        // A device that does not open changes the dice, loses its whole turn, and is counted.
        dev[0].Sheet = "";
        string with = f.SealKey();
        Round(skipOpen: 2);
        Assert.Equal(1, f.unopened[2]);
        Assert.True(f.VerifyLast(), f.error);
        Assert.Equal(g.Seal.Sha256Hex("PD1|seed|" + with + "|0:" + f.lastSalt[0] + "|1:" + f.lastSalt[1]), f.lastDiceSeed);
    }

    [Fact]
    public void H3_RandomGamesOnTheTestBoardHoldTogether()
    {
        var rep = PurseSim.Run(PurseBoard.Valleys, 40, 11, 12);
        Assert.True(rep.Problems.Count == 0, string.Join("\n", rep.Problems));
        Assert.True(rep.Finished > 0);
        foreach (var seen in new[] { "bribed to disband", "siege of", "rises in rebellion", "now holds", "out of the game", "Borrows" })
            Assert.True(rep.Seen.ContainsKey(seen), "random play never produced: " + seen);
    }

    [Fact]
    public void H4_TheImportedItalyBoardPlaysIfItHasBeenFetched()
    {
        string file = Path.Combine(Game.RepoRoot(), "maps-local", "italy1454.pmap");
        if (!File.Exists(file)) return; // run scripts/fetch-reference-maps.sh to get it
        var g = Game.FromPmap(File.ReadAllText(file));
        Assert.Equal(81, g.Map.landCount); Assert.Equal(9, g.Map.powerCount);
        Assert.Equal("W1453A", g.State.PhaseLabel());
        Assert.Equal(39, g.State.unitCount);
        Assert.Equal(12, g.State.CountUnits(g.Purse.autoPower));
        for (int p = 0; p < 8; p++) Assert.True(g.Purse.Treasury(g.State, p) >= 3 * g.State.CountUnits(p), g.Map.powerName[p] + " cannot pay for its starting units");
        var rep = PurseSim.Run(File.ReadAllText(file), 6, 4, 8);
        Assert.True(rep.Problems.Count == 0, string.Join("\n", rep.Problems));
    }
}
