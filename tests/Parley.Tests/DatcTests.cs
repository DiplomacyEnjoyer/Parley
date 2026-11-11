// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>Every DATC v3.0 chapter 6 case, one test each.</summary>
public class DatcTests
{
    private static readonly Lazy<Dictionary<string, DatcRunner.CaseResult>> Results =
        new(() => DatcRunner.RunAll().ToDictionary(r => r.Id));

    public static IEnumerable<object[]> CaseIds()
    {
        foreach (var line in File.ReadAllLines(DatcRunner.CasesPath()))
            if (line.StartsWith("CASE ")) yield return new object[] { line.Split(' ')[1] };
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Case(string id)
    {
        var r = Results.Value[id];
        Assert.True(r.Passed, id + " " + r.Title + ": " + string.Join("; ", r.Failures));
    }

    [Fact]
    public void AllCasesArePresent()
    {
        // 164 cases in DATC v3.0 chapter 6, plus the second situation of 6.J.9
        Assert.Equal(165, Results.Value.Count);
    }
}
