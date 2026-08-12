// <copyright file="HosterRetentionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Pins every hoster's documented retention to the figure its pipeline cites — each value here traces
/// to the host's own copy, plan table, or a measured expiry stamp, so a drive-by edit can't quietly
/// change what the "Kept for" column claims. Everything is asserted THROUGH
/// <see cref="IFileHosterPipeline"/>: for subclasses of a base that binds the interface slot
/// (TeraBytez under XFS, udrop under YetiShare), a same-named method that fails to override would
/// never be reached through the interface, and calling it directly would hide exactly that bug.
/// </summary>
public class HosterRetentionTests
{
    private static readonly FileHosterLoginDto Anonymous = new() { IsAnonymous = true };
    private static readonly FileHosterLoginDto FreeAccount = new() { Username = "someone", AccountType = AccountType.Free };
    private static readonly FileHosterLoginDto PremiumAccount = new() { Username = "someone", AccountType = AccountType.Premium };

    [Fact]
    public void DefaultIsUnspecified_NotPermanent()
    {
        // The interface default, through a pipeline that never overrides it: "the host publishes
        // nothing" must be the resting state, because most hosts publish nothing.
        IFileHosterPipeline pipeline = new RapidgatorPipeline();

        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(FreeAccount));
    }

    [Fact]
    public void PermanentHosts_SaySo()
    {
        Assert.Equal(FileRetention.Permanent, ((IFileHosterPipeline)new CatboxPipeline()).RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Permanent, ((IFileHosterPipeline)new QuAxPipeline()).RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Permanent, ((IFileHosterPipeline)new DropMbPipeline()).RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Permanent, ((IFileHosterPipeline)new UdropPipeline()).RetentionFor(Anonymous));
    }

    [Theory]
    [InlineData(typeof(HostizePipeline), 24)]
    [InlineData(typeof(TmpFilesPipeline), 48)]
    [InlineData(typeof(LitterboxPipeline), 72)]
    public void HourScaleHosts_ExpireAfterUpload(Type pipelineType, int hours)
    {
        IFileHosterPipeline pipeline = (IFileHosterPipeline)Activator.CreateInstance(pipelineType)!;

        Assert.Equal(FileRetention.AfterUpload(TimeSpan.FromHours(hours)), pipeline.RetentionFor(Anonymous));
    }

    [Theory]
    [InlineData(typeof(TempShPipeline), 3)]
    [InlineData(typeof(FilebinPipeline), 7)]
    [InlineData(typeof(DropMeFilesPipeline), 14)]
    [InlineData(typeof(FilegoPipeline), 30)]
    [InlineData(typeof(GigaFilePipeline), 100)]
    public void DayScaleHosts_ExpireAfterUpload(Type pipelineType, int days)
    {
        IFileHosterPipeline pipeline = (IFileHosterPipeline)Activator.CreateInstance(pipelineType)!;

        Assert.Equal(FileRetention.DaysAfterUpload(days), pipeline.RetentionFor(Anonymous));
    }

    [Fact]
    public void VikingFile_CountsFromTheLastDownload()
    {
        IFileHosterPipeline pipeline = new VikingFilePipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(15), pipeline.RetentionFor(Anonymous));
    }

    [Fact]
    public void UploadEe_TiersByAnonymity()
    {
        IFileHosterPipeline pipeline = new UploadEePipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(50), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(120), pipeline.RetentionFor(FreeAccount));
    }

    [Fact]
    public void TeraBytez_TiersByAccountType_ThroughTheXfsBase()
    {
        IFileHosterPipeline pipeline = new TeraBytezPipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.DaysAfterLastDownload(365), pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void FileMirage_OnlyTheFreeTierIsDocumented()
    {
        IFileHosterPipeline pipeline = new FileMiragePipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(20), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void StorageTo_OnlyTheAnonymousRouteIsDocumented()
    {
        IFileHosterPipeline pipeline = new StorageToPipeline();

        Assert.Equal(FileRetention.DaysAfterUpload(3), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(FreeAccount));
    }

    [Fact]
    public void DepositFiles_FreeTierMeasured_PremiumUnknown()
    {
        IFileHosterPipeline pipeline = new DepositFilesPipeline();

        Assert.Equal(FileRetention.DaysAfterUpload(121), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void YetiShareBase_StaysUnspecified_UdropIsTheException()
    {
        // udrop's permanence is udrop's policy, not the platform's — its siblings must not inherit it.
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new BowFilePipeline()).RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new MegaUpPipeline()).RetentionFor(Anonymous));
    }
}
