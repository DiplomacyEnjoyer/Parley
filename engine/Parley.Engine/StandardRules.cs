// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// The standard game as a ruleset module: armies and fleets of strength one, the
    /// seasons the map lists (spring and fall unless it says otherwise), supply centres
    /// that change hands when a season marked for it ends, one unit per centre, and
    /// victory by number of centres. No money, no dice.
    ///
    /// It is deliberately thin. Moving, supporting, convoying and retreating are done by
    /// the shared referee (Adjudicator), which every ruleset uses; what makes the standard
    /// game the standard game is which orders a phase asks for and how the year runs, and
    /// that is what this file says. A new ruleset is a file with these same methods (see
    /// Ruleset.cs for how they are reached).
    /// </summary>
    public class StandardRules : UdonSharpBehaviour
    {
        public MapData map;
        public Adjudicator adj;
        public GameCodec codec;

        public void Init(MapData m, Adjudicator a, GameCodec c)
        {
            map = m; adj = a; codec = c;
            adj.autoAdvance = true; // the referee runs the standard calendar by itself
        }

        public string Title() { return "Standard"; }

        public void SetupStart(BoardState s) { s.SetupStart(map); }

        /// <summary>Does this power have anything to decide in the phase the board is in?</summary>
        public bool NeedsOrders(BoardState s, int power)
        {
            if (s.phase == BoardState.PHASE_MOVE) return s.CountUnits(power) > 0;
            if (s.phase == BoardState.PHASE_RETREAT) return s.CountDislodged(power) > 0;
            if (s.phase == BoardState.PHASE_ADJUST) return adj.AdjustDelta(s, power) != 0;
            return false;
        }

        public bool IsOut(BoardState s, int power) { return s.IsEliminated(power); }

        public void ClearOrders() { adj.ClearOrders(); }

        /// <summary>Enters one power's sheet into the referee (power -1: everybody's).</summary>
        public bool AddOrders(string text, BoardState s, int power) { return codec.DecodeOrders(text, s, adj, power); }

        /// <summary>The orders now entered, as sheet text (power -1: everybody's).</summary>
        public string OrdersText(BoardState s, int power) { return codec.EncodeOrders(s, adj, power); }

        public void Resolve(BoardState s) { adj.Resolve(s); }
    }
}
