// <copyright file="ColumnValueExtractorTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

public class ColumnValueExtractorTests
{
    // ── Uploaded tab (UploadedFileRow) ──

    [Fact]
    public void Extract_UploadedRow_NameMapsToFileName()
    {
        UploadedFileRow row = new() { FileName = "x.rar" };

        Assert.Equal("x.rar", ColumnValueExtractor.Extract(row, "Name", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_PathMapsToFileDirectory()
    {
        UploadedFileRow row = new() { FileDirectory = @"C:\stuff" };

        Assert.Equal(@"C:\stuff", ColumnValueExtractor.Extract(row, "Path", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_HosterMapsToFileHosterName()
    {
        UploadedFileRow row = new() { FileHosterName = "Rapidgator" };

        Assert.Equal("Rapidgator", ColumnValueExtractor.Extract(row, "Hoster", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_AccountMapsToAccountDisplay()
    {
        UploadedFileRow row = new() { AccountDisplay = "bob@example.com" };

        Assert.Equal("bob@example.com", ColumnValueExtractor.Extract(row, "Account", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_FinishedFormatsAsInvariantTimestamp()
    {
        UploadedFileRow row = new() { FinishedDateTime = new DateTime(2025, 6, 1, 12, 34, 56, DateTimeKind.Local) };

        Assert.Equal("2025-06-01 12:34:56", ColumnValueExtractor.Extract(row, "Finished", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_StartedMapsToStartedDateTime()
    {
        UploadedFileRow row = new() { StartedDateTime = new DateTime(2025, 6, 1, 9, 30, 0, DateTimeKind.Local) };

        Assert.Equal("2025-06-01 09:30:00", ColumnValueExtractor.Extract(row, "Started", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadsRow_StartedMapsToStartedDate()
    {
        // The Uploads tab reflects against the live row VM; the "Started" key maps to the
        // in-memory StartedDate property (DateTime?). Any row exposing StartedDate works.
        UploadsRowStub row = new() { StartedDate = new DateTime(2025, 6, 1, 9, 30, 0, DateTimeKind.Local) };

        Assert.Equal("2025-06-01 09:30:00", ColumnValueExtractor.Extract(row, "Started", isUploadsTab: true));
    }

    private sealed class UploadsRowStub
    {
        public DateTime? StartedDate { get; set; }
    }

    [Fact]
    public void Extract_UploadedRow_UrlMapsToFileUrl()
    {
        UploadedFileRow row = new() { FileUrl = "https://example/x.html" };

        Assert.Equal("https://example/x.html", ColumnValueExtractor.Extract(row, "URL", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_HashMapsToFileHash()
    {
        UploadedFileRow row = new() { FileHash = "deadbeef" };

        Assert.Equal("deadbeef", ColumnValueExtractor.Extract(row, "Hash", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_SizeRendersRawByteCount()
    {
        UploadedFileRow row = new() { FileSize = 1090519040 };

        // Raw value is more useful for clipboard than a formatted "1.0 GB" — users can
        // re-format if needed. Long is IFormattable so CurrentCulture grouping may add
        // separators; either rendering of the raw number is acceptable here.
        string? actual = ColumnValueExtractor.Extract(row, "Size", isUploadsTab: false);
        Assert.NotNull(actual);
        Assert.Contains("1090519040".Replace(",", string.Empty, StringComparison.Ordinal), actual!.Replace(",", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_UploadedRow_EmptyStringTreatedAsNull()
    {
        UploadedFileRow row = new() { FileUrl = string.Empty };

        Assert.Null(ColumnValueExtractor.Extract(row, "URL", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_NullPropertyReturnsNull()
    {
        UploadedFileRow row = new() { FileHash = null };

        Assert.Null(ColumnValueExtractor.Extract(row, "Hash", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_UnknownColumnFallsBackToReflection()
    {
        UploadedFileRow row = new() { PackageName = "Pack" };

        // PackageName isn't in the map, so the fallback uses the key as the property name.
        Assert.Equal("Pack", ColumnValueExtractor.Extract(row, "PackageName", isUploadsTab: false));
    }

    [Fact]
    public void Extract_UploadedRow_NonExistentPropertyReturnsNull()
    {
        UploadedFileRow row = new();

        Assert.Null(ColumnValueExtractor.Extract(row, "TotallyMadeUpKey", isUploadsTab: false));
    }
}
