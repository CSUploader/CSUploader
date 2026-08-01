// <copyright file="UploadrarPipelineTests.cs" company="CSUploader">
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
/// Uploadrar is a thin shim on <see cref="XFileSharingApiPipeline"/>, so the protocol is already
/// covered by the base's tests. What's specific to this host — and what these cover — is its
/// extension blocklist, which the host itself enforces only AFTER taking the whole file.
/// </summary>
public class UploadrarPipelineTests
{
    private const string UploadServerOkJson = """{"msg":"OK","status":200,"sess_id":"sess_ur","result":"https://fs21.uploadrar.com/cgi-bin/upload.cgi"}""";
    private const string UploadOkJson = """[{"file_code":"ghjbdgxpc0pw","file_status":"OK"}]""";

    [Theory]
    // Verbatim from ?op=api_get_limits → ExtNotAllowed, read live 2026-08-02.
    [InlineData("clip.mp4", true)]
    [InlineData("clip.MKV", true)]     // case-insensitive
    [InlineData("song.mp3", true)]
    [InlineData("movie.avi", true)]    // the one the capture watched upload in full, then get refused
    [InlineData("clip.mpg", true)]
    [InlineData("clip.wmv", true)]
    [InlineData("clip.m4v", true)]
    // …and the shapes this app actually posts, which the same capture uploaded successfully.
    [InlineData("The.Matrix.2160p-GRP.srr", false)]
    [InlineData("release.rar", false)]
    [InlineData("release.r00", false)]
    [InlineData("release.zip", false)]
    [InlineData("noextension", false)]
    public void IsBlockedExtension_MatchesTheHostsPublishedList(string fileName, bool blocked)
        => Assert.Equal(blocked, UploadrarPipeline.IsBlockedExtension(fileName));

    [Fact]
    public async Task RunAsync_BlockedExtension_FailsBeforeSendingAnything()
    {
        // The point of the pre-flight. Uploadrar's own node accepts every byte and only the finalise
        // says {"error":"unallowed extension"} — so without this the whole file is spent to earn a
        // refusal. Neither the server lookup nor the upload may run.
        UploadrarPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not resolve a server"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with { FilePath = @"C:\nope\clip.mkv", FileName = "clip.mkv" };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("MKV", reason, StringComparison.Ordinal);
        Assert.Contains("Archive the file first", reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_AllowedExtension_UploadsNormally()
    {
        // The counterpart: an archive part must not be caught by the guard.
        Queue<string> gets = new([UploadServerOkJson]);
        UploadrarPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(gets.Dequeue()),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>())));

        AttemptContext ctx = MakeContext() with { FilePath = @"C:\nope\release.r00", FileName = "release.r00" };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://uploadrar.com/ghjbdgxpc0pw", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public void Uploadrar_IsAccountOnly_OnTheApiKeyCredentialUi()
    {
        UploadrarPipeline pipeline = new();
        Assert.Equal("Uploadrar", pipeline.Name);

        // Anonymous is off: its api_get_limits reports MaxUploadFilesize 0.00001 to a signed-out
        // caller — the DropGalaxy dialect for "not allowed", not a real cap.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Uploadrar"));
        Assert.Equal("uploadrar.com", FileHosterClient.FileHosters["Uploadrar"]);

        // Standard XFileSharing REST API → the family's sign-in-or-paste-a-key dialog.
        Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode("Uploadrar"));
    }

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
        FilePath = @"C:\nope\release.r00",
        FileName = "release.r00",
        FileSize = 4096,
        HosterName = "Uploadrar",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Uploadrar", ApiKey = "key_ur" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
