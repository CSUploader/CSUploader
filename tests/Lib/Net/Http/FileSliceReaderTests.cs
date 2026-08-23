// <copyright file="FileSliceReaderTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// Independent readers over regions of one file. This is what makes parallel part uploads possible:
/// <see cref="ChunkSliceStream"/> deliberately shares one caller-owned <see cref="FileStream"/> and
/// rides its advancing position, which is correct in order and completely wrong the moment two
/// parts are in flight together.
/// </summary>
public class FileSliceReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"csu-slice-{Guid.NewGuid():N}.bin");

    public FileSliceReaderTests()
    {
        byte[] content = new byte[8192];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = Pattern(i);
        }

        File.WriteAllBytes(_path, content);
    }

    /// <summary>251 is prime and coprime with every power of two, so no two 4 KiB regions of the
    /// file share a byte pattern — which is what lets a wrongly-offset read be detected.</summary>
    private static byte Pattern(int index) => (byte)(index % 251);

    private static byte[] Expected(int from, int count)
        => [.. Enumerable.Range(from, count).Select(i => Pattern(i))];

    private static async Task<byte[]> DrainAsync(Stream stream)
    {
        using (stream)
        {
            using MemoryStream sink = new();
            await stream.CopyToAsync(sink);
            return sink.ToArray();
        }
    }

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Slices_ReadTheirOwnRegions_Concurrently()
    {
        using FileSliceReader reader = new(_path);

        byte[][] halves = await Task.WhenAll(
            DrainAsync(reader.OpenSlice(0, 4096)),
            DrainAsync(reader.OpenSlice(4096, 4096)));

        Assert.Equal(Expected(0, 4096), halves[0]);
        Assert.Equal(Expected(4096, 4096), halves[1]);
    }

    /// <summary>
    /// The bug this class was written wrong for once already: in the synchronous override the
    /// <c>Stream.Read</c> parameter named <c>offset</c> is the BUFFER offset, and it shadowed the
    /// slice's FILE offset. A slice starting at 4096 read into buffer position 20 read file byte 20.
    /// </summary>
    [Fact]
    public void Read_UsesTheFileOffset_NotTheBufferOffset()
    {
        using FileSliceReader reader = new(_path);
        byte[] buffer = new byte[1024];

        using Stream slice = reader.OpenSlice(4096, 512);
        int read = slice.Read(buffer, 20, 512);

        Assert.Equal(512, read);
        Assert.Equal(Expected(4096, 512), buffer[20..532]);
    }

    /// <summary>UploadNow retries a part by re-invoking its delegate; a consumed slice would send
    /// EOF on the retry instead of the bytes.</summary>
    [Fact]
    public void OpenSlice_CanBeCalledTwiceForTheSameRegion()
    {
        using FileSliceReader reader = new(_path);

        Assert.Equal(1024, reader.OpenSlice(0, 1024).Length);
        Assert.Equal(1024, reader.OpenSlice(0, 1024).Length);
    }

    [Fact]
    public void OpenSlice_RejectsRangesOutsideTheFile()
    {
        using FileSliceReader reader = new(_path);

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(8000, 1024));

        // The overflow case: written as `fileOffset + length > FileLength` this wraps negative and
        // sails through the check.
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void FileLength_IsTheWholeFile_WhileASliceLengthIsItsOwn()
    {
        using FileSliceReader reader = new(_path);

        Assert.Equal(8192, reader.FileLength);

        // HttpContent reads Length to set Content-Length; the file's own length would be wrong.
        Assert.Equal(1024, reader.OpenSlice(4096, 1024).Length);
    }

    [Fact]
    public void Read_ValidatesItsArgumentsRatherThanReturningZero()
    {
        using FileSliceReader reader = new(_path);
        using Stream slice = reader.OpenSlice(0, 1024);

        // Computing the allowed slice first swallows this: count == -1 makes `allowed` negative,
        // the guard returns 0, and the caller sees a silent EOF.
        Assert.Throws<ArgumentOutOfRangeException>(() => slice.Read(new byte[10], 0, -1));
    }
}
