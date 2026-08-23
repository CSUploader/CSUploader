// <copyright file="PartParallelismWiringTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

/// <summary>
/// The seams between the setting and the value a pipeline actually sees.
/// <para>
/// <c>PartParallelismTests</c> proves the arithmetic and the per-hoster declarations, but a
/// disconnected setting or a hard-coded context value would leave every host sequential while all of
/// those stayed green. These cover the two joins in between: the ceiling reaching
/// <c>AttemptInputs</c>, and <c>AttemptRunner</c> combining it with the pipeline's declaration.
/// </para>
/// </summary>
public class PartParallelismWiringTests
{
    [Fact]
    public void BuildAttemptInputs_CarriesTheUsersCeilingFromSettings()
    {
        AppSettings settings = new() { MaxParallelPartsPerFile = 6 };
        Package package = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);

        AttemptInputs inputs = package.First().BuildAttemptInputs(Mock.Of<IAppLogger>());

        Assert.Equal(6, inputs.MaxParallelPartsCeiling);
    }

    [Fact]
    public void BuildAttemptInputs_WithNoSettings_FallsBackToTheDefaultCeiling()
    {
        // PackageOptions.Settings is nullable for non-DI callers, so this path is reachable.
        AppSettings settings = new();
        Package package = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);

        AttemptInputs inputs = package.First().BuildAttemptInputs(Mock.Of<IAppLogger>());

        Assert.Equal(AppSettings.DefaultMaxParallelPartsPerFile, inputs.MaxParallelPartsCeiling);
    }

    [Theory]
    [InlineData(8, 4, 4)]  // the user's ceiling wins
    [InlineData(2, 8, 2)]  // the hoster's declaration wins
    [InlineData(1, 8, 1)]  // an un-opted-in hoster stays sequential whatever the user asks for
    public async Task AttemptRunner_PutsTheCombinedDegreeOnTheContext(int hosterDeclares, int ceiling, int expected)
    {
        CapturingPipeline pipeline = new(hosterDeclares);
        DefaultFileHosterRegistry registry = new([pipeline]);

        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(new ProxyChoice(1, null, "http://x:1"));
        Mock<IHttpHandlerFactory> handlerFactory = new();
        handlerFactory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));

        AttemptRunner runner = new(registry, proxy.Object, handlerFactory.Object);

        AttemptInputs inputs = new()
        {
            FilePath = "x.zip",
            FileName = "x.zip",
            FileSize = 100,
            HosterName = "Fake",
            Credentials = new FileHosterLoginDto(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedBudget = SpeedBudget.Unlimited,
            MaxParallelPartsCeiling = ceiling,
        };

        await foreach (UploadEvent _ in runner.RunAsync(inputs, CancellationToken.None))
        {
            // drain
        }

        Assert.NotNull(pipeline.SeenContext);
        Assert.Equal(expected, pipeline.SeenContext!.MaxParallelParts);
    }

    /// <summary>Records the context it was handed, so the test can assert the resolved degree the
    /// way a real pipeline would read it.</summary>
    private sealed class CapturingPipeline(int declares) : IFileHosterPipeline
    {
        public AttemptContext? SeenContext { get; private set; }

        public string Name => "Fake";

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public int MaxParallelPartsFor(FileHosterLoginDto credentials) => declares;

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "Login OK"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            SeenContext = ctx;
            await Task.Yield();
            yield return new TransferStarted(ctx.FileSize);
            yield return new TransferCompleted("https://done");
        }
    }
}
