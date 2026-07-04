// <copyright file="UfileIoPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Net.Sockets;
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
/// Orchestration tests for <see cref="UfileIoPipeline"/> — the anonymous chunked flow: GET / (csrf +
/// session) → select_storage → create_session → 1-based chunks → finalise. The GET, the urlencoded POSTs,
/// and the chunk POST are stubbed via the test ctor (the whole flow was verified live), so these lock in
/// the step sequence, the csrf/field wiring, the chunk indexing, the share link, cookie reuse, and the
/// failure/retry branches.
/// </summary>
public class UfileIoPipelineTests
{
    [Fact]
    public void Properties_DeclareUfileConfig()
    {
        UfileIoPipeline pipeline = new();
        Assert.Equal("Ufile", pipeline.Name);
        Assert.Equal(5L * 1024 * 1024 * 1024, pipeline.MaxFileSize); // free/anonymous cap (5 GB)
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Ufile"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_RunsTheFullFlowAndReturnsLink()
    {
        FakeServer server = new();
        UfileIoPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://ufile.io/abc123", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // select_storage carried the csrf token; create_session carried the file size.
        Assert.Equal("CSRF_TOK", server.LastSelectForm!["csrf_test_name"]);
        Assert.Equal("5225142", server.LastCreateForm!["file_size"]);
        // create_session + finalise target the returned storage node.
        Assert.StartsWith("https://store-eu-hz-3.ufile.io/", server.LastFinaliseUrl!, StringComparison.Ordinal);

        // One chunk (5 MB < 99 MB), index 1, carrying the fuid.
        ChunkCall chunk = Assert.Single(server.Chunks);
        Assert.Equal("1", chunk.Fields["chunk_index"]);
        Assert.Equal("FUID123", chunk.Fields["fuid"]);
        Assert.Contains("//v1/upload/chunk", chunk.Url, StringComparison.Ordinal); // intentional double slash

        // finalise carried everything ufile needs.
        Assert.Equal("FUID123", server.LastFinaliseForm!["fuid"]);
        Assert.Equal("clip.avi", server.LastFinaliseForm["file_name"]);
        Assert.Equal("avi", server.LastFinaliseForm["file_type"]);
        Assert.Equal("1", server.LastFinaliseForm["total_chunks"]);
        // Anonymous: finalise ties the file to the browser session, and no x-api-key is sent.
        Assert.Equal("SESS_ID", server.LastFinaliseForm["session_id"]);
        Assert.False(server.LastFinaliseHeaders!.ContainsKey("x-api-key"));
    }

    [Fact]
    public async Task RunAsync_RegisteredAccount_SendsApiKeyHeaderAndDashboardFinalise()
    {
        FakeServer server = new();
        UfileIoPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(apiKey: "MYAPIKEY"), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());

        // x-api-key rides on select_storage + finalise (identifies the account).
        Assert.Equal("MYAPIKEY", server.LastSelectHeaders!["x-api-key"]);
        Assert.Equal("MYAPIKEY", server.LastFinaliseHeaders!["x-api-key"]);

        // Finalise lands the file in the account: dashboard=true + folder_id, and NO session_id.
        Assert.Equal("true", server.LastFinaliseForm!["dashboard"]);
        Assert.Equal("0", server.LastFinaliseForm["folder_id"]);
        Assert.False(server.LastFinaliseForm.ContainsKey("session_id"));
    }

    [Fact]
    public void ParseProbe_ReadsApiKeyStorageAndTier()
    {
        (string? key, long? used, long? quota, string? tier) = UfileIoPipeline.ParseProbe(
            """{"apiKey":"b901c16f9ea997f0d295593a11c0112f","storageUsed":1024,"storageQuota":10737418240,"tier":"pro"}""");
        Assert.Equal("b901c16f9ea997f0d295593a11c0112f", key);
        Assert.Equal(1024, used);
        Assert.Equal(10737418240, quota);
        Assert.Equal("pro", tier);
    }

    [Theory]
    [InlineData("""{"apiKey":null}""")]
    [InlineData("")]
    [InlineData("not json")]
    public void ParseProbe_NoKey_ReturnsNulls(string probe)
    {
        (string? key, _, _, _) = UfileIoPipeline.ParseProbe(probe);
        Assert.Null(key);
    }

    [Fact]
    public async Task RunAsync_LargeFile_SplitsIntoOneBasedChunks()
    {
        // chunkSize 4, file 10 bytes → 3 chunks with indices 1,2,3 and total_chunks 3.
        FakeServer server = new();
        UfileIoPipeline pipeline = server.Build(chunkSize: 4);

        await Drain(pipeline.RunAsync(MakeContext(fileSize: 10), CancellationToken.None));

        Assert.Equal(3, server.Chunks.Count);
        Assert.Equal(["1", "2", "3"], server.Chunks.Select(c => c.Fields["chunk_index"]).ToArray());
        Assert.Equal("3", server.LastFinaliseForm!["total_chunks"]);
    }

    [Fact]
    public async Task RunAsync_ReusesCookiesAcrossUploads()
    {
        FakeServer server = new();
        UfileIoPipeline pipeline = server.Build();

        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));
        await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // ONE GET / (csrf + session cached), but each upload made its own session + chunks.
        Assert.Equal(1, server.HomeGets);
        Assert.Equal(2, server.CreateSessions);
        Assert.Equal(2, server.Chunks.Count);
    }

    [Fact]
    public async Task RunAsync_SelectStorageFails_FallsBackToDefaultNode()
    {
        FakeServer server = new() { SelectStorageBody = """{"storageBaseUrl":"","error":"nope"}""" };
        UfileIoPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        // create_session/finalise target the fallback node when select_storage yields no URL.
        Assert.StartsWith("https://up.ufile.io/", server.LastFinaliseUrl!, StringComparison.Ordinal);
    }

    private const long Gb = 1024L * 1024 * 1024;

    private static FileHosterLoginDto Account(AccountType type) =>
        new() { FileHosterName = "Ufile", IsAnonymous = false, ApiKey = "KEY", AccountType = type };

    [Fact]
    public void MaxFileSizeFor_IsPerTier()
    {
        UfileIoPipeline pipeline = new();
        Assert.Equal(5 * Gb, pipeline.MaxFileSizeFor(new FileHosterLoginDto { FileHosterName = "Ufile", IsAnonymous = true })); // anon (Free default)
        Assert.Equal(5 * Gb, pipeline.MaxFileSizeFor(Account(AccountType.Free)));
        Assert.Equal(10 * Gb, pipeline.MaxFileSizeFor(Account(AccountType.Pro)));
        Assert.Equal(100 * Gb, pipeline.MaxFileSizeFor(Account(AccountType.Business)));
    }

    [Fact]
    public void MaxConcurrentUploadsFor_IsPerTier()
    {
        UfileIoPipeline pipeline = new();
        Assert.Equal(10, pipeline.MaxConcurrentUploadsFor(new FileHosterLoginDto { FileHosterName = "Ufile", IsAnonymous = true })); // anon
        Assert.Equal(10, pipeline.MaxConcurrentUploadsFor(Account(AccountType.Free)));
        Assert.Equal(30, pipeline.MaxConcurrentUploadsFor(Account(AccountType.Pro)));
        Assert.Equal(99, pipeline.MaxConcurrentUploadsFor(Account(AccountType.Business)));
    }

    [Theory]
    [InlineData("free", AccountType.Free)]
    [InlineData("Pro", AccountType.Pro)]
    [InlineData("BUSINESS", AccountType.Business)]
    [InlineData("premium", AccountType.Free)] // unknown label → Free
    [InlineData(null, AccountType.Free)]
    public void TierFromName_MapsDashboardLabel(string? label, AccountType expected)
        => Assert.Equal(expected, UfileIoPipeline.TierFromName(label));

    [Fact]
    public async Task RunAsync_FreeFileOverFiveGb_FailsFastWithoutAnyRequest()
    {
        FakeServer server = new();
        UfileIoPipeline pipeline = server.Build();

        // Free account, 6 GB file → over the 5 GB free cap.
        AttemptContext ctx = MakeContext(fileSize: (5 * Gb) + 1, apiKey: "KEY", accountType: AccountType.Free);
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("per-file limit", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.HomeGets);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_ProFileBetweenFiveAndTenGb_IsAccepted()
    {
        // 6 GB on a Pro account: over the 5 GB free cap but under the 10 GB pro cap.
        FakeServer server = new();
        UfileIoPipeline pipeline = server.Build();

        AttemptContext ctx = MakeContext(fileSize: 6 * Gb, apiKey: "KEY", accountType: AccountType.Pro);
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_CreateSessionReturnsNoFuid_FailsBeforeUpload()
    {
        FakeServer server = new() { CreateSessionBody = """{"error":"denied"}""" };
        UfileIoPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(server.Chunks);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_ChunkRejected_YieldsAttemptFailedWithoutFinalise()
    {
        FakeServer server = new();
        server.ChunkHandler = (_, _, _, _) => new HttpResponseSnapshot(400, "\"Chunk error\"", []);
        UfileIoPipeline pipeline = server.Build();

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Equal(0, server.Finalises);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_ChunkTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send abort must PROPAGATE (retryable) — the next attempt makes a fresh session, so no
        // double-create.
        FakeServer server = new();
        server.ChunkHandler = (_, _, _, _) =>
            throw new HttpRequestException("reset", new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054))));
        UfileIoPipeline pipeline = server.Build();

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    private sealed record ChunkCall(string Url, IReadOnlyDictionary<string, string> Fields, string FileName, long Length);

    /// <summary>A URL-routing fake for ufile's GET, urlencoded POSTs, and the chunk POST. Defaults model
    /// the verified happy path; toggles flip individual legs.</summary>
    private sealed class FakeServer
    {
        public string SelectStorageBody { get; set; } = """{"storageBaseUrl":"https://store-eu-hz-3.ufile.io/","error":""}""";

        public string CreateSessionBody { get; set; } = """{"fuid":"FUID123"}""";

        public string FinaliseBody { get; set; } = """{"id":13238156,"url":"https://ufile.io/abc123","slug":"abc123","name":"clip.avi"}""";

        public int HomeGets { get; private set; }

        public int CreateSessions { get; private set; }

        public int Finalises { get; private set; }

        public List<ChunkCall> Chunks { get; } = [];

        public IReadOnlyDictionary<string, string>? LastSelectForm { get; private set; }

        public IReadOnlyDictionary<string, string>? LastCreateForm { get; private set; }

        public IReadOnlyDictionary<string, string>? LastFinaliseForm { get; private set; }

        public IReadOnlyDictionary<string, string>? LastSelectHeaders { get; private set; }

        public IReadOnlyDictionary<string, string>? LastFinaliseHeaders { get; private set; }

        public string? LastFinaliseUrl { get; private set; }

        /// <summary>Overridable so a test can make the chunk throw or return a specific verdict.</summary>
        public Func<string, IReadOnlyDictionary<string, string>, string, long, HttpResponseSnapshot>? ChunkHandler { get; set; }

        public UfileIoPipeline Build(long chunkSize = 99_000_000) => new(Get, PostForm, Chunk, chunkSize);

        private HttpResponseSnapshot Get(string url, IReadOnlyDictionary<string, string>? headers)
        {
            HomeGets++;
            return new HttpResponseSnapshot(200, "<html>ufile</html>",
                ["csrf_cookie_name=CSRF_TOK; path=/; domain=ufile.io", "_ci_sessions_=SESS_ID; path=/; domain=ufile.io; HttpOnly"]);
        }

        private HttpResponseSnapshot PostForm(string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string>? headers)
        {
            if (url.EndsWith("select_storage", StringComparison.Ordinal))
            {
                LastSelectForm = form;
                LastSelectHeaders = headers;
                return new HttpResponseSnapshot(200, SelectStorageBody, []);
            }

            if (url.EndsWith("create_session", StringComparison.Ordinal))
            {
                CreateSessions++;
                LastCreateForm = form;
                return new HttpResponseSnapshot(200, CreateSessionBody, []);
            }

            // finalise
            Finalises++;
            LastFinaliseForm = form;
            LastFinaliseHeaders = headers;
            LastFinaliseUrl = url;
            return new HttpResponseSnapshot(200, FinaliseBody, []);
        }

        private HttpResponseSnapshot Chunk(string url, IReadOnlyDictionary<string, string> fields, string fileName, long length)
        {
            if (ChunkHandler is not null)
            {
                return ChunkHandler(url, fields, fileName, length);
            }

            Chunks.Add(new ChunkCall(url, new Dictionary<string, string>(fields), fileName, length));
            return new HttpResponseSnapshot(200, "\"Uploaded successfully.\"", []);
        }
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

    private static AttemptContext MakeContext(long fileSize = 5_225_142, string? apiKey = null, AccountType accountType = AccountType.Free) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = fileSize,
        HosterName = "Ufile",
        Credentials = new FileHosterLoginDto { FileHosterName = "Ufile", IsAnonymous = apiKey is null, ApiKey = apiKey, AccountType = accountType },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
