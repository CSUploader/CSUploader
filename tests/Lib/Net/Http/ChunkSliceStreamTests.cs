// <copyright file="ChunkSliceStreamTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// Verifies <see cref="ChunkSliceStream"/> serves exactly N bytes from a position-anchored
/// underlying stream and that successive slices advance through the file without copying.
/// This is load-bearing for the chunked upload path — if the slice over-reads, the next
/// chunk skips bytes; if it under-reads, the multipart body's Content-Length lies and the
/// server closes the connection.
/// </summary>
public class ChunkSliceStreamTests
{
    [Fact]
    public void Read_LimitsToSliceLength_EvenWhenInnerStreamHasMore()
    {
        byte[] source = new byte[1000];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i & 0xff);
        }

        using MemoryStream inner = new(source);

        ChunkSliceStream slice = new(inner, sliceLength: 100);
        byte[] buf = new byte[1000];
        int total = 0;
        while (true)
        {
            int n = slice.Read(buf, total, buf.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        Assert.Equal(100, total);
        Assert.Equal(100, inner.Position); // exactly the slice length consumed
    }

    [Fact]
    public async Task ReadAsync_LimitsToSliceLength()
    {
        byte[] source = new byte[1000];
        using MemoryStream inner = new(source);
        ChunkSliceStream slice = new(inner, sliceLength: 250);

        byte[] buf = new byte[1000];
        int total = 0;
        int n;
        while ((n = await slice.ReadAsync(buf.AsMemory(total))) > 0)
        {
            total += n;
        }
        Assert.Equal(250, total);
    }

    [Fact]
    public void SuccessiveSlices_AdvanceThroughTheUnderlyingStream()
    {
        // The realistic scenario: open a file once, slice it twice for two consecutive
        // chunks. The second slice picks up where the first left off — no extra plumbing.
        byte[] source = new byte[300];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)i;
        }

        using MemoryStream inner = new(source);

        byte[] firstChunk = ReadFully(new ChunkSliceStream(inner, 100));
        byte[] secondChunk = ReadFully(new ChunkSliceStream(inner, 100));
        byte[] thirdChunk = ReadFully(new ChunkSliceStream(inner, 100));

        Assert.Equal(100, firstChunk.Length);
        Assert.Equal(100, secondChunk.Length);
        Assert.Equal(100, thirdChunk.Length);
        Assert.Equal((byte)0, firstChunk[0]);
        Assert.Equal((byte)100, secondChunk[0]);
        Assert.Equal((byte)200, thirdChunk[0]);
    }

    [Fact]
    public void Length_AndPosition_ReflectSliceWindow()
    {
        using MemoryStream inner = new(new byte[500]);
        ChunkSliceStream slice = new(inner, 200);
        Assert.Equal(200, slice.Length);
        Assert.Equal(0, slice.Position);

        byte[] buf = new byte[50];
        slice.ReadExactly(buf, 0, 50);
        Assert.Equal(50, slice.Position);
    }

    [Fact]
    public void Dispose_DoesNotCloseInnerStream()
    {
        // The pipeline opens one FileStream and slices it N times — each slice must NOT
        // close the underlying file when disposed. (StreamContent disposes its content
        // after sending; in our case the "content" is the slice, not the file.)
        using MemoryStream inner = new(new byte[100]);
        ChunkSliceStream slice = new(inner, 50);
        slice.Dispose();

        // If the inner had been closed, this would throw ObjectDisposedException.
        inner.Position = 0;
        Assert.Equal(0, inner.Position);
    }

    private static byte[] ReadFully(Stream s)
    {
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
