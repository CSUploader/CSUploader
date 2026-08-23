// <copyright file="PartParallelismTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

/// <summary>
/// How many of a file's parts may fly at once, and — more importantly — which hosters are allowed to
/// try. The safety property of the whole feature is that adding the capability changes nothing for
/// anyone who has not deliberately opted in.
/// </summary>
public class PartParallelismTests
{
    /// <summary>
    /// The four REGISTERED hosters whose protocols make parts genuinely order-independent:
    /// presigned per-part URLs (VikingFile, Hostize), on-demand per-part signing (UploadNow), or an
    /// explicit byte offset per chunk (DataNodes' <c>X-Seek-To</c>).
    /// </summary>
    private static readonly string[] OptedIn =
        ["VikingFile", "Hostize", "UploadNow", "DataNodes"];

    /// <summary>
    /// Storage.to is opted in at the code level and uses the same presigned-part shape, but it is
    /// DISABLED (<c>ServiceRegistration.cs:350</c>, behind a Cloudflare managed challenge since
    /// 2026-08-16) so it never reaches the registry. Named here rather than dropped, so that
    /// re-enabling it does not look like an accidental new parallel hoster.
    /// </summary>
    private const string OptedInButDisabled = "Storage.to";

    private static IFileHosterPipeline[] AllPipelines()
    {
        ServiceCollection services = new();
        services.AddCoreServices(AppContext.BaseDirectory);
        services.AddSingleton(Mock.Of<IInteractiveAuthService>());
        services.AddSingleton(Mock.Of<IToastNotificationService>());
        using ServiceProvider provider = services.BuildServiceProvider();
        return [.. provider.GetServices<IFileHosterPipeline>()];
    }

    /// <summary>
    /// The safety property of the feature: adding the capability must not change a single hoster's
    /// behaviour until someone deliberately opts it in. Asserted over the WHOLE registry, so a new
    /// pipeline cannot quietly inherit parallelism it has not earned.
    /// </summary>
    [Fact]
    public void MaxParallelPartsFor_DefaultsToOne_UnlessTheHosterOptedIn()
    {
        IFileHosterPipeline[] pipelines = AllPipelines();
        Assert.NotEmpty(pipelines);

        foreach (IFileHosterPipeline pipeline in pipelines)
        {
            int degree = pipeline.MaxParallelPartsFor(new FileHosterLoginDto { FileHosterName = pipeline.Name });

            if (OptedIn.Contains(pipeline.Name, StringComparer.Ordinal))
            {
                Assert.True(degree > 1, $"{pipeline.Name} should have opted in but returned {degree}");
            }
            else
            {
                Assert.True(
                    degree == 1,
                    $"{pipeline.Name} returned {degree}. Only a hoster whose parts are genuinely "
                    + "order-independent may exceed 1 — an append-only chunk endpoint would corrupt the upload.");
            }
        }
    }

    [Fact]
    public void EveryOptedInHosterIsRegistered()
    {
        // Guards the list above from rotting: a hoster renamed would otherwise make the check above
        // silently stop covering it.
        string[] registered = [.. AllPipelines().Select(p => p.Name)];

        Assert.All(OptedIn, name => Assert.Contains(name, registered, StringComparer.Ordinal));
    }

    [Fact]
    public void TheDisabledParallelHoster_IsStillNotRegistered()
    {
        // The other half of the guard. If Storage.to is ever re-enabled this fails, which is the
        // prompt to move it into OptedIn and give it a live verification pass — rather than having
        // it quietly start uploading in parallel with nothing asserting that it works.
        string[] registered = [.. AllPipelines().Select(p => p.Name)];

        Assert.DoesNotContain(OptedInButDisabled, registered, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(8, 2, 2)]   // the user's ceiling caps a keen hoster
    [InlineData(4, 8, 4)]   // the hoster's own declaration caps a generous ceiling
    [InlineData(8, 0, 1)]   // never below 1, whatever the setting says
    [InlineData(1, 8, 1)]   // an un-opted-in hoster stays sequential regardless
    public void Effective_IsTheLesserOfTheHostersDeclarationAndTheUsersCeiling(int declares, int ceiling, int expected)
    {
        Assert.Equal(expected, PartParallelism.Effective(declares, ceiling));
    }

    [Fact]
    public void TheUserCeilingDefault_IsLowerThanWhatTheHostersDeclare()
    {
        // Degree multiplies with MaxConcurrentUploadJobs: at its default of 5, a ceiling of 8 would
        // mean 40 in-flight part bodies. 4 is the deliberate conservative default.
        Assert.Equal(4, AppSettings.DefaultMaxParallelPartsPerFile);
        Assert.True(AppSettings.DefaultMaxParallelPartsPerFile < 8);
    }
}
