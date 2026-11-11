// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Dice that every device can roll for itself and get the same result.
    ///
    /// Nothing here is random. All the randomness is in the seed, which the game flow
    /// builds from secrets that every player's device committed to before anybody could
    /// know anybody else's (see GameFlow and docs/secret-orders-and-dice.md). Given the
    /// seed, each roll is a fixed function of its label ("famine.row", "kill.3.5" ...),
    /// so the order in which a ruleset asks for its rolls does not matter, and anyone
    /// can re-check every roll afterwards from the seed that was announced.
    ///
    /// A ruleset must give every roll of one phase a different label.
    /// </summary>
    public class Dice : UdonSharpBehaviour
    {
        public const string VERSION = "PD1";
        private const string HEXDIGITS = "0123456789abcdef";

        public OrderSeal hash;
        /// <summary>The seed of the phase being resolved ("" when the phase has no dice).</summary>
        public string seed = "";

        // every roll made since Begin, for the announcement and for checking afterwards
        public int rollCount;
        public string[] rollLabel;
        public int[] rollSides;
        public int[] rollValue;

        public void Init(OrderSeal sha)
        {
            hash = sha;
            rollLabel = new string[64]; rollSides = new int[64]; rollValue = new int[64];
            seed = ""; rollCount = 0;
        }

        /// <summary>Starts a new phase's dice from its seed and forgets the previous phase's rolls.</summary>
        public void Begin(string seedText)
        {
            seed = seedText == null ? "" : seedText;
            rollCount = 0;
        }

        /// <summary>
        /// A whole number from 1 to sides, each equally likely. Works by reading 28 bits
        /// from SHA-256(seed, label) and, in the rare case that they fall in the uneven
        /// tail (which would make low numbers a touch likelier), reading another 28.
        /// </summary>
        public int Roll(int sides, string label)
        {
            if (sides < 1) sides = 1;
            int v = 0;
            if (sides > 1)
            {
                int limit = (268435456 / sides) * sides;
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    string h = hash.Sha256Hex(VERSION + "|" + seed + "|" + label + "|" + attempt.ToString());
                    v = 0;
                    for (int i = 0; i < 7; i++) v = v * 16 + HEXDIGITS.IndexOf(h[i]);
                    if (v < limit) break;
                }
                v = v % sides;
            }
            if (rollCount >= rollLabel.Length) Grow();
            rollLabel[rollCount] = label; rollSides[rollCount] = sides; rollValue[rollCount] = v + 1;
            rollCount++;
            return v + 1;
        }

        private void Grow()
        {
            int n = rollLabel.Length * 2;
            string[] nl = new string[n]; int[] ns = new int[n]; int[] nv = new int[n];
            for (int i = 0; i < rollCount; i++) { nl[i] = rollLabel[i]; ns[i] = rollSides[i]; nv[i] = rollValue[i]; }
            rollLabel = nl; rollSides = ns; rollValue = nv;
        }
    }
}
