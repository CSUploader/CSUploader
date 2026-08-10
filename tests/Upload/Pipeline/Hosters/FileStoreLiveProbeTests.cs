// <copyright file="FileStoreLiveProbeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

// THROWAWAY — drives the SHIPPED pipeline with exactly the pair a sign-in would have captured (the
// session cookie and the node URL), since the WebView itself can't run from here. Delete after use.
public class FileStoreLiveProbeTests
{
    [Fact]
    public async Task LiveUpload()
    {
        string session = (await File.ReadAllTextAsync(
            @"C:\Users\Paul\AppData\Local\Temp\claude\E--Projects-CSUploader-CSUploader\8f9f2dbf-9309-470e-a87a-f1fccd88dabc\scratchpad\fs_session.txt")).Trim();

        const string Node = "https://srv9.filestore.me/cgi-bin/upload.cgi?upload_type=file&utype=reg";
        const string Name = "csu-fs-pipeline.rar";

        string path = Path.Combine(Path.GetTempPath(), Name);
        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(2 * 1024 * 1024));

        using HttpClient client = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        HttpHandler handler = new(client, Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        FileStorePipeline pipeline = new();

        List<string> log = [];

        AccountCheckResult refreshed = await pipeline.RefreshAccountAsync(Node, session, handler, ProxyChoice.Direct, CancellationToken.None);
        log.Add($"Refresh (session+node): valid={refreshed.IsValid} — {refreshed.Message}");

        AccountCheckResult noNode = await pipeline.RefreshAccountAsync(null, session, handler, ProxyChoice.Direct, CancellationToken.None);
        log.Add($"Refresh (no node): valid={noNode.IsValid}");

        AttemptContext ctx = new()
        {
            AttemptId = Guid.NewGuid(),
            FilePath = path,
            FileName = Name,
            FileSize = new FileInfo(path).Length,
            HosterName = "FileStore",
            Credentials = new FileHosterLoginDto
            {
                Id = 1,
                FileHosterName = "FileStore",
                IsAnonymous = false,
                Username = "csuprobe",
                SessionCookie = session,
                ApiKey = Node,
                SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(7),
            },
            Proxy = ProxyChoice.Direct,
            Handler = handler,
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };

        long peak = 0;
        string? link = null;
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            if (ev is TransferProgress p) peak = Math.Max(peak, p.BytesUploaded);
            if (ev is AttemptFailed f) log.Add("FAILED: " + f.Reason);
            if (ev is TransferCompleted c) { link = c.FileUrl; log.Add("LINK: " + c.FileUrl); }
        }

        log.Add($"progress peaked at {peak} of {ctx.FileSize}");

        // A dead session must produce the host's own words, not a generic failure.
        AttemptContext stale = ctx with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 2, FileHosterName = "FileStore", IsAnonymous = false,
                SessionCookie = "0000000000000000", ApiKey = Node,
            },
        };
        await foreach (UploadEvent ev in pipeline.RunAsync(stale, CancellationToken.None))
        {
            if (ev is AttemptFailed f) log.Add("dead session -> " + f.Reason);
            if (ev is TransferCompleted c) log.Add("dead session -> ⚠ SUCCEEDED: " + c.FileUrl);
        }

        AttemptContext anon = ctx with { Credentials = new FileHosterLoginDto { FileHosterName = "FileStore", IsAnonymous = true } };
        await foreach (UploadEvent ev in pipeline.RunAsync(anon, CancellationToken.None))
        {
            if (ev is AttemptFailed f) log.Add("anonymous -> " + f.Reason);
        }

        AttemptContext big = ctx with { FileSize = (250L * 1024 * 1024) + 1 };
        await foreach (UploadEvent ev in pipeline.RunAsync(big, CancellationToken.None))
        {
            if (ev is AttemptFailed f) log.Add("over cap -> " + f.Reason);
        }

        File.Delete(path);
        await File.AppendAllLinesAsync(@"D:\temp2\filestore-live.txt", log);
        Assert.NotNull(link);
    }
}
