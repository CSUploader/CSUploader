// <copyright file="MimeTypeGuesserTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

public class MimeTypeGuesserTests
{
    [Theory]
    [InlineData(@"C:\videos\Movie.mp4", "video/mp4")]
    [InlineData(@"D:\Music\song.MP3", "audio/mpeg")] // case-insensitive
    [InlineData("archive.zip", "application/zip")]
    [InlineData("archive.rar", "application/vnd.rar")]
    [InlineData("subtitles.srt", "application/x-subrip")]
    [InlineData("photo.JPG", "image/jpeg")]
    public void Guess_KnownExtension_ReturnsSpecificType(string filePath, string expectedMime) => Assert.Equal(expectedMime, MimeTypeGuesser.Guess(filePath));

    [Theory]
    [InlineData("no-extension")]
    [InlineData("something.xyzunknown")]
    [InlineData("")]
    public void Guess_UnknownOrMissingExtension_FallsBackToOctetStream(string filePath) =>
        // The fallback preserves the pre-refactor behaviour — no regression for callers that
        // upload exotic file types.
        Assert.Equal("application/octet-stream", MimeTypeGuesser.Guess(filePath));
}
