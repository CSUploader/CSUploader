// <copyright file="UploadsViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

public class UploadsViewModelTests
{
    [Fact]
    public void TryBuildExplorerSelectArgument_ExistingFile_ReturnsQuotedSelectArgument()
    {
        string full = Path.Combine(@"C:\src\My Uploads", "movie.mkv");

        string? arg = UploadsViewModel.TryBuildExplorerSelectArgument(@"C:\src\My Uploads", "movie.mkv", p => p == full);

        // /select,"<path>" highlights the file in its folder — comma form, path quoted (handles spaces).
        Assert.Equal($"/select,\"{full}\"", arg);
    }

    [Fact]
    public void TryBuildExplorerSelectArgument_MissingFile_ReturnsNull_SoCallerOpensFolder()
        => Assert.Null(UploadsViewModel.TryBuildExplorerSelectArgument(@"C:\src\pkg", "gone.bin", _ => false));

    [Theory]
    [InlineData(null, "f.bin")]
    [InlineData("", "f.bin")]
    [InlineData(@"C:\d", null)]
    [InlineData(@"C:\d", "")]
    public void TryBuildExplorerSelectArgument_MissingDirectoryOrName_ReturnsNull(string? directory, string? fileName)
        => Assert.Null(UploadsViewModel.TryBuildExplorerSelectArgument(directory, fileName, _ => true));
}
