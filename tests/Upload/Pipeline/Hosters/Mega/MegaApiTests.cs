// <copyright file="MegaApiTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

public class MegaApiTests
{
    private static MegaApi Make(Queue<string> responses, List<string>? sentBodies = null, Func<uint[]>? randKey = null)
        => new(
            (url, body, ct) =>
            {
                sentBodies?.Add(body);
                return Task.FromResult(new HttpResponseSnapshot(200, responses.Dequeue(), []));
            },
            randKey: randKey);

    [Fact]
    public async Task CreateEphemeralSession_CompletesCeremonyAndVerifiesTsid()
    {
        uint[] masterKey = [10, 20, 30, 40];
        Queue<uint[]> keys = new([masterKey, [1, 2, 3, 4], [5, 6, 7, 8]]); // master, pw, ssc

        // A valid tsid: 16 arbitrary bytes, then EncryptKeyEcb(masterKey, first16) — exactly what
        // CreateEphemeralSession re-derives and checks.
        byte[] first16 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        byte[] tail16 = MegaCrypto.A32ToBytes(MegaCrypto.EncryptKeyEcb(MegaCrypto.A32ToBytes(masterKey), MegaCrypto.BytesToA32(first16)));
        string tsidB64 = MegaCrypto.B64UrlEncode([.. first16, .. tail16]);

        List<string> sent = [];
        Queue<string> responses = new(["[\"USERHANDLE\"]", $"[{{\"tsid\":\"{tsidB64}\"}}]"]);
        MegaApi api = Make(responses, sent, randKey: keys.Dequeue);

        uint[] mk = await api.CreateEphemeralSessionAsync(CancellationToken.None);

        Assert.Equal(masterKey, mk);
        Assert.Equal(tsidB64, api.Sid);
        // The up request carried the wrapped master key + ts; the us request carried the user handle.
        Assert.Contains("\"a\":\"up\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"user\":\"USERHANDLE\"", sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Req_NegativeCode_ThrowsWithCode()
    {
        MegaApi api = Make(new(["-9"]));
        MegaApiException ex = await Assert.ThrowsAsync<MegaApiException>(
            () => api.ReqAsync(new { a = "xc", xh = "x" }, CancellationToken.None));
        Assert.Equal(-9, ex.Code);
    }

    [Fact]
    public async Task Req_Eagain_RetriesThenSucceeds()
    {
        // First attempt -3 (EAGAIN) → retry (1s) → success.
        MegaApi api = Make(new(["[-3]", "[0]"]));
        // xc returns [0]; no throw means the retry path worked.
        await api.CloseTransferAsync("xh", CancellationToken.None);
    }

    [Theory]
    [InlineData("[[\"XHaaaaaaaaaa\",\"ROOThndl\"]]")]      // bare [xh, h]
    [InlineData("[[0,[\"XHaaaaaaaaaa\",\"ROOThndl\"]]]")]  // status-wrapped (transfer.it current)
    public async Task CreateTransfer_ParsesBothResponseShapes(string xnResponse)
    {
        MegaApi api = Make(new([xnResponse]), randKey: () => [1, 2, 3, 4]);
        (string xh, string root, uint[] folderKey) = await api.CreateTransferAsync("My Transfer", CancellationToken.None);

        Assert.Equal("XHaaaaaaaaaa", xh);
        Assert.Equal("ROOThndl", root);
        Assert.Equal<uint[]>([1, 2, 3, 4], folderKey);
    }

    [Fact]
    public async Task UploadPools_ParsesAndPicksBySize()
    {
        MegaApi api = Make(new(["[[[\"h1\",\"ul/uri1\",50],[\"h2\",\"ul/uri2\",0]]]"]));
        List<MegaUploadPool> pools = await api.UploadPoolsAsync(CancellationToken.None);

        Assert.Equal(2, pools.Count);
        Assert.Equal("h1", pools[0].Host);
        Assert.Equal("ul/uri1", pools[0].Uri);
        Assert.Equal(50, pools[0].Limit);

        Assert.Equal("h1", MegaApi.PickPool(pools, 10).Host);   // fits the 50-limit pool
        Assert.Equal("h2", MegaApi.PickPool(pools, 9999).Host); // overflows to the no-limit pool
    }

    [Fact]
    public async Task FinaliseFile_SendsXpV3AndReturnsNodeHandle()
    {
        List<string> sent = [];
        MegaApi api = Make(new(["[{\"f\":[{\"h\":\"NODEhndl\"}]}]"]), sent);

        string handle = await api.FinaliseFileAsync(
            "ROOThndl",
            completionToken: [1, 2, 3, 4, 5, 6],
            ulKey: [1, 2, 3, 4, 5, 6],
            macsOrdered: [[7, 8, 9, 10]],
            filename: "file.bin",
            CancellationToken.None);

        Assert.Equal("NODEhndl", handle);
        Assert.Contains("\"a\":\"xp\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"v\":3", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"t\":\"ROOThndl\"", sent[0], StringComparison.Ordinal);
    }
}
