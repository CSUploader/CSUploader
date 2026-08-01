// <copyright file="FilestankPipelineTests.cs" company="CSUploader">
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
/// Filestank on the YetiShare API: authorise with two account keys, then a multipart upload. Response
/// shapes are from the host's own published API docs (its live /authorize refusals were probed
/// 2026-08-01). What's pinned hardest is the token reuse the API explicitly asks for, and the
/// per-file <c>error</c> that rides inside an otherwise-successful 200.
/// </summary>
public class FilestankPipelineTests
{
    private const string Key1 = "k1_0000000000000000000000000000000000000000000000000000000000000";
    private const string Key2 = "k2_0000000000000000000000000000000000000000000000000000000000000";

    private const string AuthOkJson = """{"data":{"access_token":"tok_demo","account_id":"158642"},"_status":"success"}""";
    private const string UploadOkJson = """{"response":"File uploaded","data":[{"name":"x.zip","size":"4096","error":null,"url":"https://www.filestank.com/2Vv","delete_url":"https://www.filestank.com/2Vv~d?abc"}]}""";

    [Fact]
    public async Task RunAsync_AuthorisesThenUploads_AndReturnsTheServersUrl()
    {
        List<string> authCalls = [];
        List<UploadCall> uploads = [];

        FilestankPipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                authCalls.Add(url);
                Assert.Equal(Key1, form["key1"]);
                Assert.Equal(Key2, form["key2"]);
                return Task.FromResult(new HttpResponseSnapshot(200, AuthOkJson, Array.Empty<string>()));
            },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://www.filestank.com/2Vv", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.EndsWith("/api/v2/authorize", Assert.Single(authCalls), StringComparison.Ordinal);

        UploadCall call = Assert.Single(uploads);
        Assert.EndsWith("/api/v2/file/upload", call.Endpoint, StringComparison.Ordinal);
        Assert.Equal("tok_demo", call.ExtraFields["access_token"]);
        Assert.Equal("158642", call.ExtraFields["account_id"]);
    }

    [Fact]
    public async Task RunAsync_SecondUpload_ReusesTheAccessToken()
    {
        // The API's own docs: "you shouldn't generate a new access_token for each request." A batch of
        // 80 files must cost one authorise, not 80.
        int auths = 0;
        FilestankPipeline pipeline = new(
            postFormOverride: (_, _) =>
            {
                auths++;
                return Task.FromResult(new HttpResponseSnapshot(200, AuthOkJson, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal(1, auths);
    }

    [Fact]
    public async Task RunAsync_ExpiredToken_ReAuthorisesOnceAndSucceeds()
    {
        int auths = 0, uploadAttempts = 0;
        FilestankPipeline pipeline = new(
            postFormOverride: (_, _) =>
            {
                auths++;
                return Task.FromResult(new HttpResponseSnapshot(200, AuthOkJson, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadAttempts++;
                return Task.FromResult(uploadAttempts == 1
                    ? new HttpResponseSnapshot(200, """{"_status":"error","response":"Invalid access_token supplied."}""", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(2, auths);           // initial + one refresh
        Assert.Equal(2, uploadAttempts);  // and exactly one re-send
        Assert.Single(events.OfType<TransferStarted>()); // one transfer as far as the UI is concerned
    }

    [Fact]
    public async Task RunAsync_PerFileError_IsReportedEvenThoughTheEnvelopeLooksFine()
    {
        // YetiShare puts a per-file "error" INSIDE a 200 with a data array — the Krakenfiles shape.
        // Reading past it to a missing url would report something far less useful.
        FilestankPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, AuthOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """{"response":"File uploaded","data":[{"name":"x.zip","error":"File too large","url":null}]}""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("File too large", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_WithoutBothKeys_FailsBeforeAnyRequest()
    {
        FilestankPipeline pipeline = new(
            postFormOverride: (_, _) => throw new InvalidOperationException("must not authorise"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with
        {
            Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Filestank", Username = Key1 }, // key2 missing
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("API key", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task CheckAccountAsync_NeverReturnsADerivedUsername()
    {
        // The Settings VM copies DerivedUsername straight onto the DTO's Username. For every other
        // hoster that field is a display name; here it is key1, half the credential. Surfacing the
        // account id would overwrite it and break the account on its own verify — so this stays null
        // and the id lives in the message.
        FilestankPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, AuthOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            Key1, Key2, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.DerivedUsername);
        Assert.Contains("158642", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccountAsync_BadKeyPair_SurfacesTheHostsOwnWording()
    {
        FilestankPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                403, """{"_status":"error","response":"Could not authenticate user. The key pair may be invalid."}""", Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("upload must not run during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            Key1, Key2, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("key pair may be invalid", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuthOkJson, "tok_demo", "158642")]
    [InlineData("""{"data":{"access_token":"t","account_id":158642}}""", "t", "158642")] // numeric account_id
    [InlineData("""{"_status":"error","response":"Could not authenticate user. The key pair may be invalid."}""", null, null)]
    [InlineData("""{"data":{"account_id":"1"}}""", null, null)]
    [InlineData("nonsense", null, null)]
    public void ParseAuthorizeResponse_ReadsTheTokenOrRefuses(string json, string? token, string? accountId)
    {
        (string? gotToken, string? gotAccount, string? error) = FilestankPipeline.ParseAuthorizeResponse(json);
        Assert.Equal(token, gotToken);
        Assert.Equal(accountId, gotAccount);
        if (token is null && json.Contains("key pair", StringComparison.Ordinal))
        {
            Assert.Contains("key pair", error!, StringComparison.Ordinal); // the host's own words
        }
    }

    [Fact]
    public void Filestank_IsAccountOnly_AndUsesThePlainTwoFieldCredentialUi()
    {
        FilestankPipeline pipeline = new();
        Assert.Equal("Filestank", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize); // no published per-file figure — the server decides
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Filestank"));
        Assert.Equal("www.filestank.com", FileHosterClient.FileHosters["Filestank"]);

        // Two keys need two fields: the API-key mode's single paste box plus a sign-in button that
        // could never produce them would be worse.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("Filestank"));
    }

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 4096,
        HosterName = "Filestank",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Filestank", Username = Key1, Password = Key2 },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> ExtraFields);
}
