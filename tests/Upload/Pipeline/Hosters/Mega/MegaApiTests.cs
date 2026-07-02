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

    // ===== mega.nz account verbs =====

    // The MegaLoginCryptoTests RSA fixture: privk is wrapped with this master key, and decrypting
    // CsidB64 with it yields ExpectedSid. Reused here so LoginAsync is covered end-to-end
    // (derive → k unwrap → privk unwrap → RSA) with zero live traffic.
    private static readonly byte[] _fixtureMasterKey = Convert.FromHexString("101112131415161718191a1b1c1d1e1f");

    private const string FixtureSaltB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string ExpectedSid = "AggPFh0kKzI5QEdOVVxjanF4f4aNlJuiqbC3vsXM09rh6O_2_QQLEhkgJw";

    private const string PrivkB64 =
        "5aAp8gizJjifKI73jMUBrs2oVTBgLaclXfvAsK25R7iPeboz0s2im_AQhFUZ96YclT-k1WORLtXT77-ufAhe5pZ5kUFJo5sA3Ou_anKrbZ6339E3pZ2dD6VQioleRCwofbNMQGyBE-yGryL94LNWhQ5SmdvJnstNd1MIRlgvkxhIRI5kbWcsvTL1e-ZOSm4DxsVtF6SmIzIbyFJN_82fm0g863hUMkSRJjNPb66nrnYLAPQQ2--sEsdR-d1VYaIj6H8t8fMKKgJBC0r3jww7Z0CEBDxchCPLzYp1Cp9ACiyoZ7rHd_DcaCJy8ckBDVdSTp0TynJzXTamsHjiR2JeZ-pIJgmdzFA3ud91oMTYMRMjeKLCdF-TUOrJoH0vk8M8IaGlpyYQLvKy8zx8R50qkdBDJvG3H1Y5CuOSOlRkhT1awfxsiiSptxm-bEEPi_VvDwRccgTqqJfppFYcMNufOs4e9ySUwC9fqGWcl97TJZvmrYqCxXuU5rzG_GLFgfhcMdqCsJrAVkIL-jPkVbM6uYP3BgpfF-mAx-CH4sEaIlCGWwxIDtbEYAtsyIIB_dr5lfmO6qrxt-v1PXfvgynqytZonsGiZD1uy11CRX10_NSnhAHCl_mfE0rpxKfYQoaY8ZzQ9bhFkuNwUsVkXH_qC6XEzsu6wXjaUkyJ3oVLT98vyCecpaniXwE4yMhHLE7WTUifPFWVF3noDDXIxSctcSyjbx4xV9NWa9ccSQtb5MacRkddusOv4RCZcqY4WtW2-jnvpOJNCP9E2UI53uyb9g-hPEMTWI7cbDK5csFHPFioLEcqNwIBz6uqG4kRJXtXo92SAf-gFNfuvnMBJoklir7h9ZBpuybuEs_Rj_d6y-c";

    private const string CsidB64 =
        "B_sGQCttyH4MsttApgbbX1zrn2REyrIssrBJ-vrpmQ45zyyKdteG7YCRx_aPwu6xM4FoRLWWpZlqqbibYSecHnvJ3X1ORhIvFhjic2723z2ag_0UR85UVsD_VSGS4CpUDDXa5n4Ejf7HGhjAlSROsfoOgmt1DA4olJKdh2UigBCY5W-bCLmvyTCK_zYSy_hmQJdZY77_397eglBmkEkcHjcI0rPVwZx14g3RoGnlf-vJV_E9E-EGTP805MsBTx_VpmrIYpHiLsBznM2wX3MtS707S_LrAEzJN27ZEKE1hF_30rXJgFZAMmo4uodkymDcgXy1dGVkkNfC0xehsrU5bMng";

    [Fact]
    public async Task Login_V2_DerivesSendsUhAndRecoversSession()
    {
        // k = the fixture master key wrapped with the v2-derived password key.
        (byte[] pwKey, string expectedUh) = MegaLoginCrypto.DeriveV2(
            "correct horse battery staple", MegaCrypto.B64UrlDecode(FixtureSaltB64));
        string kB64 = MegaCrypto.A32ToB64(MegaCrypto.EncryptKeyEcb(pwKey, MegaCrypto.BytesToA32(_fixtureMasterKey)));

        List<string> sent = [];
        Queue<string> responses = new([
            $"[{{\"s\":\"{FixtureSaltB64}\",\"v\":2}}]",
            $"[{{\"k\":\"{kB64}\",\"privk\":\"{PrivkB64}\",\"csid\":\"{CsidB64}\",\"u\":\"USERhndl\"}}]",
        ]);
        MegaApi api = Make(responses, sent);

        byte[] masterKey = await api.LoginAsync("User@Example.com ", "correct horse battery staple", CancellationToken.None);

        Assert.Equal(_fixtureMasterKey, masterKey);
        Assert.Equal(ExpectedSid, api.Sid);
        Assert.Contains("\"a\":\"us0\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"user\":\"user@example.com\"", sent[0], StringComparison.Ordinal); // trimmed + lowercased
        Assert.Contains($"\"uh\":\"{expectedUh}\"", sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_V1Fallback_UsesLegacyDerivationWhenUs0Errors()
    {
        // us0 errors → legacy path: pwKey = PrepareKeyV1, uh = StringHashV1 (the KAT pair for
        // "legacy-pass-1" / user@example.com), k wrapped with that pwKey.
        byte[] pwKey = Convert.FromHexString("317665eca9bb92cb79fa7bd5a2bb2f69");
        string kB64 = MegaCrypto.A32ToB64(MegaCrypto.EncryptKeyEcb(pwKey, MegaCrypto.BytesToA32(_fixtureMasterKey)));

        List<string> sent = [];
        Queue<string> responses = new([
            "-2",
            $"[{{\"k\":\"{kB64}\",\"privk\":\"{PrivkB64}\",\"csid\":\"{CsidB64}\"}}]",
        ]);
        MegaApi api = Make(responses, sent);

        byte[] masterKey = await api.LoginAsync("user@example.com", "legacy-pass-1", CancellationToken.None);

        Assert.Equal(_fixtureMasterKey, masterKey);
        Assert.Equal(ExpectedSid, api.Sid);
        Assert.Contains("\"uh\":\"hMq2qfHH_68\"", sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WrongPassword_ThrowsWithMegaCode()
    {
        Queue<string> responses = new([
            $"[{{\"s\":\"{FixtureSaltB64}\",\"v\":2}}]",
            "-9", // us: ENOENT — MEGA's wrong-email/password answer
        ]);
        MegaApi api = Make(responses);

        MegaApiException ex = await Assert.ThrowsAsync<MegaApiException>(
            () => api.LoginAsync("user@example.com", "wrong", CancellationToken.None));
        Assert.Equal(-9, ex.Code);
    }

    [Fact]
    public async Task FetchCloudRoot_ReturnsTheTypeTwoNode()
    {
        MegaApi api = Make(new(["[{\"f\":[{\"h\":\"FILEhndl\",\"t\":0},{\"h\":\"ROOThndl\",\"t\":2},{\"h\":\"TRSHhndl\",\"t\":4}]}]"]));

        Assert.Equal("ROOThndl", await api.FetchCloudRootAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PutFileNode_WrapsKeyWithMasterKeyAndReturnsHandle()
    {
        uint[] ulKey = [1, 2, 3, 4, 5, 6];
        List<uint[]> macs = [[7, 8, 9, 10]];
        uint[] expectedFileKey = MegaCrypto.BuildFileKey(ulKey, MegaCrypto.CondenseMacs(macs, ulKey));
        string expectedK = MegaCrypto.A32ToB64(MegaCrypto.EncryptKeyEcb(_fixtureMasterKey, expectedFileKey));

        List<string> sent = [];
        MegaApi api = Make(new(["[{\"f\":[{\"h\":\"NEWNODE1\"}]}]"]), sent);

        (string handle, uint[] fileKey) = await api.PutFileNodeAsync(
            "ROOThndl", completionToken: [1, 2, 3, 4, 5, 6], ulKey, macs, "file.bin", _fixtureMasterKey, CancellationToken.None);

        Assert.Equal("NEWNODE1", handle);
        Assert.Equal(expectedFileKey, fileKey);
        Assert.Contains("\"a\":\"p\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"t\":\"ROOThndl\"", sent[0], StringComparison.Ordinal);
        Assert.Contains($"\"k\":\"{expectedK}\"", sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"i\":", sent[0], StringComparison.Ordinal); // classic shape — an i defers the node to the sc channel
        Assert.DoesNotContain("\"v\":", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportNode_ReturnsPublicHandle()
    {
        List<string> sent = [];
        MegaApi api = Make(new(["[\"PUBhndl12\"]"]), sent);

        Assert.Equal("PUBhndl12", await api.ExportNodeAsync("NEWNODE1", CancellationToken.None));
        Assert.Contains("\"a\":\"l\"", sent[0], StringComparison.Ordinal);
        Assert.Contains("\"n\":\"NEWNODE1\"", sent[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[{\"cstrg\":123456,\"mstrg\":21474836480,\"utype\":0}]", 123456L, 21474836480L, false)]
    [InlineData("[{\"cstrg\":999,\"mstrg\":2199023255552,\"utype\":4}]", 999L, 2199023255552L, true)]
    // The real free-account capture: cstrg/mstrg present, utype omitted → Free.
    [InlineData("[{\"mstrg\":10737418240,\"usl\":0,\"cstrg\":0,\"uslw\":9000,\"balance\":[],\"plans\":[]}]", 0L, 10737418240L, false)]
    public async Task Quota_ParsesStorageAndTier(string response, long used, long total, bool paid)
    {
        MegaApi api = Make(new([response]));

        (long u, long t, bool p) = await api.QuotaAsync(CancellationToken.None);

        Assert.Equal(used, u);
        Assert.Equal(total, t);
        Assert.Equal(paid, p);
    }
}
