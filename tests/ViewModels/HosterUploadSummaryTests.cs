// <copyright file="HosterUploadSummaryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// The per-hoster capacity fit on the wizard's Summary page: greedy "keep biggest that fit"
/// auto-fit, over-capacity detection, and live recompute on checkbox toggles.
/// </summary>
public class HosterUploadSummaryTests
{
    private static SummaryFileItem Item(string name, long size, bool included = true)
        => new(new FileEntry { FileName = name, FullPath = name, Size = size }, included);

    private static HosterUploadSummary Summary(long? available, params SummaryFileItem[] items)
        => new("IcerBox", "user", items, available, maxFileSize: null);

    [Fact]
    public void AutoFit_KeepsBiggestThatFit_UnchecksRest()
    {
        // 1000 free; largest-first keeps 600 then 300 (=900); the second 300 won't fit.
        HosterUploadSummary s = Summary(1000, Item("big", 600), Item("m1", 300), Item("m2", 300));

        int removed = s.AutoFit();

        Assert.Equal(1, removed);
        Assert.Equal(900L, s.IncludedBytes);
        Assert.Equal(2, s.IncludedCount);
        Assert.False(s.IsOverCapacity);
        Assert.True(s.Files.First(f => f.Size == 600).Included);
        Assert.Equal(1, s.Files.Count(f => f.Size == 300 && !f.Included));
    }

    [Fact]
    public void AutoFit_KeepsTheBigFileOverManySmallOnes()
    {
        // The design example: 1000 free, one 900 + five 200 → keep only the 900.
        HosterUploadSummary s = Summary(
            1000,
            Item("a", 900),
            Item("b", 200),
            Item("c", 200),
            Item("d", 200),
            Item("e", 200),
            Item("f", 200));

        int removed = s.AutoFit();

        Assert.Equal(5, removed);
        Assert.Equal(900L, s.IncludedBytes);
        Assert.Equal(1, s.IncludedCount);
        Assert.True(s.Files.First(f => f.Size == 900).Included);
    }

    [Fact]
    public void AutoFit_Unlimited_KeepsEverythingAndNeverOverCapacity()
    {
        HosterUploadSummary s = Summary(available: null, Item("a", 5_000_000_000), Item("b", 9_000_000_000));

        int removed = s.AutoFit();

        Assert.Equal(0, removed);
        Assert.Equal(2, s.IncludedCount);
        Assert.False(s.IsOverCapacity);
    }

    [Fact]
    public void AutoFit_EverythingFits_UnchecksNothing()
    {
        HosterUploadSummary s = Summary(10_000, Item("a", 100), Item("b", 200));

        Assert.Equal(0, s.AutoFit());
        Assert.Equal(2, s.IncludedCount);
        Assert.False(s.IsOverCapacity);
    }

    [Fact]
    public void AutoFit_AvailableZero_DropsEveryPositiveSizeFile()
    {
        // Account already full (available == 0) → every positive-size file is unchecked.
        HosterUploadSummary s = Summary(0, Item("a", 100), Item("b", 200));

        int removed = s.AutoFit();

        Assert.Equal(2, removed);
        Assert.Equal(0, s.IncludedCount);
        Assert.Equal(0L, s.IncludedBytes);
        Assert.False(s.IsOverCapacity);
    }

    [Fact]
    public void Ctor_AllIncludedOverAvailable_IsOverCapacityBeforeAnyFit()
    {
        // Items start checked; the ctor's Recompute flags over-capacity (600 + 700 > 1000).
        HosterUploadSummary s = Summary(1000, Item("a", 600), Item("b", 700));

        Assert.True(s.IsOverCapacity);
        Assert.Equal(1300L, s.IncludedBytes);
    }

    [Fact]
    public void Recheck_PastAvailable_FlipsOverCapacityAndRaisesCapacityChanged()
    {
        HosterUploadSummary s = Summary(1000, Item("a", 600), Item("b", 300), Item("c", 300));
        s.AutoFit(); // 600 + one 300 checked (900); the other 300 dropped
        Assert.False(s.IsOverCapacity);

        int events = 0;
        s.CapacityChanged += (_, _) => events++;

        s.Files.First(f => !f.Included).Included = true; // re-check the dropped 300 → 1200 > 1000

        Assert.True(s.IsOverCapacity);
        Assert.Equal(1200L, s.IncludedBytes);
        Assert.True(events >= 1);
    }

    [Fact]
    public void Uncheck_BackWithinAvailable_ClearsOverCapacity()
    {
        HosterUploadSummary s = Summary(1000, Item("a", 600), Item("b", 700)); // 1300 > 1000 at ctor
        Assert.True(s.IsOverCapacity);

        s.Files.First(f => f.Size == 700).Included = false; // 600 ≤ 1000

        Assert.False(s.IsOverCapacity);
        Assert.Equal(600L, s.IncludedBytes);
        Assert.Equal(1, s.IncludedCount);
    }

    [Fact]
    public void AutoFit_DroppingFiles_ExposesPerHosterUncheckedClue()
    {
        // 1000 free; one 900 + five 200 → auto-fit keeps only the 900, drops 5.
        HosterUploadSummary s = Summary(
            1000, Item("a", 900), Item("b", 200), Item("c", 200), Item("d", 200), Item("e", 200), Item("f", 200));

        s.AutoFit();

        Assert.Equal(5, s.UncheckedCount);
        Assert.True(s.HasUncheckedFiles);
        Assert.NotEmpty(s.UncheckedDisplay);
    }

    [Fact]
    public void UncheckedDisplay_NamesTheAccountsFreeSpace()
    {
        // 10 GiB free; a 9 GiB + two 1 GiB → auto-fit keeps the 9 GiB, drops the rest.
        const long Gib = 1024L * 1024 * 1024;
        HosterUploadSummary s = Summary(10 * Gib, Item("big", 9 * Gib), Item("a", Gib), Item("b", Gib));

        s.AutoFit();

        Assert.True(s.HasUncheckedFiles);
        Assert.Contains("unchecked", s.UncheckedDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10 GiB", s.UncheckedDisplay, StringComparison.Ordinal); // the account's free space
        Assert.Contains("free", s.UncheckedDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UncheckedClue_HidesWhenOverCapacity_AndReturnsWhenBackUnder()
    {
        // 1000 free, three 700s → auto-fit keeps one, drops two (within capacity → clue shows).
        HosterUploadSummary s = Summary(1000, Item("a", 700), Item("b", 700), Item("c", 700));
        s.AutoFit();
        Assert.True(s.HasUncheckedFiles);
        Assert.False(s.IsOverCapacity);

        // Re-check one dropped file → 1400 > 1000 (over). The red hint takes over; the clue hides
        // even though a file is still unchecked.
        s.Files.First(f => !f.Included).Included = true;
        Assert.True(s.IsOverCapacity);
        Assert.False(s.HasUncheckedFiles);

        // Uncheck it again → back under, clue returns.
        s.Files.First(f => f.Included).Included = false;
        Assert.False(s.IsOverCapacity);
        Assert.True(s.HasUncheckedFiles);
    }

    [Fact]
    public void Unlimited_NeverShowsUncheckedClue_EvenAfterManualUncheck()
    {
        HosterUploadSummary s = Summary(available: null, Item("a", 100), Item("b", 200));
        s.AutoFit(); // no-op for unlimited

        s.Files.First().Included = false; // user manually unchecks one

        Assert.False(s.HasUncheckedFiles); // no quota → no capacity-reason clue
        Assert.Empty(s.UncheckedDisplay);
    }
}
