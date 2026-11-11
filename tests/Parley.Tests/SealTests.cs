// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Security.Cryptography;
using System.Text;
using Parley.Engine;
using Xunit;

namespace Parley.Tests;

public class SealTests
{
    private static OrderSeal New() { var s = new OrderSeal(); s.Init(); return s; }

    private static string Reference(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void Sha256MatchesPublishedTestVectors()
    {
        var s = New();
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", s.Sha256Hex(""));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", s.Sha256Hex("abc"));
        Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
            s.Sha256Hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"));
    }

    [Fact]
    public void Sha256MatchesTheStandardLibraryOnEveryLengthAndOnOtherAlphabets()
    {
        var s = New();
        var rnd = new Random(7);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-;,+!| ";
        for (int len = 0; len <= 200; len++)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            Assert.Equal(Reference(sb.ToString()), s.Sha256Hex(sb.ToString()));
        }
        foreach (var text in new[] { "héllo wörld", "サイコー", "Ω≈ç√∫", "emoji 🎲 dice", new string('x', 5000), "ÿࠀ￿" })
            Assert.Equal(Reference(text), s.Sha256Hex(text));
        // the same object gives the same answer again after hashing something long
        Assert.Equal(Reference("abc"), s.Sha256Hex("abc"));
    }

    [Fact]
    public void ASealOpensOnlyWithExactlyWhatWasSealed()
    {
        var s = New();
        const string salt = "0123456789abcdef0123456789abcdef", key = "1.S1M.0A0Q0e", orders = "0Am0B0Cs0D";
        string seal = s.Make(salt, 2, key, orders);
        Assert.True(s.SealOk(seal));
        Assert.True(s.Check(seal, salt, 2, key, orders));
        Assert.False(s.Check(seal, salt, 2, key, orders + "0Em0F"));      // different orders
        Assert.False(s.Check(seal, salt, 2, key, ""));                    // "I ordered nothing"
        Assert.False(s.Check(seal, salt, 1, key, orders));                // another power's seal
        Assert.False(s.Check(seal, salt, 2, "2.F1M.0A0Q0e", orders));     // another phase
        Assert.False(s.Check(seal, "0123456789abcdef0123456789abcdeX", 2, key, orders));
        Assert.False(s.Check(seal, "short", 2, key, orders));             // salts must be long enough to be unguessable
        Assert.False(s.Check("nonsense", salt, 2, key, orders));
        Assert.False(s.SealOk(seal.ToUpperInvariant()));
        // the same orders under a different salt look completely different, so a seal says nothing about its contents
        Assert.NotEqual(seal, s.Make("fedcba9876543210fedcba9876543210", 2, key, orders));
        Assert.True(s.SaltOk(s.SaltFrom("some noise 0.123 4.56")));
        Assert.NotEqual(s.SaltFrom("noise a"), s.SaltFrom("noise b"));
    }
}
