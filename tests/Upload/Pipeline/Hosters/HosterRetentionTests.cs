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

    /// <summary>Builds a pipeline through its shortest constructor, honouring optional parameters —
    /// <see cref="Activator.CreateInstance(Type)"/> ignores those and throws.</summary>
    private static IFileHosterPipeline Create(Type pipelineType)
    {
        System.Reflection.ConstructorInfo ctor =
            pipelineType.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        object?[] args = [.. ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null)];
        return (IFileHosterPipeline)ctor.Invoke(args);
    }

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
        IFileHosterPipeline pipeline = Create(pipelineType);

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
        IFileHosterPipeline pipeline = Create(pipelineType);

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

    // ── The 2026-08-12 sweep of the hosts' own published pages ──

    [Theory]
    [InlineData(typeof(ClicknuploadPipeline), 10, 35, 60)]
    [InlineData(typeof(UploadyPipeline), 3, 30, 60)]
    [InlineData(typeof(UploadrarPipeline), 1, 90, 120)]
    [InlineData(typeof(SubysharePipeline), 1, 30, 180)]
    [InlineData(typeof(DailyUploadsPipeline), 1, 15, 40)]
    public void XfsForks_TierAnonymousFreePremium_AfterLastDownload(Type pipelineType, int anon, int free, int premium)
    {
        IFileHosterPipeline pipeline = Create(pipelineType);

        Assert.Equal(FileRetention.DaysAfterLastDownload(anon), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(free), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.DaysAfterLastDownload(premium), pipeline.RetentionFor(PremiumAccount));
    }

    [Theory]
    [InlineData(typeof(UploadHivePipeline), 50, 140)]
    [InlineData(typeof(FileaxaPipeline), 5, 30)]
    [InlineData(typeof(FilehosterIoPipeline), 5, 60)]
    public void XfsForks_WhosePremiumNeverDeletes(Type pipelineType, int anon, int free)
    {
        IFileHosterPipeline pipeline = Create(pipelineType);

        Assert.Equal(FileRetention.DaysAfterLastDownload(anon), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(free), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Uploadrar_ProTierOutlastsPremium()
    {
        IFileHosterPipeline pipeline = new UploadrarPipeline();

        FileHosterLoginDto pro = new() { Username = "someone", AccountType = AccountType.Pro };
        Assert.Equal(FileRetention.DaysAfterLastDownload(360), pipeline.RetentionFor(pro));
    }

    [Fact]
    public void Filedot_RegisteredCountsFromLastDownload_PremiumNever()
    {
        IFileHosterPipeline pipeline = new FiledotPipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(1000), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(Anonymous));
    }

    [Fact]
    public void Hexload_FreeAndGuestShareTheInactivityWindow()
    {
        IFileHosterPipeline pipeline = new HexloadPipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void UsersDrive_OnlyAccountTiersAreStated()
    {
        IFileHosterPipeline pipeline = new UsersDrivePipeline();

        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(9), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.DaysAfterLastDownload(19), pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void WorldFiles_OnlyTheRegisteredTierIsStated()
    {
        IFileHosterPipeline pipeline = new WorldFilesPipeline();

        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(45), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(PremiumAccount)); // "individual condition"
    }

    [Fact]
    public void DataVaults_StatesDaysWithoutABasis_SoTheFloorIsShown()
    {
        // Its page says "3 Days"/"7 Days"/"Never" without saying counted from what — the from-upload
        // floor, not the last-download reset its siblings promise.
        IFileHosterPipeline pipeline = new DataVaultsPipeline();

        Assert.Equal(FileRetention.DaysAfterUpload(3), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterUpload(7), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Hxfile_OnlyPremiumIsStated()
    {
        IFileHosterPipeline pipeline = new HxfilePipeline();

        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.DaysAfterUpload(365), pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void BowFile_TwentyOrHundredDays_FloorBasis()
    {
        IFileHosterPipeline pipeline = new BowFilePipeline();

        Assert.Equal(FileRetention.DaysAfterUpload(20), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterUpload(20), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.DaysAfterUpload(100), pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void MegaUp_ThirtyDaysInactivity_EveryTier()
    {
        IFileHosterPipeline pipeline = new MegaUpPipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Filestank_PremiumFigureIsSelfContradictory_SoOnlyFreeIsShown()
    {
        // Its FAQ prints premium 22 days against free 30 — shorter for the paid tier. That is not a
        // fact worth repeating; the free figure is.
        IFileHosterPipeline pipeline = new FilestankPipeline();

        Assert.Equal(FileRetention.DaysAfterUpload(30), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void BRupload_PortuguesePlansTable()
    {
        IFileHosterPipeline pipeline = new BRuploadPipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(3), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount)); // "NUNCA"
    }

    [Fact]
    public void Pixeldrain_SixtyDaysUnaccessed_EveryTier()
    {
        IFileHosterPipeline pipeline = new PixeldrainPipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(60), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.DaysAfterLastDownload(60), pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Upstore_OnlyPremiumPermanenceIsStated()
    {
        IFileHosterPipeline pipeline = new UpstorePipeline();

        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Wormhole_TwentyFourHours()
    {
        IFileHosterPipeline pipeline = new WormholePipeline();

        Assert.Equal(FileRetention.AfterUpload(TimeSpan.FromHours(24)), pipeline.RetentionFor(Anonymous));
    }

    [Fact]
    public void Ufile_GuestThirtyDays_AccountsPermanent()
    {
        IFileHosterPipeline pipeline = new UfileIoPipeline();

        Assert.Equal(FileRetention.DaysAfterUpload(30), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Permanent, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Sendspace_ThirtyDayInactivity_PremiumIsMembershipBoundSoUnknown()
    {
        IFileHosterPipeline pipeline = new SendspacePipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(Anonymous));
        Assert.Equal(FileRetention.DaysAfterLastDownload(30), pipeline.RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, pipeline.RetentionFor(PremiumAccount));
    }

    [Fact]
    public void Gofile_TenDaysWithoutDownloads()
    {
        IFileHosterPipeline pipeline = new GofilePipeline();

        Assert.Equal(FileRetention.DaysAfterLastDownload(10), pipeline.RetentionFor(Anonymous));
    }

    [Fact]
    public void HostsWhosePagesStatedNothing_StayUnspecified()
    {
        // Each of these was looked for on 2026-08-12 and the host's reachable pages said nothing
        // (or, for Turbobit/HitFile, the FAQ only renders in a browser). A future sweep can move
        // them up; nothing here may guess them up.
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new XubsterPipeline()).RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new DataNodesPipeline()).RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new TurbobitPipeline()).RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new HitFilePipeline()).RetentionFor(FreeAccount));
        Assert.Equal(FileRetention.Unspecified, ((IFileHosterPipeline)new KsharedPipeline()).RetentionFor(FreeAccount));
    }
}
