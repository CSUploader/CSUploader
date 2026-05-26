// <copyright file="XFileSharingApiPipelineSubclassTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Sanity test for the abstract <see cref="XFileSharingApiPipeline"/> base class. Locks
/// in the contract that adding a new XFileSharing-API hoster only requires supplying a
/// Name + Host — everything else (regexes, multipart shape, my_account scrape, API
/// endpoints, cookie defaults) is shared verbatim. If this test ever breaks, the base
/// has grown a new abstract / required override that needs documenting before any
/// subclass beyond ExLoadPipeline can be added.
/// </summary>
public class XFileSharingApiPipelineSubclassTests
{
    private const string MyAccountWithApiKeyHtml = """
        <!doctype html><html><body>
        <form method="POST">
          <input type="hidden" name="op" value="my_account">
          <input type="hidden" name="token" value="csrf">
          <input type="text" readonly name="api-url" value="https://test-xfs.example/api/account/info?key=demo_key">
        </form>
        </body></html>
        """;

    private const string UploadServerOkJson = """{"msg":"OK","status":200,"sess_id":"sess_demo","result":"http://fs1.test-xfs.example/cgi-bin/upload.cgi"}""";

    private const string UploadOkJson = """[{"file_code":"demoCode","file_status":"OK"}]""";

    /// <summary>
    /// Stand-in subclass for a hypothetical "TestXfsHost" hoster. Supplies only Name +
    /// Host; all behaviour comes from the base. Exercising the full RunAsync flow proves
    /// the base really is hoster-agnostic.
    /// </summary>
    private sealed class TestXfsHostPipeline : XFileSharingApiPipeline
    {
        public TestXfsHostPipeline(
            IInteractiveAuthService? authService,
            FileHosterLoginRepository? loginRepository,
            Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
            Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
            : base(authService, loginRepository, getOverride, uploadOverride)
        {
        }

        public override string Name => "TestXfsHost";

        protected override string Host => "https://test-xfs.example";
    }

    [Fact]
    public async Task SubclassWithJustNameAndHost_CompletesFullApiKeyDirectUploadFlow()
    {
        Queue<string> getResponses = new(new[] { UploadServerOkJson });
        Queue<HttpResponseSnapshot> uploads = new(new[]
        {
            new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()),
        });
        List<UploadCall> uploadCalls = [];

        TestXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(getResponses.Dequeue()),
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                uploadCalls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(uploads.Dequeue());
            });

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "TestXfsHost", ApiKey = "demo_key" };
        AttemptContext ctx = MakeContext(credentials);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        // Public URL is prefixed with the subclass's Host, not ex-load — proves Host is
        // properly propagated through the shared PublicUrlPrefix derivation.
        Assert.Equal("https://test-xfs.example/demoCode", tc.FileUrl);

        UploadCall call = Assert.Single(uploadCalls);
        Assert.Equal("http://fs1.test-xfs.example/cgi-bin/upload.cgi", call.Endpoint);
        // Origin header should also come from the subclass's Host.
        Assert.Equal("https://test-xfs.example", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task SubclassMaxFileSizeMessage_UsesSubclassNameNotExLoad()
    {
        TestXfsHostPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(string.Empty),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>())));

        FileHosterLoginDto credentials = new() { Id = 1, FileHosterName = "TestXfsHost", ApiKey = "k" };
        AttemptContext ctx = MakeContext(credentials) with { FileSize = 2L * 1024 * 1024 * 1024 };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("TestXfsHost", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ExLoad", fail.Reason, StringComparison.Ordinal);
    }

    private static AttemptContext MakeContext(FileHosterLoginDto credentials) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "TestXfsHost",
        Credentials = credentials,
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);
}
