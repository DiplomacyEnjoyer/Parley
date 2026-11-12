# Purse and Dagger: rules reference and rulings

"Purse and Dagger" is Parley's Renaissance mode: the mechanics of the board game
*Machiavelli* (money, bribes, garrisons and sieges, rebellions, assassins, famine and
plague) as a ruleset module (`engine/Parley.Engine/PurseRules.cs`).

- **The name is ours.** *Machiavelli* is a trademark of its publisher; the mode has its
  own name, uses none of the rulebook's text, and Parley ships no picture of the board.
- **The reference** was the rules text the e-mail judges published for their games
  (`rules.machiavelli` in the nJudge archive; fetched by the setup script into
  `third_party/njudge/` for anyone who wants to read along). The Diplomatic Pouch
  article series and diplom.org could not be reached from the machine this was written
  on. The open-source Condottieri project is AGPL-3.0, so nothing was taken from it.
- **There is no DATC for this game.** `tests/Parley.Tests/PurseTests.cs` holds 38 cases
  of our own in the same spirit; the case numbers below (A.1, C.3 ...) refer to them.
  A random-play harness (`parley purse-sim`) checks thousands of phases for impossible
  positions, save/load and exact replay.

Where the judge rules were silent or unclear, or where this engine differs on
purpose, there is a numbered ruling below. Rulings marked **(differs)** depart from the
judge rules deliberately.

## The year

| Step | What happens |
|---|---|
| Spring, Summer, Fall | Three campaigns. In each: everyone writes one sealed sheet (money orders, up to four expenses, unit orders); then retreats. |
| Winter | Income, then the adjustment phase: every unit must be paid for (3 ducats), new units may be bought. |

When a campaign's sheets are opened: **1** repayments to the bank, gifts and tokens
between players, new loans, the bank collects; **2** expenses; **3** unit orders;
**4** sieges, rebels, control, then (spring only) famine deaths and plague; **5** retreats;
then eliminations, home countries, victory.

1. **The game opens in the winter before the first spring** (as the judge does): opening
   income, then everybody pays for the units they keep. *Case A.1.*
2. **The opening income uses no dice** (nobody has sealed anything yet, so there is
   nothing fair to roll with): each variable-income die counts as 3 in an even year and
   4 in an odd one, the judge's own "no dice" values. No famine before the first spring.
3. **(differs) Dice are rolled only when a campaign's sheets are opened.** Famine and
   the variable-income dice for the coming year are rolled when the **Fall** campaign
   resolves; they are therefore known during Fall retreats as well as in the winter
   (the board game rolls them at the start of the winter steps). Plague is rolled and
   strikes when the **Spring** campaign resolves (the board game: at the start of
   Summer). Reason: a dice roll in a phase where some player has nothing to lose can
   be re-rolled for free by that player refusing to open their seal; in a campaign
   everybody has a turn at stake. See `docs/secret-orders-and-dice.md`. *Cases F.1, F.3, H.2.*
4. **Famine kills after the spring moves, before the spring retreats.** A unit that
   marched out is safe; a unit that marched in dies; a garrison in the province's city
   dies too. Units that were dislodged in that campaign are spared (they are leaving).
   After that the famine is over. *Case F.1.*
5. **How bad a year is** (two dice): 2-3 nothing; 4 or 6 one row of the table; 5 or 7
   one column; 8-12 a row and a column. This is the judge's coding; its rules note
   that the original game had 4 and 7 for rows, 5 and 6 for columns.

## Money

6. **Bank and gifts, in this order:** repayments; tokens and gifts (all gifts leave
   before any arrives, so money received cannot be passed on in the same campaign);
   new loans; then the bank collects what is due. A power that owes the bank anything
   cannot give money away. *Cases A.5, A.6.*
7. **Loans.** Up to 25 ducats of principal outstanding; one year at 20% or two years at
   50%, interest rounded up; due in the same campaign one or two years later; at most
   three loans at a time and one new loan of each length per campaign (our limits).
   Repayments go to the loan that falls due first, interest before principal. *Case A.4.*
8. **Default.** A power that cannot pay a due loan in full loses its whole treasury to
   the bank, the debt is written off, its ruler is assassinated (ruling 33), its
   expenses for that campaign do not happen, and it never gets credit again. *Case A.5.*
9. **Paying for expenses.** Bribes and counter-bribes are rounded down to a multiple of
   3, assassinations to 12, 24 or 36. If the treasury is short, what is left is spent.
   An expense that fails or was impossible costs the same. At most four per campaign,
   carried out in the order written. *Case B.6.*
10. **(differs) Units are kept unless you dismiss them.** The judge wants every unit
    explicitly maintained or disbanded. Here an empty sheet pays for everything it
    can: dismissals first, then every remaining unit in board order while the money
    lasts (the unpaid go home), then new units in the order written. A player who
    wants to choose which unit to lose dismisses it. *Case A.2.*
11. **Builds:** only in a home country you hold, in a province you control that has a
    city; an army or fleet in the empty province, or a garrison in the empty walled
    city; fleets only at ports; one new unit per province per winter; not in a famine;
    not in a rebelling province (a garrison may still be built in a city that is not
    itself in rebellion); an army or fleet may not be dismissed and replaced by the
    other on the spot, but a garrison may make way. *Case A.3.*

Income: 1 ducat per controlled province, the city's number for each controlled city,
1 per sea your fleet stands in, plus the variable income of each home country you hold
(and of special cities, by city control). A province in famine or rebellion pays
nothing; nor does its city, unless a garrison holds the city. *Cases C.6, D.3, F.2.*

## Expenses

| Expense | Least cost | Notes |
|---|---|---|
| Famine relief | 3 | Removes the famine from a province (before it kills). |
| Pacify rebellion | 12 | Province and city. |
| Counter-bribe | 3, 6, 9 ... | Subtracted from the winning bribe on that unit. |
| Disband autonomous garrison | 6 | |
| Buy autonomous garrison | 9 | |
| Somebody's garrison turns autonomous | 9 | |
| Disband somebody's garrison, army or fleet | 12 | |
| Buy somebody's army, fleet or garrison | 18 | Type and place unchanged. |
| Raise a rebellion | 9, or 15 in the ruler's home country | |
| Assassination | 12, 24 or 36 | Needs a token against that ruler; succeeds on a die roll of 1, 1-2, 1-3. |

12. **Several bribes on one unit:** only the largest counts; equal largest bribes all
    fail. The counter-bribes are then subtracted from it, and what is left must reach
    the least cost **of what that bribe ordered**: 15 ducats to *buy* fail even though
    they would have been enough to disband, and they still shut out a smaller bribe.
    *Cases B.1, B.2.*
13. **Doubling.** Garrisons in a city worth more than one ducat cost double; so do
    citizen militia and elite professionals (both together: four times). *Case B.4.*
14. **The adjacency option** (on for the Italy board, as on the judges): a bribe, a
    counter-bribe for another power's unit, or a rebellion needs one of your armies or
    fleets in the target's province or in one that touches it by land or water; for
    relief and pacifying a garrison will do too. Your own unit can always be
    counter-bribed. Assassination needs nothing nearby. *Case B.5.*
15. **A bought unit obeys its buyer in the same campaign.** Unit orders are read after
    the expenses, by ownership at that moment; the seller's orders for it are ignored.
    The unit brings control of its province with it at once, before it marches. If it
    was bought from a ruler whose province was in rebellion, the rebellion ends. *Case B.3.*
16. **(differs) Every expense becomes public** once the sheets are opened, including
    failed bribes and failed assassination attempts. The judge tells only the people
    concerned. With sealed sheets there is no neutral party who could keep a secret.

## Garrisons, conversions, sieges

17. **A walled city is a space of its own inside its province** (the judge's own
    reading). Converting is a move between the two, so the ordinary rules decide it:
    a garrison coming out bounces off a unit coming in with equal strength, units
    cannot swap places, a unit dislodged by a garrison that came out cannot retreat
    into that city. *Cases in `RulesetTests`.*
18. **A garrison is never pushed out by force.** A unit cannot convert into a city that
    already holds a garrison (the order is void). Garrisons end by siege, bribe,
    famine, plague or lack of pay.
19. **A garrison may** hold, convert, or support a unit holding in or moving into the
    province around its city. Nothing else.
20. **Siege.** An army, or a fleet at a port, standing in the province orders "besiege"
    in two campaigns running. After the first the siege is under way; after the second
    the garrison is destroyed and the city belongs to the besieger. *Cases C.1, C.6.*
21. **A besieger is pinned.** With a siege under way its only orders are besiege, hold
    and lift siege; anything else becomes a hold. A hold (ordered or not) ends the
    siege without result, as lifting does; after either, the unit is free again the
    next campaign. A siege also ends if the besieger is dislodged or removed, or if
    there is nothing left to besiege. *Cases C.2, C.3.*
22. **A besieged garrison cannot convert** once the siege is under way; in the first
    campaign of a siege it still can. It can always support. *Case C.4.*
23. **Besieging your own garrison, or an empty loyal city, is a hold.** So is the second
    besiege order if you bought the garrison in that same campaign. *Case C.5.*
24. **Retreat into your own province's city** (becoming a garrison) only when there is
    no other retreat; fleets only at ports; never into a rebelling city. *Case C.7.*
25. **Not implemented:** the judge's rule that a strength-2 unit "cannot dislodge any
    besieger in the second siege season". Its meaning is unclear, and it only matters
    with the optional special units.

## Rebellions

26. **Where.** In a province controlled by another living power: 15 ducats if the
    province is part of a home country that this power holds, 9 otherwise. Not where
    another power's unit stands in the province (it would put the rebels down at
    once), not where rebels already are. *Case D.1.*
27. **What rebels take.** The province, and its walled city if no garrison holds it.
    A garrisoned city never rebels. Control does not change. *Case D.1.*
28. **Rebels fight for an outsider:** a unit of any power other than the ruler moving
    into the province (or a garrison coming out of its city into it) counts one
    stronger, provided units of only **one** such power are trying; with two, neither is
    helped. *Case D.2.*
29. **Putting rebels down.** In the province: the ruler's army or fleet there holds for
    a campaign and is not dislodged. Supporting, besieging or converting is not
    holding; a murdered ruler's troops cannot do it. In the city: a two-campaign siege
    by the ruler, which clears the province as well. Or 12 ducats. *Case D.3.*
30. **(ruling) It takes a whole campaign.** A rebellion cannot be put down in the
    campaign in which it broke out. Otherwise any idle unit, which holds by default,
    would end a rebellion before it had any effect.
31. **Liberation.** Any other power's army or fleet that ends a campaign in the
    province ends the rebellion in province and city. *Case D.2.*
32. The ruler cannot convert into, or retreat into, a rebelling city.

## Assassination

33. **Effects.** For that campaign all the victim's units hold (others may still
    support them); those holds do not put down rebellions; the victim's sieges are
    broken; the victim's own expenses still happen (unless it was the bank that killed
    him); each of his garrisons under a siege that was already under way surrenders,
    unless the besieger's ruler died too; and each province he controls may rebel on
    one die: home province with troops on 1, without on 1-2; conquered province with
    troops on 1-3, without on 1-5. "With troops" means an army or fleet in the
    province or a garrison in its city. *Cases E.1, E.2.*
34. **Tokens.** Each power starts with one token against each rival. An attempt uses the
    token up, successful or not. A token handed to another power arrives before the
    expenses of the same campaign, so it can be used at once. *Case E.1.*

## Control, elimination, victory

35. **Control follows the troops.** The last power to have a unit in a province controls
    it; a unit in a province also controls an ungarrisoned city there; a garrison
    controls its city, and the province too whenever no unit stands outside (this is
    how autonomous garrisons hold their provinces). *Cases C.6, G.1.*
36. **Elimination** is checked at the end of every campaign, after retreats: a power
    that controls no city in any home country it held when the campaign began is out.
    Its units are removed, its lands belong to nobody. *Case G.2.*
37. **Home countries** change hands whole: to a living power that controls every
    province and city of one. This is checked after eliminations, so two powers that
    take each other's home country in the same campaign are **both** eliminated
    (the judge rules warn of exactly this). *Cases G.2, G.3.*
38. **Victory** is checked at the end of every campaign: the board's number of cities
    (15 on the Italy board) **and** two home countries. If several powers qualify, the
    one with most cities; if they tie, play goes on. The last ruler left also wins.
    Every city counts, walled or open. *Case G.4.*

## Lagoon cities (city and province in one space)

39. Only one unit fits. A unit there converts on the spot. A garrison there cannot be
    attacked or besieged at all. It cannot come out in a campaign in which anybody
    tries to move in. An army or fleet there that is attacked with greater strength
    is dislodged even if it ordered a conversion, and normally has nowhere to go.
    Rebels can only take the place when it is empty; they then keep their ruler's
    units out, while anybody else's arrival ends the rebellion. *Cases V.1, V.2.*

## Simplifications to know about

| Judge rule | Here |
|---|---|
| Convoy orders name the whole sea route. | Any chain of fleets ordered to convoy the army will do (as in the standard game). Fleets in coastal provinces may convoy; a fleet cannot link two coasts of one province. |
| Straits: a fleet in the guarding province closes the passage to hostile fleets and convoys; the owner can allow others through; a fleet arriving there in the same campaign also closes it. | A foreign fleet in the guarding province at the start of the campaign stops other powers' **fleet moves** through the strait. No permission order; an arriving fleet does not close it yet; convoys are not affected. *Case S.1.* |
| Optional fortresses. | Not supported (the judge's standard scenario does not use them either). |
| Limits on which power may have which special unit. | Not supported. Special units themselves are (off by default; one per power). |
| Storms table. | Unused, as on the judge. |

## Options

Set on the board file's `rules purse ...` line, changeable per game before it starts
(`PurseRules.startOptions`): `famine`, `plague`, `loans`, `assassins`, `special`,
`adjacency`, `varincome`. The Italy board starts with all but `plague` and `special`,
which is what the judges default to.
