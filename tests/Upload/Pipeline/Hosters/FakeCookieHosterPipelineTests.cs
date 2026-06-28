// <copyright file="FakeCookieHosterPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class FakeCookieHosterPipelineTests
{
    [Fact]
    public async Task CookieAuth_PersistsAcrossAttempts_WithoutTokenOrHeader()
    {
        FakeCookieHosterPipeline pipeline = new();

        AttemptContext ctx1 = MakeContext();
        await Drain(pipeline.RunAsync(ctx1, CancellationToken.None));

        AttemptContext ctx2 = MakeContext();
        List<UploadEvent> evs = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx2, CancellationToken.None))
        {
            evs.Add(ev);
        }

        // Second attempt skips AuthStarted because the cookie jar is reused
        Assert.DoesNotContain(evs, e => e is AuthStarted);
        Assert.Contains(evs, e => e is TransferCompleted);
    }

    private static async Task Drain(IAsyncEnumerable<UploadEvent> stream)
    {
        await foreach (UploadEvent _ in stream)
        { }
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = "x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "FakeCookie",
        Credentials = new FileHosterLoginDto { Id = 17, FileHosterName = "FakeCookie", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    /// <summary>
    /// Reference cookie-based pipeline: <see cref="CookieContainer"/> is the auth state,
    /// keyed by Credentials.Id. No token, no bearer header — proves the contract is generic.
    /// </summary>
    private sealed class FakeCookieHosterPipeline : IFileHosterPipeline
    {
        private readonly Dictionary<int, CookieContainer> _jars = [];

        public string Name => "FakeCookie";
        public bool RequiresHashingBeforeUpload => false;
        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "Login OK"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!_jars.ContainsKey(ctx.Credentials.Id))
            {
                yield return new AuthStarted();
                await Task.Yield();
                CookieContainer jar = new();
                jar.Add(new Cookie("session", "abc", "/", "fake"));
                _jars[ctx.Credentials.Id] = jar;
                yield return new AuthSucceeded();
            }

            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();
            yield return new TransferCompleted("https://fake/file/" + ctx.FileName);
        }
    }
}
