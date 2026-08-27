// <copyright file="Avalonia12EmptyLineHangTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.UI;

namespace CSUploader.Tests.Avalonia.Lib;

/// <summary>
/// The padder that keeps Avalonia 12.1.1's text formatter away from the one line shape it cannot
/// finish measuring. Pure string logic — the windows that hung without it (the update prompt with
/// notes, any message box whose body holds a blank line) are the integration-level regression net.
/// </summary>
public class Avalonia12EmptyLineHangTests
{
    [Theory]
    [InlineData("a\n\nb", "a\n \nb")]
    [InlineData("a\n\n\nb", "a\n \n \nb")]              // every empty line in a run
    [InlineData("\n\na", " \n \na")]                     // leading blanks
    [InlineData("a\n\n", "a\n \n ")]                     // trailing blanks
    [InlineData("a\r\n\r\nb", "a\r\n \r\nb")]            // CRLF pairs stay pairs
    public void EveryEmptyLine_GainsExactlyOneSpace(string text, string expected)
        => Assert.Equal(expected, Avalonia12EmptyLineHang.PadEmptyLines(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no newlines at all")]
    [InlineData("two\nlines, neither empty")]
    public void TextWithoutEmptyLines_PassesThroughUntouched(string? text)
        => Assert.Same(text, Avalonia12EmptyLineHang.PadEmptyLines(text));
}
