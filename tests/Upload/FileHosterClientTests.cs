// <copyright file="FileHosterClientTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Tests.Upload;

public class FileHosterClientTests
{
    [Fact]
    public void NamesAlphabetical_ReturnsEveryHosterSortedCaseInsensitive()
    {
        // Pins the UI ordering used by the Add Account dropdown and the upload wizard
        // grid. The master FileHosters dictionary is authored in arbitrary order; the
        // sorted view is what users actually see, so a regression here is visible.
        IReadOnlyList<string> names = FileHosterClient.NamesAlphabetical;

        Assert.Equal(FileHosterClient.FileHosters.Count, names.Count);
        Assert.Equal(
            names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            names.ToArray());
        // No duplicates and no entries the master table doesn't know about.
        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
        Assert.All(names, n => Assert.True(FileHosterClient.FileHosters.ContainsKey(n)));
    }
}
