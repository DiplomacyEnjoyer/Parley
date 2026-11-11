// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// The whole story of one game: the position at the start of every phase and the
    /// orders that were resolved in it. The last entry is the phase currently being
    /// played (no orders yet).
    ///
    /// It is saved as one line of text, the "game archive":
    ///     PA1;mapId.hash;timers;seats;entry;entry;...;check
    /// where each entry is a position (see GameCodec.EncodeBody) optionally followed by
    /// '+' and that phase's orders. Any entry can be turned back into a board, which is
    /// what makes roll-back possible. The archive also remembers who sat where, so a
    /// restarted game can hand the same powers to the same people.
    /// </summary>
    public class GameHistory : UdonSharpBehaviour
    {
        public const string MAGIC = "PA1";
        private const string HEX = "0123456789ABCDEF";

        public MapData map;
        public GameCodec codec;

        public int count;
        public string[] body;     // position at the start of phase i
        public string[] orders;   // orders resolved in phase i ("" if none recorded / still open)

        /// <summary>Per power: display name of the player who holds it ("" = nobody).</summary>
        public string[] seatName;

        // timer settings travel with the game so a resumed game keeps its pace (seconds; 0 = no limit)
        public int moveSeconds;
        public int retreatSeconds;
        public int adjustSeconds;

        public string error = "";

        public void Init(MapData m, GameCodec c)
        {
            map = m; codec = c;
            body = new string[64]; orders = new string[64];
            seatName = new string[m.powerCount];
            Reset();
        }

        public void Reset()
        {
            count = 0;
            for (int i = 0; i < seatName.Length; i++) seatName[i] = "";
            moveSeconds = 0; retreatSeconds = 0; adjustSeconds = 0;
        }

        private void Grow()
        {
            int n = body.Length * 2;
            string[] nb = new string[n]; string[] no = new string[n];
            for (int i = 0; i < count; i++) { nb[i] = body[i]; no[i] = orders[i]; }
            body = nb; orders = no;
        }

        /// <summary>Starts a new phase with the given position.</summary>
        public void Push(string positionBody)
        {
            if (count >= body.Length) Grow();
            body[count] = positionBody; orders[count] = "";
            count++;
        }

        /// <summary>Records the orders that were resolved in the latest phase.</summary>
        public void SetLatestOrders(string ordersText)
        {
            if (count > 0) orders[count - 1] = ordersText;
        }

        /// <summary>Rolls back: entry "index" becomes the current, open phase again.</summary>
        public void RollBackTo(int index)
        {
            if (index < 0 || index >= count) return;
            count = index + 1;
            orders[index] = "";
        }

        /// <summary>The self-contained game code of entry i (the latest entry if i is out of range).</summary>
        public string CodeAt(int i)
        {
            if (count == 0) return "";
            if (i < 0 || i >= count) i = count - 1;
            string b = GameCodec.MAGIC + "." + map.mapId + "." + map.hash + "." + body[i];
            return b + "." + codec.Checksum(b);
        }

        public string LatestCode() { return CodeAt(count - 1); }

        // =====================================================================
        // Saving and loading
        // =====================================================================

        public string ToArchive()
        {
            string r = MAGIC + ";" + map.mapId + "." + map.hash + ";"
                + moveSeconds.ToString() + "-" + retreatSeconds.ToString() + "-" + adjustSeconds.ToString() + ";";
            for (int i = 0; i < seatName.Length; i++)
            {
                if (i > 0) r += ",";
                r += EscapeName(seatName[i]);
            }
            for (int i = 0; i < count; i++)
            {
                r += ";" + body[i];
                if (orders[i] != "") r += "+" + orders[i];
            }
            return r + ";" + codec.Checksum(r);
        }

        /// <summary>Which map does this archive need? Returns "id.hash", or "".</summary>
        public string PeekMap(string archive)
        {
            if (archive == null) return "";
            string[] p = archive.Trim().Split(';');
            if (p.Length < 5 || p[0] != MAGIC) return "";
            return p[1];
        }

        /// <summary>
        /// Loads an archive. Every position in it is checked by actually decoding it, so a
        /// damaged archive is rejected as a whole rather than half-loaded.
        /// </summary>
        public bool LoadArchive(string archive, BoardState scratch)
        {
            error = "";
            if (archive == null) { error = "empty archive"; return false; }
            string t = archive.Trim();
            int last = t.LastIndexOf(';');
            if (last < 0) { error = "this is not a game archive"; return false; }
            string content = t.Substring(0, last);
            string[] p = content.Split(';');
            if (p[0] != MAGIC)
            {
                bool otherVersion = p[0].Length >= 3 && p[0][0] == 'P' && p[0][1] == 'A';
                error = otherVersion ? "this archive is from a different version of the game" : "this is not a game archive";
                return false;
            }
            if (p.Length < 5) { error = "the archive is damaged or incomplete"; return false; }
            if (codec.Checksum(content) != t.Substring(last + 1)) { error = "the archive is damaged or incomplete (checksum does not match)"; return false; }
            if (p[1] != map.mapId + "." + map.hash) { error = "this archive is for a different map (" + p[1] + ")"; return false; }

            string[] timers = p[2].Split('-');
            if (timers.Length != 3) { error = "bad timer settings"; return false; }
            int ms = map.ParseInt(timers[0], -1); int rs = map.ParseInt(timers[1], -1); int az = map.ParseInt(timers[2], -1);
            if (ms < 0 || rs < 0 || az < 0) { error = "bad timer settings"; return false; }

            string[] seats = p[3].Split(',');
            if (seats.Length != map.powerCount) { error = "bad seat list"; return false; }

            int n = p.Length - 4;
            string[] nb = new string[n < 64 ? 64 : n * 2]; string[] no = new string[nb.Length];
            for (int i = 0; i < n; i++)
            {
                string e = p[4 + i];
                int plus = e.IndexOf('+');
                nb[i] = plus < 0 ? e : e.Substring(0, plus);
                no[i] = plus < 0 ? "" : e.Substring(plus + 1);
                if (!codec.DecodeBody(nb[i], scratch)) { error = "entry " + (i + 1).ToString() + ": " + codec.error; return false; }
            }
            body = nb; orders = no; count = n;
            moveSeconds = ms; retreatSeconds = rs; adjustSeconds = az;
            for (int i = 0; i < seatName.Length; i++) seatName[i] = UnescapeName(seats[i]);
            return true;
        }

        /// <summary>Total size of the archive text, without building it.</summary>
        public int ApproxChars()
        {
            int c = 40 + map.mapId.Length;
            for (int i = 0; i < seatName.Length; i++) c += seatName[i].Length * 2 + 1;
            for (int i = 0; i < count; i++) c += body[i].Length + orders[i].Length + 2;
            return c;
        }

        /// <summary>
        /// Keeps the archive under a size budget (VRChat limits how much a world may store).
        /// Old orders are forgotten first; positions are kept as long as possible because
        /// they are what roll-back needs. Returns true if anything was dropped.
        /// </summary>
        public bool TrimTo(int maxChars)
        {
            bool dropped = false;
            for (int i = 0; i < count - 1 && ApproxChars() > maxChars; i++)
                if (orders[i] != "") { orders[i] = ""; dropped = true; }
            while (count > 2 && ApproxChars() > maxChars)
            {
                for (int i = 1; i < count; i++) { body[i - 1] = body[i]; orders[i - 1] = orders[i]; }
                count--;
                dropped = true;
            }
            return dropped;
        }

        // Names may contain anything, so everything except plain letters and digits is
        // written as '!' followed by four hex digits.
        public string EscapeName(string name)
        {
            if (name == null) return "";
            string r = "";
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) r += name.Substring(i, 1);
                else
                {
                    int v = c;
                    r += "!" + HEX.Substring((v / 4096) % 16, 1) + HEX.Substring((v / 256) % 16, 1) + HEX.Substring((v / 16) % 16, 1) + HEX.Substring(v % 16, 1);
                }
            }
            return r;
        }

        public string UnescapeName(string text)
        {
            string r = "";
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '!' && i + 4 < text.Length)
                {
                    int v = 0;
                    for (int k = 1; k <= 4; k++)
                    {
                        int d = HEX.IndexOf(text[i + k]);
                        if (d < 0) d = 0;
                        v = v * 16 + d;
                    }
                    r += ((char)v).ToString();
                    i += 5;
                }
                else { r += text.Substring(i, 1); i++; }
            }
            return r;
        }
    }
}
