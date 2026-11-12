// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// The one door through which the game flow reaches "the rules".
    ///
    /// A ruleset is a module: a file such as StandardRules.cs that says what a phase asks
    /// of each power, how a sheet of orders is read, what happens when the phase is
    /// resolved and how the calendar moves on. Which module a game uses is decided by the
    /// board ("rules" line of the .pmap file; "standard" when there is none).
    ///
    /// What a ruleset can define, and where:
    ///   seasons per year, their names, when centres change hands   map file ("seasons" line) -> MapData
    ///   unit types and where each can stand                         MapData.CanStand, MapData.unitTypeCount
    ///   unit strength, bonuses, orders that are forced to hold      set in the referee before it resolves
    ///   money, loans, bribes, anything else it must remember        BoardState.ext / unitFlag / provFlag
    ///   secret spending that resolves before movement               part of the same sealed sheet; the module resolves it first
    ///   random events                                               the Dice it is handed (UsesDice says whether it wants any)
    ///   victory                                                     the module's Resolve sets BoardState.winner
    ///
    /// UdonSharp has no interfaces, so this class is a switch on <see cref="kind"/>:
    /// adding a ruleset means writing its module and adding one line to each method here.
    /// </summary>
    public class Ruleset : UdonSharpBehaviour
    {
        public const int KIND_STANDARD = 0;
        public const int KIND_PURSE = 1;

        public int kind;
        public string error = "";

        public MapData map;
        public Adjudicator adj;
        public GameCodec codec;
        public Dice dice;
        public StandardRules standard;
        public PurseRules purse;

        /// <summary>
        /// Chooses the module the board asks for. Returns false (see error) if this
        /// version of the game does not have it.
        /// </summary>
        public bool Init(MapData m, Adjudicator a, GameCodec c, Dice d, StandardRules standardModule, PurseRules purseModule)
        {
            map = m; adj = a; codec = c; dice = d; standard = standardModule; purse = purseModule; error = "";
            kind = KIND_STANDARD;
            standard.Init(m, a, c);
            if (m.rulesId == "purse")
            {
                kind = KIND_PURSE;
                if (!purse.Init(m, a, c, d)) { error = purse.error; return false; }
                return true;
            }
            if (m.rulesId != "standard") { error = "this board needs the ruleset '" + m.rulesId + "', which this version of the game does not have"; return false; }
            return true;
        }

        /// <summary>Name of the ruleset, for menus.</summary>
        public string Title() { return kind == KIND_PURSE ? purse.Title() : standard.Title(); }

        /// <summary>Makes room in a board for the numbers this ruleset keeps. Call once for every BoardState.</summary>
        public void Attach(BoardState s) { if (kind == KIND_PURSE) purse.Attach(s); }

        public void SetupStart(BoardState s) { if (kind == KIND_PURSE) purse.SetupStart(s); else standard.SetupStart(s); }

        /// <summary>Does resolving a phase need dice? If so every player's device must contribute to the seed.</summary>
        public bool UsesDice(BoardState s) { return kind == KIND_PURSE && purse.UsesDice(s); }

        /// <summary>Can a player sit in this seat? (False for powers the rules run themselves.)</summary>
        public bool Playable(int power)
        {
            if (power < 0 || power >= map.powerCount) return false;
            return kind != KIND_PURSE || purse.Playable(power);
        }

        public bool NeedsOrders(BoardState s, int power) { return kind == KIND_PURSE ? purse.NeedsOrders(s, power) : standard.NeedsOrders(s, power); }

        /// <summary>Is this power out of the game?</summary>
        public bool IsOut(BoardState s, int power) { return kind == KIND_PURSE ? purse.IsOut(s, power) : standard.IsOut(s, power); }

        public void ClearOrders() { if (kind == KIND_PURSE) purse.ClearOrders(); else standard.ClearOrders(); }

        public bool AddOrders(string text, BoardState s, int power)
        {
            if (kind == KIND_PURSE)
            {
                bool fine = purse.AddOrders(text, s, power);
                error = fine ? "" : purse.error;
                return fine;
            }
            bool ok = standard.AddOrders(text, s, power);
            error = ok ? "" : codec.error;
            return ok;
        }

        /// <summary>Is this text a readable sheet for this power in the current phase? (Leaves the referee cleared.)</summary>
        public bool OrdersOk(string text, BoardState s, int power)
        {
            ClearOrders();
            bool ok = AddOrders(text, s, power);
            ClearOrders();
            return ok;
        }

        public string OrdersText(BoardState s, int power) { return kind == KIND_PURSE ? purse.OrdersText(s, power) : standard.OrdersText(s, power); }

        /// <summary>
        /// Resolves the phase the board is in with the orders entered, and moves the
        /// board on. seed is the phase's dice seed ("" if the ruleset uses no dice). The
        /// announcement is filled in with what was ordered and what became of it; status
        /// says how each power's sheet arrived (Announcement.S_*), or is null.
        /// </summary>
        public void Resolve(BoardState s, string seed, Announcement announce, int[] status)
        {
            dice.Begin(seed);
            if (kind == KIND_PURSE) { purse.Resolve(s, announce, status); return; }
            announce.Capture(s, adj, status);
            standard.Resolve(s);
            announce.Finish(adj);
        }

        /// <summary>Which movement phase of the game the board is in (0 = the first); the clock schedule goes by this.</summary>
        public int MoveIndex(BoardState s)
        {
            int season = s.season < map.seasonCount ? s.season : map.seasonCount - 1;
            int i = (s.year - map.startYear) * map.seasonCount + season;
            return i < 0 ? 0 : i;
        }
    }
}
