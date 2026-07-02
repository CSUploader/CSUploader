// <copyright file="MegaPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using CSUploader.Upload.Pipeline.Hosters.Mega;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// Orchestration tests for <see cref="MegaPipeline"/> — the account-upload glue. The MEGA API and
/// the WebSocket upload are stubbed (their internals are KAT-tested elsewhere; the real wire is the
/// live test), so these lock in the event sequence, the share URL (with the key fragment), and the
/// account-check mapping. The login leg runs the real crypto against the stage-1 RSA fixture.
/// </summary>
public class MegaPipelineTests
{
    // The MegaLoginCryptoTests fixture — privk is wrapped with this master key; decrypting CsidB64
    // with it yields a valid session id. Password "correct horse battery staple" + this salt derive
    // the v2 password key that k is wrapped with.
    private static readonly byte[] FixtureMasterKey = Convert.FromHexString("101112131415161718191a1b1c1d1e1f");
    private const string FixtureSaltB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string Password = "correct horse battery staple";

    private const string PrivkB64 =
        "5aAp8gizJjifKI73jMUBrs2oVTBgLaclXfvAsK25R7iPeboz0s2im_AQhFUZ96YclT-k1WORLtXT77-ufAhe5pZ5kUFJo5sA3Ou_anKrbZ6339E3pZ2dD6VQioleRCwofbNMQGyBE-yGryL94LNWhQ5SmdvJnstNd1MIRlgvkxhIRI5kbWcsvTL1e-ZOSm4DxsVtF6SmIzIbyFJN_82fm0g863hUMkSRJjNPb66nrnYLAPQQ2--sEsdR-d1VYaIj6H8t8fMKKgJBC0r3jww7Z0CEBDxchCPLzYp1Cp9ACiyoZ7rHd_DcaCJy8ckBDVdSTp0TynJzXTamsHjiR2JeZ-pIJgmdzFA3ud91oMTYMRMjeKLCdF-TUOrJoH0vk8M8IaGlpyYQLvKy8zx8R50qkdBDJvG3H1Y5CuOSOlRkhT1awfxsiiSptxm-bEEPi_VvDwRccgTqqJfppFYcMNufOs4e9ySUwC9fqGWcl97TJZvmrYqCxXuU5rzG_GLFgfhcMdqCsJrAVkIL-jPkVbM6uYP3BgpfF-mAx-CH4sEaIlCGWwxIDtbEYAtsyIIB_dr5lfmO6qrxt-v1PXfvgynqytZonsGiZD1uy11CRX10_NSnhAHCl_mfE0rpxKfYQoaY8ZzQ9bhFkuNwUsVkXH_qC6XEzsu6wXjaUkyJ3oVLT98vyCecpaniXwE4yMhHLE7WTUifPFWVF3noDDXIxSctcSyjbx4xV9NWa9ccSQtb5MacRkddusOv4RCZcqY4WtW2-jnvpOJNCP9E2UI53uyb9g-hPEMTWI7cbDK5csFHPFioLEcqNwIBz6uqG4kRJXtXo92SAf-gFNfuvnMBJoklir7h9ZBpuybuEs_Rj_d6y-c";

    private const string CsidB64 =
        "B_sGQCttyH4MsttApgbbX1zrn2REyrIssrBJ-vrpmQ45zyyKdteG7YCRx_aPwu6xM4FoRLWWpZlqqbibYSecHnvJ3X1ORhIvFhjic2723z2ag_0UR85UVsD_VSGS4CpUDDXa5n4Ejf7HGhjAlSROsfoOgmt1DA4olJKdh2UigBCY5W-bCLmvyTCK_zYSy_hmQJdZY77_397eglBmkEkcHjcI0rPVwZx14g3RoGnlf-vJV_E9E-EGTP805MsBTx_VpmrIYpHiLsBznM2wX3MtS707S_LrAEzJN27ZEKE1hF_30rXJgFZAMmo4uodkymDcgXy1dGVkkNfC0xehsrU5bMng";

    [Fact]
    public void Properties_DeclareMegaConfig()
    {
        MegaPipeline pipeline = new();
        Assert.Equal("MEGA", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("MEGA"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_UploadsAndReturnsMegaLinkWithKeyFragment()
    {
        MegaApi api = StubApi(LoginResponses(
            "[{\"f\":[{\"h\":\"ROOThndl\",\"t\":2}]}]",  // f (Cloud Drive root)
            "[[[\"h1\",\"ul/u1\",0]]]",                    // usc
            "[{\"f\":[{\"h\":\"NEWNODE1\"}]}]",           // p (attach)
            "[\"PUBhndl12\"]"));                            // l (export)

        // The pipeline mints its own ulKey; capture the one handed to the upload so the expected
        // file key (and thus the link fragment) is computed from the exact same key.
        List<uint[]> macs = [[7, 8, 9, 10]];
        uint[] capturedUlKey = [];
        MegaPipeline pipeline = new(
            _ => api,
            (pool, ctx, key, progress, ct) =>
            {
                capturedUlKey = key;
                progress(ctx.FileSize, ctx.FileSize);
                return Task.FromResult(((byte[])[1, 2, 3], macs));
            });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferProgress);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());

        // Link carries the public handle + the plain file key in the fragment (never sent to the server).
        uint[] fileKey = MegaCrypto.BuildFileKey(capturedUlKey, MegaCrypto.CondenseMacs(macs, capturedUlKey));
        Assert.Equal($"https://mega.nz/file/PUBhndl12#{MegaCrypto.A32ToB64(fileKey)}", done.FileUrl);
    }

    [Fact]
    public async Task RunAsync_LoginFails_YieldsAttemptFailedWithoutUpload()
    {
        // us0 ok, us → -9 (wrong password) before any upload.
        MegaApi api = StubApi([$"[{{\"s\":\"{FixtureSaltB64}\",\"v\":2}}]", "-9"]);

        bool uploadRan = false;
        MegaPipeline pipeline = new(
            _ => api,
            (pool, ctx, key, progress, ct) => { uploadRan = true; return Task.FromResult(((byte[])[], (List<uint[]>)[])); });

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.False(uploadRan);
    }

    [Fact]
    public async Task RunAsync_UploadFault_PropagatesAsRetryableBodyTransfer()
    {
        // Login + f + usc succeed, then the WS upload faults. The node (p) never ran, so it must
        // surface as UploadBodyTransferException → the retry layer re-runs on a fresh session.
        MegaApi api = StubApi(LoginResponses("[{\"f\":[{\"h\":\"ROOThndl\",\"t\":2}]}]", "[[[\"h1\",\"ul/u1\",0]]]"));
        MegaPipeline pipeline = new(
            _ => api,
            (pool, ctx, key, progress, ct) =>
                Task.FromException<(byte[], List<uint[]>)>(new System.Net.WebSockets.WebSocketException("connection dropped")));

        UploadBodyTransferException ex = await Assert.ThrowsAsync<UploadBodyTransferException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    /// <summary>The two login responses (us0 salt/version + us with the fixture k/privk/csid),
    /// followed by whatever post-login responses the test needs.</summary>
    private static string[] LoginResponses(params string[] afterLogin)
    {
        (byte[] pwKey, _) = MegaLoginCrypto.DeriveV2(Password, MegaCrypto.B64UrlDecode(FixtureSaltB64));
        string kB64 = MegaCrypto.A32ToB64(MegaCrypto.EncryptKeyEcb(pwKey, MegaCrypto.BytesToA32(FixtureMasterKey)));
        return
        [
            $"[{{\"s\":\"{FixtureSaltB64}\",\"v\":2}}]",
            $"[{{\"k\":\"{kB64}\",\"privk\":\"{PrivkB64}\",\"csid\":\"{CsidB64}\"}}]",
            .. afterLogin,
        ];
    }

    private static MegaApi StubApi(string[] responses)
    {
        Queue<string> queue = new(responses);
        return new MegaApi((url, body, ct) => Task.FromResult(new HttpResponseSnapshot(200, queue.Dequeue(), [])));
    }

    private static async Task<List<UploadEvent>> Drain(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeContext()
    {
        return new AttemptContext
        {
            AttemptId = Guid.NewGuid(),
            FilePath = @"C:\nope\x.bin",
            FileName = "x.bin",
            FileSize = 100,
            HosterName = "MEGA",
            Credentials = new FileHosterLoginDto { FileHosterName = "MEGA", Username = "user@example.com", Password = Password },
            Proxy = ProxyChoice.Direct,
            Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };
    }
}
