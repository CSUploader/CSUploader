// <copyright file="UploadsSortCoverageTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CSUploader.Tests.Avalonia;
using CSUploader.Upload;

namespace CSUploader.Tests.Views;

/// <summary>
/// Every column on the Uploads tab must declare a <c>SortMemberPath</c> that names a real row
/// property.
/// <para>
/// Both halves of that shipped broken. Six template columns (Name, Size, Hoster, Status,
/// Progress, Order) declared no path at all, so their headers were inert — a template column has
/// no binding for the DataGrid to derive a path from, and nothing warns you. The fifteen bound
/// columns sorted only because Avalonia derives their path internally: probing
/// <c>DataGridTextColumn.SortMemberPath</c> returns an EMPTY string even with a Binding set, so
/// once the view handles the <c>Sorting</c> event itself, an undeclared path means that column
/// silently stops sorting too. Hence every column, not just the six.
/// </para>
/// <para>
/// The path is checked by reflecting the row types directly rather than through
/// <see cref="UploadRowSortKeys"/>: that helper cannot answer this question, because it returns
/// null both for "no such property" and for a property that is legitimately null right now (a
/// fresh file has no FinishedDate). There is also no normalisation rule here to accidentally
/// re-implement — the path IS the property name.
/// </para>
/// </summary>
public class UploadsSortCoverageTests
{
    /// <summary>Column elements, excluding the nested <c>…Column.CellTemplate</c> property tags.</summary>
    private static readonly Regex ColumnPattern =
        new("<DataGrid(?:Text|Template|CheckBox)Column(?=[\\s>])([^>]*)>", RegexOptions.Compiled);

    private static readonly Regex HeaderPattern = new("Header=\"\\{loc:Loc \\w*?Col_(\\w+)\\}\"", RegexOptions.Compiled);
    private static readonly Regex SortPathPattern = new("SortMemberPath=\"(\\w+)\"", RegexOptions.Compiled);

    [Fact]
    public void EveryUploadsColumn_DeclaresASortMemberPath()
    {
        (string Header, string? Path)[] columns = ReadColumns();

        Assert.NotEmpty(columns);
        string[] missing = [.. columns.Where(c => c.Path is null).Select(c => c.Header)];

        Assert.True(
            missing.Length == 0,
            "These Uploads columns declare no SortMemberPath, so their headers do nothing: "
                + string.Join(", ", missing));
    }

    [Fact]
    public void EveryUploadsSortPath_ResolvesOnAFileRow()
    {
        (string Header, string? Path)[] columns = ReadColumns();

        string[] unresolvable =
        [
            .. columns
                .Where(c => c.Path is not null && !HasProperty(typeof(PackageFile), c.Path!))
                .Select(c => $"{c.Header} -> {c.Path}"),
        ];

        Assert.True(
            unresolvable.Length == 0,
            "These Uploads columns name a property PackageFile does not have — a typo here sorts "
                + "nothing, silently: " + string.Join(", ", unresolvable));
    }

    [Fact]
    public void PackageRows_ResolveEverySortPathExceptQueueOrder()
    {
        // Packages mirror the file row's properties so one flat grid can show both. QueueOrder is
        // the sole exception, and deliberately so: it makes the Order column rank files inside
        // each package while leaving the packages themselves in default order. Pinned here so
        // that stays a decision rather than something a future column drifts into by accident.
        (string Header, string? Path)[] columns = ReadColumns();

        string[] missingOnPackage =
        [
            .. columns
                .Where(c => c.Path is not null && !HasProperty(typeof(Package), c.Path!))
                .Select(c => c.Path!),
        ];

        Assert.Equal(["QueueOrder"], missingOnPackage);
    }

    private static bool HasProperty(Type type, string name)
        => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is not null;

    private static (string Header, string? Path)[] ReadColumns()
    {
        string xaml = File.ReadAllText(Path.Combine(
            RepoXaml.FindRepoRoot(), "src", "CSUploader", "Views", "UploadsView.axaml"));

        int start = xaml.IndexOf("<DataGrid.Columns>", StringComparison.Ordinal);
        int end = xaml.IndexOf("</DataGrid.Columns>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "UploadsView.axaml has no <DataGrid.Columns> block");

        return [.. ColumnPattern.Matches(xaml[start..end])
            .Select(m => m.Groups[1].Value)
            .Select(attributes => (
                Header: HeaderPattern.Match(attributes) is { Success: true } h ? h.Groups[1].Value : "(unnamed)",
                Path: SortPathPattern.Match(attributes) is { Success: true } s ? s.Groups[1].Value : null))];
    }
}
