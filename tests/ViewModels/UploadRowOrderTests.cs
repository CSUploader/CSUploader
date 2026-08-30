// <copyright file="UploadRowOrderTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Moq;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// Ordering rules for the Uploads tab's hierarchical sort. The order is built by the ViewModel
/// rather than by a DataGrid sort description, so all of it is testable here without Avalonia —
/// which is the point of building it this way (see
/// docs/superpowers/specs/2026-08-30-uploads-hierarchical-sort-design.md for the probe that
/// falsified the comparer-on-the-collection-view approach).
/// </summary>
public class UploadRowOrderTests
{
    // ── Key resolution ────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeyFor_SharedPath_ResolvesOnBothRowTypes()
    {
        Package pkg = MakePackage("Holiday", "Rapidgator");
        PackageFile file = MakeFile(pkg, "clip.bin", "Turbobit");

        // Both row types expose the sort paths under the same names — the property that lets one
        // flat grid render both, and the reason a path is all the ordering needs.
        Assert.Equal("Rapidgator", UploadRowSortKeys.KeyFor(pkg, "HosterDisplay"));
        Assert.Equal("Turbobit", UploadRowSortKeys.KeyFor(file, "HosterDisplay"));
        Assert.Equal("Holiday", UploadRowSortKeys.KeyFor(pkg, "Name"));
        Assert.Equal("clip.bin", UploadRowSortKeys.KeyFor(file, "Name"));
    }

    [Fact]
    public void KeyFor_PathAbsentOnType_ReturnsNull()
    {
        Package pkg = MakePackage("Holiday", "Rapidgator");
        PackageFile file = MakeFile(pkg, "clip.bin", "Rapidgator");
        file.QueueOrder = 7;

        // QueueOrder is the one sort path packages do not have. Null here is what keeps packages
        // in default order while their files rank by queue position.
        Assert.Null(UploadRowSortKeys.KeyFor(pkg, "QueueOrder"));
        Assert.Equal(7, UploadRowSortKeys.KeyFor(file, "QueueOrder"));
    }

    [Fact]
    public void KeyFor_UnknownPath_ReturnsNull()
    {
        Package pkg = MakePackage("Holiday", "Rapidgator");

        Assert.Null(UploadRowSortKeys.KeyFor(pkg, "NoSuchProperty"));
    }

    // ── Key comparison ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_Ascending_OrdersNaturally()
    {
        UploadKeyComparer cmp = new(ListSortDirection.Ascending);

        Assert.True(cmp.Compare(1L, 2L) < 0);
        Assert.True(cmp.Compare(2L, 1L) > 0);
        Assert.Equal(0, cmp.Compare(1L, 1L));
    }

    [Fact]
    public void Compare_Descending_ReversesOrder()
    {
        UploadKeyComparer cmp = new(ListSortDirection.Descending);

        Assert.True(cmp.Compare(1L, 2L) > 0);
        Assert.True(cmp.Compare(2L, 1L) < 0);
    }

    [Fact]
    public void Compare_Nulls_SortLastInBothDirections()
    {
        // Deliberate asymmetry: an idle queue's blank Speed/ETA rows belong at the bottom either
        // way round, so descending is NOT the exact reverse of ascending.
        UploadKeyComparer asc = new(ListSortDirection.Ascending);
        UploadKeyComparer desc = new(ListSortDirection.Descending);

        Assert.True(asc.Compare(null, 5L) > 0);
        Assert.True(asc.Compare(5L, null) < 0);
        Assert.True(desc.Compare(null, 5L) > 0);
        Assert.True(desc.Compare(5L, null) < 0);
        Assert.Equal(0, asc.Compare(null, null));
        Assert.Equal(0, desc.Compare(null, null));
    }

    [Fact]
    public void Compare_Strings_IgnoresCase()
    {
        UploadKeyComparer cmp = new(ListSortDirection.Ascending);

        Assert.True(cmp.Compare("apple", "Banana") < 0);
        Assert.Equal(0, cmp.Compare("Rapidgator", "rapidgator"));
    }

    [Fact]
    public void Compare_MismatchedTypes_DoesNotThrow()
    {
        // Impossible across the current 21 paths, but a future column pairing an int with a
        // string must not take the sort down with an ArgumentException from CompareTo.
        UploadKeyComparer cmp = new(ListSortDirection.Ascending);

        Exception? ex = Record.Exception(() => cmp.Compare(5, "five"));

        Assert.Null(ex);
    }

    [Fact]
    public void Compare_MismatchedTypes_IsTransitive()
    {
        // A falling-back-to-ToString comparer is not merely imprecise, it is CYCLIC: 2 < 10 as
        // ints, "10" < "15" as text, and "15" < "2" as text — so 2 < 10 < 15 < 2, and a sort
        // over that produces arbitrary output rather than a slightly-off one. Ordering unlike
        // types by type first restores a total order.
        UploadKeyComparer cmp = new(ListSortDirection.Ascending);
        object[] values = [2, 10, 15L];

        foreach (object a in values)
        {
            foreach (object b in values)
            {
                foreach (object c in values)
                {
                    if (cmp.Compare(a, b) <= 0 && cmp.Compare(b, c) <= 0)
                    {
                        Assert.True(
                            cmp.Compare(a, c) <= 0,
                            $"intransitive: {a} <= {b} <= {c} but {a} > {c}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Compare_MismatchedTypes_IsAntisymmetric()
    {
        UploadKeyComparer cmp = new(ListSortDirection.Ascending);

        Assert.Equal(-Math.Sign(cmp.Compare(10, 15L)), Math.Sign(cmp.Compare(15L, 10)));
        Assert.Equal(-Math.Sign(cmp.Compare(5, "five")), Math.Sign(cmp.Compare("five", 5)));
    }

    // ── Row ordering ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoSort_ReturnsDefaultOrder()
    {
        Package zulu = MakePackage("Zulu", "Rapidgator");
        PackageFile z1 = AddFile(zulu, "b.bin", "Turbobit");
        PackageFile z2 = AddFile(zulu, "a.bin", "Katfile");
        Package alpha = MakePackage("Alpha", "Katfile");
        PackageFile a1 = AddFile(alpha, "c.bin", "Rapidgator");

        List<object> rows = UploadRowOrder.Build([zulu, alpha], sort: null);

        // Untouched: packages in their given order, files in the order they were added.
        Assert.Equal([zulu, z1, z2, alpha, a1], rows);
    }

    [Fact]
    public void Build_SortsPackagesAmongThemselves()
    {
        Package zulu = MakePackage("Zulu", "Rapidgator");
        Package alpha = MakePackage("Alpha", "Katfile");
        Package mike = MakePackage("Mike", "Turbobit");

        List<object> rows = UploadRowOrder.Build([zulu, alpha, mike], Sort("Name"));

        Assert.Equal([alpha, mike, zulu], rows);
    }

    [Fact]
    public void Build_SortsFilesWithinTheirPackage()
    {
        Package pkg = MakePackage("Holiday", "Rapidgator");
        PackageFile turbo = AddFile(pkg, "one.bin", "Turbobit");
        PackageFile kat = AddFile(pkg, "two.bin", "Katfile");
        PackageFile rapid = AddFile(pkg, "three.bin", "Rapidgator");

        List<object> rows = UploadRowOrder.Build([pkg], Sort("HosterDisplay"));

        Assert.Equal([pkg, kat, rapid, turbo], rows);
    }

    [Theory]
    [InlineData(ListSortDirection.Ascending)]
    [InlineData(ListSortDirection.Descending)]
    public void Build_KeepsEveryPackageDirectlyAboveItsOwnFiles(ListSortDirection direction)
    {
        // The invariant the whole feature exists to protect, and the one the abandoned
        // comparer approach broke: a descending sort must not float files above their package.
        Package zulu = MakePackage("Zulu", "Rapidgator");
        AddFile(zulu, "z-one.bin", "Turbobit");
        AddFile(zulu, "z-two.bin", "Katfile");
        Package alpha = MakePackage("Alpha", "Katfile");
        AddFile(alpha, "a-one.bin", "Rapidgator");

        List<object> rows = UploadRowOrder.Build([zulu, alpha], new UploadSort("Name", direction));

        AssertTreeIntact(rows);
    }

    [Fact]
    public void Build_FilesOfDifferentPackagesNeverInterleave()
    {
        // Hoster keys deliberately chosen so a flat global sort WOULD interleave them.
        Package first = MakePackage("First", "Mega");
        AddFile(first, "f1.bin", "Zzz");
        AddFile(first, "f2.bin", "Aaa");
        Package second = MakePackage("Second", "Mega");
        AddFile(second, "s1.bin", "Bbb");

        List<object> rows = UploadRowOrder.Build([first, second], Sort("HosterDisplay"));

        AssertTreeIntact(rows);
    }

    [Fact]
    public void Build_CollapsedPackage_ContributesNoFileRows()
    {
        Package pkg = MakePackage("Holiday", "Rapidgator");
        AddFile(pkg, "one.bin", "Turbobit");
        pkg.IsExpanded = false;

        List<object> rows = UploadRowOrder.Build([pkg], Sort("Name"));

        Assert.Equal([pkg], rows);
    }

    [Fact]
    public void Build_EqualKeys_KeepsPriorOrder()
    {
        // Stability is what removes the need for any tiebreaker: equal keys keep the order they
        // already had, so a sorted view of identical rows still reads as the queue does.
        Package one = MakePackage("Same", "Rapidgator");
        Package two = MakePackage("Same", "Rapidgator");
        Package three = MakePackage("Same", "Rapidgator");

        List<object> rows = UploadRowOrder.Build([one, two, three], Sort("Name"));

        Assert.Equal([one, two, three], rows);
    }

    [Fact]
    public void Build_PathAbsentOnPackages_RanksFilesAndLeavesPackagesInDefaultOrder()
    {
        Package zulu = MakePackage("Zulu", "Rapidgator");
        PackageFile z1 = AddFile(zulu, "z1.bin", "Rapidgator");
        PackageFile z2 = AddFile(zulu, "z2.bin", "Rapidgator");
        z1.QueueOrder = 9;
        z2.QueueOrder = 4;
        Package alpha = MakePackage("Alpha", "Katfile");
        PackageFile a1 = AddFile(alpha, "a1.bin", "Katfile");
        a1.QueueOrder = 1;

        List<object> rows = UploadRowOrder.Build([zulu, alpha], Sort("QueueOrder"));

        // Packages compare equal (null key) so stability leaves them as given; files rank inside.
        Assert.Equal([zulu, z2, z1, alpha, a1], rows);
    }

    // ── Placing a new package without rebuilding the whole list ───────────────────────────

    [Fact]
    public void IndexForPackage_NoSort_ReturnsEndOfList()
    {
        Package existing = MakePackage("Alpha", "Katfile");
        PackageFile file = AddFile(existing, "a.bin", "Katfile");
        List<object> rows = [existing, file];

        int index = UploadRowOrder.IndexForPackage(rows, MakePackage("Zulu", "Rapidgator"), sort: null);

        Assert.Equal(2, index);
    }

    [Fact]
    public void IndexForPackage_Sorted_ReturnsIndexOfFirstLaterRankingPackage()
    {
        Package alpha = MakePackage("Alpha", "Katfile");
        PackageFile alphaFile = AddFile(alpha, "a.bin", "Katfile");
        Package zulu = MakePackage("Zulu", "Rapidgator");
        PackageFile zuluFile = AddFile(zulu, "z.bin", "Rapidgator");
        List<object> rows = [alpha, alphaFile, zulu, zuluFile];

        int index = UploadRowOrder.IndexForPackage(rows, MakePackage("Mike", "Turbobit"), Sort("Name"));

        // Index 2 is the Zulu PACKAGE row — inserting there puts Mike's block between the two
        // packages, never between Alpha and Alpha's own file.
        Assert.Equal(2, index);
    }

    [Fact]
    public void IndexForPackage_Sorted_RankingLast_ReturnsEndOfList()
    {
        Package alpha = MakePackage("Alpha", "Katfile");
        PackageFile alphaFile = AddFile(alpha, "a.bin", "Katfile");
        List<object> rows = [alpha, alphaFile];

        int index = UploadRowOrder.IndexForPackage(rows, MakePackage("Zulu", "Rapidgator"), Sort("Name"));

        Assert.Equal(2, index);
    }

    [Fact]
    public void IndexForPackage_Sorted_EqualKeys_LandsAfterTheExistingOnes()
    {
        // Same stability rule as a full rebuild: a newcomer that ties joins the back of the tie.
        Package first = MakePackage("Same", "Katfile");
        Package second = MakePackage("Same", "Katfile");
        Package later = MakePackage("Zulu", "Rapidgator");
        List<object> rows = [first, second, later];

        int index = UploadRowOrder.IndexForPackage(rows, MakePackage("Same", "Turbobit"), Sort("Name"));

        Assert.Equal(2, index);
    }

    [Fact]
    public void OrderFiles_RanksWithinThePackage_AndIsPackageOrderWhenUnsorted()
    {
        Package pkg = MakePackage("Holiday", "Rapidgator");
        PackageFile turbo = AddFile(pkg, "one.bin", "Turbobit");
        PackageFile kat = AddFile(pkg, "two.bin", "Katfile");

        Assert.Equal([kat, turbo], UploadRowOrder.OrderFiles(pkg, Sort("HosterDisplay")));
        Assert.Equal([turbo, kat], UploadRowOrder.OrderFiles(pkg, sort: null));
    }

    // ── Persisted form ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("HosterDisplay", ListSortDirection.Ascending, "HosterDisplay|asc")]
    [InlineData("QueueOrder", ListSortDirection.Descending, "QueueOrder|desc")]
    public void Format_RoundTrips(string path, ListSortDirection direction, string expected)
    {
        UploadSort sort = new(path, direction);

        Assert.Equal(expected, sort.Format());
        Assert.True(UploadSort.TryParse(sort.Format(), out UploadSort? parsed));
        Assert.Equal(sort, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HosterDisplay")]
    [InlineData("HosterDisplay|sideways")]
    [InlineData("|asc")]
    [InlineData("a|b|c")]
    public void TryParse_Unreadable_YieldsDefaultOrder(string? stored)
    {
        // A persisted sort is a convenience. Anything we cannot read means default order, never a
        // failed startup.
        Assert.False(UploadSort.TryParse(stored, out UploadSort? parsed));
        Assert.Null(parsed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static UploadSort Sort(string path) => new(path, ListSortDirection.Ascending);

    /// <summary>
    /// Asserts every package row is immediately followed by exactly its own files — the
    /// structural invariant, checked without caring what the ranking itself came out as.
    /// </summary>
    private static void AssertTreeIntact(List<object> rows)
    {
        Package? current = null;
        Dictionary<Package, int> seen = [];
        foreach (object row in rows)
        {
            if (row is Package package)
            {
                Assert.DoesNotContain(package, seen.Keys);
                seen[package] = 0;
                current = package;
                continue;
            }

            PackageFile file = Assert.IsType<PackageFile>(row);
            Assert.Same(current, file.Package);
            seen[file.Package]++;
        }

        foreach ((Package package, int count) in seen)
        {
            Assert.Equal(package.IsExpanded ? package.Count() : 0, count);
        }
    }

    private static Package MakePackage(string name, string hosterName)
    {
        FileHosterClient hoster = new(hosterName, Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = hosterName, IsAnonymous = true };
        PackageOptions options = new()
        {
            Title = name,
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        };
        return new Package(options);
    }

    private static PackageFile MakeFile(Package package, string name, string hosterName)
    {
        FileHosterClient hoster = new(hosterName, Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = hosterName, IsAnonymous = true };
        return new PackageFile(package, @"C:\src\" + name, hoster, login) { Name = name };
    }

    private static PackageFile AddFile(Package package, string name, string hosterName)
    {
        PackageFile file = MakeFile(package, name, hosterName);
        package.AddPackageFiles([file]);
        return file;
    }
}
