// <copyright file="UploadQueueOrderTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Upload;

namespace CSUploader.Tests.Upload;

public class UploadQueueOrderTests
{
    [Fact]
    public void Renumber_AssignsDenseOneToN()
    {
        var files = Make(3);
        UploadQueueOrder.Renumber(files);
        Assert.Equal([1, 2, 3], files.Select(f => f.QueueOrder));
    }

    [Fact]
    public void MoveTo_FirstToLast_ShiftsEverythingElseUp()
    {
        var files = Make(5); // positions 1..5
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveTo(files, files[0], 5);
        // old #1 is now #5; old #2..#5 became #1..#4
        Assert.Equal(5, files[0].QueueOrder);
        Assert.Equal([1, 2, 3, 4, 5], OrderedPositions(files));
        Assert.Equal(files[0], InOrder(files).Last());
    }

    [Fact]
    public void MoveTo_ClampsOutOfRange()
    {
        var files = Make(3);
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveTo(files, files[2], 99);
        Assert.Equal(3, files[2].QueueOrder); // clamped to N
    }

    [Fact]
    public void MoveBy_BlockDown_KeepsRelativeOrder()
    {
        var files = Make(5); // A B C D E
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveBy(files, [files[1], files[2]], +1); // move B,C down 1
        // expected order: A D B C E
        Assert.Equal([files[0], files[3], files[1], files[2], files[4]], InOrder(files));
    }

    [Fact]
    public void MoveBy_ClampsAtTop()
    {
        var files = Make(5); // A B C D E
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveBy(files, [files[3]], -10); // move D far up, clamped to front
        // expected order: D A B C E
        Assert.Equal(1, files[3].QueueOrder);
        Assert.Equal([files[3], files[0], files[1], files[2], files[4]], InOrder(files));
        Assert.Equal([1, 2, 3, 4, 5], OrderedPositions(files));
    }

    [Fact]
    public void MoveBy_ZeroDelta_IsNoOp()
    {
        var files = Make(3);
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveBy(files, [files[1]], 0);
        Assert.Equal([1, 2, 3], files.Select(f => f.QueueOrder));
    }

    private static List<PackageFile> Make(int n)
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
        Package pkg = new(new PackageOptions { Title = "p", FileHosters = new() { { hoster, login } } });
        List<PackageFile> files = [];
        for (int i = 0; i < n; i++)
        {
            files.Add(new PackageFile(pkg, $@"C:\x\f{i}.bin", hoster, login) { QueueOrder = i + 1 });
        }
        return files;
    }

    private static List<PackageFile> InOrder(IEnumerable<PackageFile> files) => [.. files.OrderBy(f => f.QueueOrder)];
    private static int[] OrderedPositions(IEnumerable<PackageFile> files) => [.. InOrder(files).Select(f => f.QueueOrder)];
}
