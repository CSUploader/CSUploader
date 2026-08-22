// <copyright file="ByteUnitTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Tests.Lib;

public class ByteUnitTests
{
    /// <summary>
    /// The roundness-picking factory behind hoster-cap displays: whichever base renders the count
    /// cleanly at its largest unit wins, so a cap reads the way the host advertises it — DropMB's
    /// 512,000,000 as "512 MB" (its own site's figure), not the arithmetically-identical
    /// "488.28 MiB" a forced-binary render produced (the wrong-looking cell a user reported).
    /// </summary>
    [Theory]
    [InlineData(512_000_000, "512 MB")] // DropMB's share.maxSize — the reported cell
    [InlineData(5_000_000_000, "5 GB")] // 1Fichier's guest cap, advertised decimal
    [InlineData(100_000_000, "100 MB")]
    [InlineData(5_500_000_000, "5.5 GB")] // one exact decimal place is still the host's own figure
    [InlineData(53_687_091_200, "50 GiB")] // DropMeFiles — binary-round caps keep reading binary
    [InlineData(5_368_709_120, "5 GiB")]
    [InlineData(314_572_800, "300 MiB")]
    [InlineData(1_048_576_000, "1000 MiB")] // roundness is judged at the LARGEST unit per base:
                                            // 1000 MiB is clean in MiB (binary wins) even though a
                                            // host might advertise it as "1 GB" — the heuristic
                                            // reconstructs round figures, not marketing copy
    public void FromBytesPreferRoundUnit_PicksTheBaseTheValueIsRoundIn(long bytes, string expected)
    {
        Assert.Equal(expected, ByteUnit.FromBytesPreferRoundUnit(bytes).ToFriendlyString());
    }

    [Fact]
    public void FromBytesPreferRoundUnit_CleanInNeitherBase_KeepsTheBinaryStatusQuo()
    {
        // 2,097,152,000 = 2000 MiB, which the largest-unit rule renders as 1.953125 GiB — not
        // clean — while decimal gives 2.097152 GB — not clean either. Neither base "wins", so the
        // pick falls back to binary, exactly what every cap rendered before the factory existed.
        Assert.Equal(
            ByteUnit.FromBytes(2_097_152_000, ByteBase.Binary).ToFriendlyString(),
            ByteUnit.FromBytesPreferRoundUnit(2_097_152_000).ToFriendlyString());
    }

    [Fact]
    public void FromBytesPreferRoundUnit_TieGoesToBinary()
    {
        // Zero (and anything under 1 KiB) renders identically in both bases; the tie-break keeps
        // the app's long-standing binary default rather than inventing a new one.
        Assert.Equal(ByteBase.Binary, ByteUnit.FromBytesPreferRoundUnit(0).Base);
    }
}
