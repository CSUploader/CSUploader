// <copyright file="HosterDownloadCaptchaTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Pins every hoster's declared download-captcha verdict to the research matrix in
/// <c>docs/hoster-download-captcha.md</c> — each Required/NotRequired below traces to the host's own
/// copy or an inspected live download flow, so a drive-by edit can't quietly change what the
/// wizard's "Download captcha?" column claims. Everything is asserted THROUGH
/// <see cref="IFileHosterPipeline"/>: for subclasses of a base that binds the interface slot
/// (XFS, YetiShare, XfsPro, MoneyPlatform), a same-named property that fails to override would
/// never be reached through the interface, and calling it directly would hide exactly that bug.
/// </summary>
public class HosterDownloadCaptchaTests
{
    [Fact]
    public void InterfaceDefault_IsUnknown_NotAClaim()
    {
        // The resting state for a pipeline that declares nothing must be "not verified" — most
        // hosts start unresearched, and Unknown is the only honest default.
        IFileHosterPipeline pipeline = new PipelineWithNoOverrides();

        Assert.Equal(DownloadCaptchaRequirement.Unknown, pipeline.DownloadCaptcha);
    }

    /// <summary>A minimal pipeline that overrides nothing optional, so every interface default is
    /// observable exactly as a real no-override hoster would surface it.</summary>
    private sealed class PipelineWithNoOverrides : IFileHosterPipeline
    {
        public string Name => "TestHost";

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, CSUploader.Lib.Net.ProxyChoice proxy, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
