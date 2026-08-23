// <copyright file="DataVaultsPipelineTests.cs" company="CSUploader">
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
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Data Vaults on the API-key path. The protocol is the family's, documented by the host itself at
/// <c>/pages/api</c>, so what's worth pinning is the one thing this host does that no sibling did:
/// answer a discarded upload with a SUCCESS shape carrying <c>file_code: "undef"</c>.
/// </summary>
public class DataVaultsPipelineTests
{
    private const string UploadServerJson = """{"status":200,"sess_id":"sess_demo_16ch","result":"https://d164.datavaults.co/cgi-bin/upload.cgi","msg":"OK"}""";

    [Fact]
    public async Task RunAsync_ApiPath_UploadsAndBuildsTheLink()
    {
        List<string> getUrls = [];
        List<(string Endpoint, IReadOnlyDictionary<string, string> Fields)> calls = [];
        DataVaultsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadServerJson); },
            uploadOverride: (_, endpoint, extra, _, _) =>
            {
                calls.Add((endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"t0evpf0suani","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://datavaults.co/t0evpf0suani", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // Step 1 of the host's own documented flow, key and all.
        Assert.Contains(getUrls, u => u.StartsWith("https://datavaults.co/api/upload/server", StringComparison.Ordinal));

        (string endpoint, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);
        Assert.Equal("https://d164.datavaults.co/cgi-bin/upload.cgi", endpoint);
        Assert.Equal("sess_demo_16ch", fields["sess_id"]); // the API's sess_id, not the key
    }

    [Fact]
    public async Task RunAsync_ServerAcceptsButStoresNothing_IsAFailure_NotALinkToUndef()
    {
        // The reason the base gained an "undef" guard. Probed live: an unauthenticated post to a real
        // node answers [{"file_status":"OK","file_code":"undef"}] — a SUCCESS shape for an upload it
        // threw away. Reading that as a code hands the user https://datavaults.co/undef and calls the
        // transfer finished, which is worse than any error.
        DataVaultsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadServerJson),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """[{"file_status":"OK","file_code":"undef"}]""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("undef", reason, StringComparison.Ordinal);
        Assert.Contains("stored nothing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CloudflareFiveTwenty_IsRetried_NotFailed()
    {
        // Reported live: GET /api/upload/server answered HTTP 520 with the body "error code: 520",
        // while the same key returned 200 on eight consecutive calls seconds later. A yielded
        // AttemptFailed is terminal — AttemptRunner only re-runs on its two never-double-create faults
        // — so without a retry that momentary edge blip loses the user's file.
        int lookups = 0;
        DataVaultsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(++lookups == 1 ? "error code: 520" : UploadServerJson),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """[{"file_code":"t0evpf0suani","file_status":"OK"}]""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://datavaults.co/t0evpf0suani", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal(2, lookups);
    }

    [Fact]
    public async Task RunAsync_RejectedKey_IsNotRetried()
    {
        // The counterpart guard: a JSON verdict is the API deciding, and re-asking would only earn the
        // same answer. Only UNREADABLE responses get another go.
        int lookups = 0;
        DataVaultsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => { lookups++; return Task.FromResult("""{"status":403,"msg":"Wrong auth"}"""); },
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal(1, lookups);
    }

    [Theory]
    [InlineData("error code: 520", true)]
    [InlineData("  error code: 522\n", true)]
    [InlineData("", true)]
    [InlineData("<html><head><title>datavaults</title></head><body>Error 520 <a>cloudflare</a></body></html>", true)]
    [InlineData("<html><body><form id=\"uploadfile\" action=\"https://d1.datavaults.co/cgi-bin/upload.cgi\"></form></body></html>", false)]
    [InlineData("<html><body>Login</body></html>", false)]
    public void LooksLikeEdgeFailure_SeparatesInfrastructureFromPages(string body, bool expected)
        => Assert.Equal(expected, XFileSharingApiPipeline.LooksLikeEdgeFailure(body));

    [Fact]
    public async Task RunAsync_FileOverTheFiveGigabyteCap_RejectedBeforeAnyTransfer()
    {
        List<string> endpoints = [];
        DataVaultsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(UploadServerJson),
            uploadOverride: (_, endpoint, _, _, _) =>
            {
                endpoints.Add(endpoint);
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = (5120L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(endpoints);
    }

    [Fact]
    public void DataVaults_IsAccountOnly_OnTheApiKeyCredential()
    {
        DataVaultsPipeline pipeline = new();
        Assert.Equal("DataVaults", pipeline.Name);
        Assert.Equal(5120L * 1024 * 1024, pipeline.MaxFileSize);

        // Probed 2026-08-02: an anonymous import_file answers "uploads are not enabled for your
        // account type".
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.Equal("datavaults.co", FileHosterClient.FileHosters["DataVaults"]);

        // ApiKey, not SessionCookie: unlike DDownload the key is one click away on My Account, and the
        // base's existing generate_api_key step drives that link.
        Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode("DataVaults"));

        // Four concurrent, measured: at 5 the origin serves four and 520s the fifth, every time.
        // Without this the scheduler's default of 5 lands exactly on the failure.
        Assert.Equal(4, pipeline.MaxConcurrentUploadsFor(new FileHosterLoginDto { FileHosterName = "DataVaults" }));
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\release.rar",
        FileName = "release.rar",
        FileSize = 100,
        HosterName = "DataVaults",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "DataVaults", ApiKey = "demo_api_key" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }
}
