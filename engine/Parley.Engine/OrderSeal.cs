// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Sealed envelopes for secret orders ("commit and reveal").
    ///
    /// A player's device keeps the order sheet to itself and publishes only a seal: a
    /// SHA-256 fingerprint of the orders mixed with a random secret (the salt). Once every
    /// seal is in, the devices publish salt and orders, and everybody can check that they
    /// match the seal. Nobody can read orders from a seal, and nobody can change orders
    /// after seeing someone else's.
    ///
    /// Udon has no built-in hashing, so SHA-256 is implemented here by hand. It keeps each
    /// 32-bit word as two 16-bit halves in ordinary ints, so no step ever overflows or goes
    /// negative: the result does not depend on how the platform treats integer overflow.
    /// </summary>
    public class OrderSeal : UdonSharpBehaviour
    {
        public const string VERSION = "PS1";
        public const int MIN_SALT_CHARS = 16;
        private const string HEXDIGITS = "0123456789abcdef";

        private int[] kh; private int[] kl;       // round constants, high and low halves
        private int[] wh; private int[] wl;       // message schedule
        private int[] hh; private int[] hl;       // running hash
        private int[] data; private int dataLen;  // message bytes
        private int rh; private int rl;           // result of the helper that was called last

        public void Init()
        {
            kh = new int[] {
                17034, 28983, 46528, 59829, 14678, 23025, 37439, 43804, 55303, 4739, 9265, 21772, 29374, 32990, 39900, 49563,
                58523, 61374, 4033, 9228, 11753, 19060, 23728, 30457, 38974, 43057, 45059, 48985, 50912, 54695, 1738, 5161,
                10167, 11803, 19756, 21304, 25866, 30314, 33218, 37490, 41663, 43034, 49739, 51052, 53650, 54937, 62478, 4202,
                6564, 7735, 10056, 13488, 14620, 20184, 23452, 26670, 29839, 30885, 33992, 36039, 37054, 42064, 48889, 50801 };
            kl = new int[] {
                12184, 17553, 64463, 56229, 49755, 4593, 33444, 24277, 43672, 23297, 34238, 32195, 23924, 45566, 1703, 61812,
                27073, 18310, 40390, 41420, 11375, 33962, 43484, 35034, 20818, 50797, 10184, 32711, 3059, 37191, 25425, 10599,
                2693, 8504, 28156, 3347, 29524, 2747, 51502, 11397, 59553, 26187, 35696, 20899, 59417, 1572, 13701, 41072,
                49430, 27656, 30540, 48309, 3251, 43594, 51791, 28659, 33518, 25455, 30740, 520, 65530, 27883, 41975, 30962 };
            wh = new int[64]; wl = new int[64];
            hh = new int[8]; hl = new int[8];
            data = new int[256];
        }

        // =====================================================================
        // Seals
        // =====================================================================

        /// <summary>
        /// The seal for one power's orders in one phase. phaseKey ties it to the exact
        /// position, so a seal cannot be reused in another phase or another game.
        /// </summary>
        public string Make(string salt, int power, string phaseKey, string orders)
        {
            return Sha256Hex(VERSION + "|" + salt + "|" + power.ToString() + "|" + phaseKey + "|" + (orders == null ? "" : orders));
        }

        /// <summary>Do this salt and these orders match the seal that was published earlier?</summary>
        public bool Check(string seal, string salt, int power, string phaseKey, string orders)
        {
            if (seal == null || seal.Length != 64) return false;
            if (!SaltOk(salt)) return false;
            return Make(salt, power, phaseKey, orders) == seal;
        }

        /// <summary>A salt must be letters and digits only and long enough to be unguessable.</summary>
        public bool SaltOk(string salt)
        {
            if (salt == null || salt.Length < MIN_SALT_CHARS || salt.Length > 128) return false;
            for (int i = 0; i < salt.Length; i++)
            {
                char c = salt[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Is this text shaped like a seal (64 lower-case hex digits)?</summary>
        public bool SealOk(string seal)
        {
            if (seal == null || seal.Length != 64) return false;
            for (int i = 0; i < 64; i++) if (HEXDIGITS.IndexOf(seal[i]) < 0) return false;
            return true;
        }

        /// <summary>
        /// Turns whatever unpredictable noise a device can gather (random numbers, the
        /// time, hand positions, tap timings) into a salt. The noise itself never leaves
        /// the device; the more of it, the better.
        /// </summary>
        public string SaltFrom(string noise)
        {
            return Sha256Hex("salt|" + noise).Substring(0, 32);
        }

        // =====================================================================
        // SHA-256
        // =====================================================================

        /// <summary>SHA-256 of the text (as UTF-8), as 64 lower-case hex digits.</summary>
        public string Sha256Hex(string text)
        {
            LoadBytes(text == null ? "" : text);
            hh[0] = 27145; hh[1] = 47975; hh[2] = 15470; hh[3] = 42319; hh[4] = 20750; hh[5] = 39685; hh[6] = 8067; hh[7] = 23520;
            hl[0] = 58983; hl[1] = 44677; hl[2] = 62322; hl[3] = 62778; hl[4] = 21119; hl[5] = 26764; hl[6] = 55723; hl[7] = 52505;
            for (int off = 0; off < dataLen; off += 64) Block(off);
            string r = "";
            for (int i = 0; i < 8; i++) r += Hex4(hh[i]) + Hex4(hl[i]);
            return r;
        }

        private string Hex4(int v)
        {
            return HEXDIGITS.Substring((v >> 12) & 15, 1) + HEXDIGITS.Substring((v >> 8) & 15, 1)
                + HEXDIGITS.Substring((v >> 4) & 15, 1) + HEXDIGITS.Substring(v & 15, 1);
        }

        /// <summary>Text to UTF-8 bytes, then the standard padding and length.</summary>
        private void LoadBytes(string text)
        {
            int need = text.Length * 3 + 72;
            if (data.Length < need) data = new int[need + 64];
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int c = text[i];
                if (c < 128) { data[n] = c; n++; }
                else if (c < 2048) { data[n] = 192 | (c >> 6); data[n + 1] = 128 | (c & 63); n += 2; }
                else if (c >= 55296 && c <= 56319 && i + 1 < text.Length && text[i + 1] >= 56320 && text[i + 1] <= 57343)
                {
                    // a surrogate pair: one character outside the basic plane, four bytes
                    int lo = text[i + 1];
                    int cp = 65536 + ((c - 55296) << 10) + (lo - 56320);
                    data[n] = 240 | (cp >> 18); data[n + 1] = 128 | ((cp >> 12) & 63); data[n + 2] = 128 | ((cp >> 6) & 63); data[n + 3] = 128 | (cp & 63);
                    n += 4; i++;
                }
                else { data[n] = 224 | (c >> 12); data[n + 1] = 128 | ((c >> 6) & 63); data[n + 2] = 128 | (c & 63); n += 3; }
            }
            int bytes = n;
            data[n] = 128; n++;
            while (n % 64 != 56) { data[n] = 0; n++; }
            // message length in bits, as 8 bytes; texts here are far below 2^28 bytes
            int bitsHigh = bytes >> 13;          // (bytes * 8) >> 16
            int bitsLow = (bytes & 8191) << 3;   // (bytes * 8) & 0xFFFF
            data[n] = 0; data[n + 1] = 0; data[n + 2] = 0; data[n + 3] = 0;
            data[n + 4] = (bitsHigh >> 8) & 255; data[n + 5] = bitsHigh & 255;
            data[n + 6] = (bitsLow >> 8) & 255; data[n + 7] = bitsLow & 255;
            dataLen = n + 8;
        }

        // Rotate the 32-bit word (h,l) right by n bits (0 < n < 32); result in rh, rl.
        private void Rotr(int h, int l, int n)
        {
            if (n >= 16) { int t = h; h = l; l = t; n -= 16; }
            if (n == 0) { rh = h; rl = l; return; }
            rh = ((h >> n) | (l << (16 - n))) & 65535;
            rl = ((l >> n) | (h << (16 - n))) & 65535;
        }

        // Shift the word right by n bits (0 < n < 16); result in rh, rl.
        private void Shr(int h, int l, int n)
        {
            rh = h >> n;
            rl = ((l >> n) | (h << (16 - n))) & 65535;
        }

        private void Block(int off)
        {
            int t;
            for (t = 0; t < 16; t++)
            {
                int p = off + t * 4;
                wh[t] = (data[p] << 8) | data[p + 1];
                wl[t] = (data[p + 2] << 8) | data[p + 3];
            }
            for (t = 16; t < 64; t++)
            {
                int xh = wh[t - 15]; int xl = wl[t - 15];
                Rotr(xh, xl, 7); int s0h = rh; int s0l = rl;
                Rotr(xh, xl, 18); s0h ^= rh; s0l ^= rl;
                Shr(xh, xl, 3); s0h ^= rh; s0l ^= rl;
                xh = wh[t - 2]; xl = wl[t - 2];
                Rotr(xh, xl, 17); int s1h = rh; int s1l = rl;
                Rotr(xh, xl, 19); s1h ^= rh; s1l ^= rl;
                Shr(xh, xl, 10); s1h ^= rh; s1l ^= rl;
                int lo = wl[t - 16] + s0l + wl[t - 7] + s1l;
                int hi = wh[t - 16] + s0h + wh[t - 7] + s1h + (lo >> 16);
                wl[t] = lo & 65535; wh[t] = hi & 65535;
            }
            int ah = hh[0]; int al = hl[0]; int bh = hh[1]; int bl = hl[1]; int ch = hh[2]; int cl = hl[2]; int dh = hh[3]; int dl = hl[3];
            int eh = hh[4]; int el = hl[4]; int fh = hh[5]; int fl = hl[5]; int gh = hh[6]; int gl = hl[6]; int jh = hh[7]; int jl = hl[7];
            for (t = 0; t < 64; t++)
            {
                Rotr(eh, el, 6); int e1h = rh; int e1l = rl;
                Rotr(eh, el, 11); e1h ^= rh; e1l ^= rl;
                Rotr(eh, el, 25); e1h ^= rh; e1l ^= rl;
                int chh = (eh & fh) ^ ((65535 ^ eh) & gh);
                int chl = (el & fl) ^ ((65535 ^ el) & gl);
                int t1l = jl + e1l + chl + kl[t] + wl[t];
                int t1h = jh + e1h + chh + kh[t] + wh[t] + (t1l >> 16);
                t1l &= 65535; t1h &= 65535;
                Rotr(ah, al, 2); int e0h = rh; int e0l = rl;
                Rotr(ah, al, 13); e0h ^= rh; e0l ^= rl;
                Rotr(ah, al, 22); e0h ^= rh; e0l ^= rl;
                int majh = (ah & bh) ^ (ah & ch) ^ (bh & ch);
                int majl = (al & bl) ^ (al & cl) ^ (bl & cl);
                int t2l = e0l + majl;
                int t2h = (e0h + majh + (t2l >> 16)) & 65535;
                t2l &= 65535;
                jh = gh; jl = gl; gh = fh; gl = fl; fh = eh; fl = el;
                int nl = dl + t1l;
                eh = (dh + t1h + (nl >> 16)) & 65535; el = nl & 65535;
                dh = ch; dl = cl; ch = bh; cl = bl; bh = ah; bl = al;
                nl = t1l + t2l;
                ah = (t1h + t2h + (nl >> 16)) & 65535; al = nl & 65535;
            }
            Add(0, ah, al); Add(1, bh, bl); Add(2, ch, cl); Add(3, dh, dl);
            Add(4, eh, el); Add(5, fh, fl); Add(6, gh, gl); Add(7, jh, jl);
        }

        private void Add(int i, int h, int l)
        {
            int lo = hl[i] + l;
            hh[i] = (hh[i] + h + (lo >> 16)) & 65535;
            hl[i] = lo & 65535;
        }
    }
}
