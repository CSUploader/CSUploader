// <copyright file="ExtMatrixPipelineSizeGateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Runtime tests for ExtMatrixPipeline's per-file size pre-check. The wizard's Summary
/// page filters oversize files out of ExtMatrix's row, but the upload runtime is the
/// final gate — if a file slips through (e.g. the wizard runs and the user adds more
/// hosters later, or a file's on-disk size changes between selection and upload), the
/// pipeline MUST refuse to dispatch bytes. These tests prove that gate fires before
/// any upload override is invoked.
/// </summary>
public class ExtMatrixPipelineSizeGateTests
{
    [Fact]
    public async Task RunAsync_FileOverMaxFileSize_EmitsAttemptFailedAndDoesNotCallUpload()
    {
        int uploadCalls = 0;
        ExtMatrixPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(string.Empty),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return Task.FromResult(new HttpResponseSnapshot(200, "upload_success", []));
            });

        AttemptContext ctx = MakeContext(fileSize: 300L * 1024 * 1024); // 300 MiB > 250 MiB

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        // Pipeline must report failure, and the upload override must NEVER be called —
        // a single byte being dispatched here would be a real "file uploaded despite
        // exceeding limit" bug.
        Assert.Equal(0, uploadCalls);
        AttemptFailed failed = Assert.IsType<AttemptFailed>(Assert.Single(events));
        Assert.Contains("per-file limit", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("250 MiB", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FileAtExactlyMaxFileSize_PassesTheSizeGate()
    {
        // Boundary: 250 MiB exactly is NOT over the cap (the check is `>`, not `>=`),
        // so the size gate should let it through. We don't drive the rest of the upload
        // here — just assert that AttemptFailed-with-size-message isn't the first event.
        int uploadCalls = 0;
        ExtMatrixPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(string.Empty),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploadCalls++;
                return Task.FromResult(new HttpResponseSnapshot(200, "upload_success\nhttps://www.extmatrix.com/abc", []));
            });

        AttemptContext ctx = MakeContext(fileSize: 250L * 1024 * 1024);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        // The first event must NOT be a size-rejection. (Subsequent events may fail for
        // other reasons — no API key, no auth service — but those aren't the size gate.)
        if (events.Count > 0 && events[0] is AttemptFailed first)
        {
            Assert.DoesNotContain("per-file limit", first.Reason, StringComparison.Ordinal);
        }
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

    private static AttemptContext MakeContext(long fileSize) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\big.bin",
        FileName = "big.bin",
        FileSize = fileSize,
        HosterName = "ExtMatrix",
        Credentials = new FileHosterLoginDto { Id = 42, FileHosterName = "ExtMatrix", ApiKey = "test-key" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
