// <copyright file="FileHosterSelectionViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

public class FileHosterSelectionViewModelTests
{
    [Fact]
    public void Ctor_NoAccounts_ReportsAnonymousState()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        Assert.False(vm.HasAccounts);
        Assert.Null(vm.SelectedAccount);
        Assert.False(vm.Use);
    }

    [Fact]
    public void Ctor_WithAccounts_PreselectsFirst()
    {
        FileHosterLoginDto[] accounts = [Login(1, "alice"), Login(2, "bob")];

        FileHosterSelectionViewModel vm = new("Rapidgator", accounts);

        Assert.True(vm.HasAccounts);
        Assert.Same(accounts[0], vm.SelectedAccount);
    }

    [Fact]
    public void Use_SettingTrueWithoutAccounts_CoercesBackToFalse()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        vm.Use = true;

        Assert.False(vm.Use);
    }

    [Fact]
    public void SetAccounts_AddingFirstAccount_ExposesItAndKeepsUseDecision()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        vm.SetAccounts([Login(7, "alice")]);

        Assert.True(vm.HasAccounts);
        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal("alice", vm.SelectedAccount!.Username);
    }

    [Fact]
    public void SetAccounts_PreservesPreviousSelectionWhenStillPresent()
    {
        FileHosterLoginDto[] before = [Login(1, "alice"), Login(2, "bob")];
        FileHosterSelectionViewModel vm = new("Rapidgator", before);
        vm.SelectedAccount = before[1]; // bob

        FileHosterLoginDto[] after = [Login(1, "alice"), Login(2, "bob"), Login(3, "carol")];
        vm.SetAccounts(after);

        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal(2, vm.SelectedAccount!.Id);
    }

    [Fact]
    public void SetAccounts_WhenSelectionDisappears_FallsBackToFirst()
    {
        FileHosterLoginDto[] before = [Login(1, "alice"), Login(2, "bob")];
        FileHosterSelectionViewModel vm = new("Rapidgator", before);
        vm.SelectedAccount = before[1]; // bob

        vm.SetAccounts([Login(5, "dave")]);

        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal(5, vm.SelectedAccount!.Id);
    }

    [Fact]
    public void SetAccounts_RemovingAllAccounts_AlsoTurnsOffUse()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", [Login(1, "alice")]);
        vm.Use = true;

        vm.SetAccounts([]);

        Assert.False(vm.HasAccounts);
        Assert.False(vm.Use);
        Assert.Null(vm.SelectedAccount);
    }

    [Fact]
    public void Ctor_NoAccountsButSupportsAnonymous_IsUsableAndAllowsUse()
    {
        FileHosterSelectionViewModel vm = new("GigaPeta", [], supportsAnonymous: true);

        Assert.False(vm.HasAccounts);
        Assert.True(vm.SupportsAnonymous);
        Assert.True(vm.CanUse);

        vm.Use = true;
        Assert.True(vm.Use); // anonymous-capable rows can be checked with no account
    }

    [Fact]
    public void Ctor_NoAccountsNoAnonymous_IsNotUsable()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        Assert.False(vm.CanUse);

        vm.Use = true;
        Assert.False(vm.Use);
    }

    [Fact]
    public void SetAccounts_RemovingAllAccountsButAnonymousSupported_KeepsUse()
    {
        FileHosterSelectionViewModel vm = new("GigaPeta", [Login(1, "alice")], supportsAnonymous: true);
        vm.Use = true;

        vm.SetAccounts([]);

        Assert.False(vm.HasAccounts);
        Assert.True(vm.CanUse);                          // still usable anonymously
        Assert.True(vm.Use);                             // so Use is NOT coerced off
        Assert.True(vm.SelectedAccount?.IsAnonymous == true);    // falls back to the Anonymous option
    }

    [Fact]
    public void AccountOptions_AnonymousCapableWithAccounts_AppendsAnonymousAndDefaultsToRealAccount()
    {
        FileHosterLoginDto[] accounts = [Login(1, "alice")];

        FileHosterSelectionViewModel vm = new("Hexload", accounts, supportsAnonymous: true);

        Assert.Equal(2, vm.AccountOptions.Count);
        Assert.Same(accounts[0], vm.AccountOptions[0]);     // real account first
        Assert.True(vm.AccountOptions[1].IsAnonymous);      // Anonymous appended
        Assert.Same(accounts[0], vm.SelectedAccount);       // prefers the real account by default
    }

    [Fact]
    public void AccountOptions_AnonymousCapableNoAccounts_IsAnonymousOnly()
    {
        FileHosterSelectionViewModel vm = new("GigaPeta", [], supportsAnonymous: true);

        FileHosterLoginDto only = Assert.Single(vm.AccountOptions);
        Assert.True(only.IsAnonymous);
        Assert.Same(only, vm.SelectedAccount);
    }

    [Fact]
    public void AccountOptions_NotAnonymousCapable_HasNoAnonymousEntry()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", [Login(1, "alice")]);

        FileHosterLoginDto only = Assert.Single(vm.AccountOptions);
        Assert.False(only.IsAnonymous);
    }

    [Fact]
    public void MaxFileSize_WithoutResolver_IsEmpty()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        Assert.Null(vm.MaxFileSizeBytes);
        Assert.Equal(string.Empty, vm.MaxFileSizeDisplay);
    }

    [Fact]
    public void MaxFileSize_WithCap_FormatsLikeTheOversizeWarning()
    {
        // The invariant is the SHARED formatter: whatever base the roundness pick lands on, the
        // column and the step-2 oversize warning must never show two different numbers.
        const long Cap = 5_500_000_000;
        FileHosterSelectionViewModel vm = new("Wormhole", [], supportsAnonymous: true, maxFileSizeResolver: _ => Cap);

        Assert.Equal(Cap, vm.MaxFileSizeBytes);
        Assert.Equal(ByteUnit.FromBytesPreferRoundUnit(Cap).ToFriendlyString(), vm.MaxFileSizeDisplay);
    }

    [Fact]
    public void MaxFileSize_DecimalRoundCap_ShowsTheHostsOwnFigure()
    {
        // DropMB's share.maxSize is exactly 512,000,000 bytes — the "512 MB" its site advertises.
        // Rendering that as "488.28 MiB" was faithful arithmetic and a wrong-looking cell (user
        // report, 2026-08-22).
        FileHosterSelectionViewModel vm = new(
            "DropMB", [], supportsAnonymous: true, maxFileSizeResolver: _ => 512_000_000);

        Assert.Equal("512 MB", vm.MaxFileSizeDisplay);
    }

    [Fact]
    public void MaxFileSize_BinaryRoundCap_StaysBinary()
    {
        // DropMeFiles' cap is a genuine 50 GiB — flipping everything to decimal would have traded
        // DropMB's complaint for "53.69 GB" here.
        FileHosterSelectionViewModel vm = new(
            "DropMeFiles", [], supportsAnonymous: true, maxFileSizeResolver: _ => 53_687_091_200);

        Assert.Equal("50 GiB", vm.MaxFileSizeDisplay);
    }

    [Fact]
    public void MaxFileSize_WithoutCap_ShowsLocalizedNoLimit()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", [Login(1, "alice")], maxFileSizeResolver: _ => null);

        Assert.Null(vm.MaxFileSizeBytes);
        Assert.Equal(Localizer.Instance["Wizard_Step2_NoLimit"], vm.MaxFileSizeDisplay);
    }

    [Fact]
    public void MaxFileSize_ReResolvesAndNotifies_WhenAccountChanges()
    {
        // Tier-dependent cap (the Hexload shape): the anonymous option allows more than the account.
        FileHosterLoginDto[] accounts = [Login(1, "alice")];
        FileHosterSelectionViewModel vm = new(
            "Hexload",
            accounts,
            supportsAnonymous: true,
            maxFileSizeResolver: acct => acct?.IsAnonymous == true ? 2L * 1024 * 1024 * 1024 : 1L * 1024 * 1024 * 1024);

        Assert.Same(accounts[0], vm.SelectedAccount); // real account preferred at construction
        Assert.Equal(1L * 1024 * 1024 * 1024, vm.MaxFileSizeBytes);

        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedAccount = vm.AccountOptions[^1]; // the synthetic anonymous entry

        Assert.Equal(2L * 1024 * 1024 * 1024, vm.MaxFileSizeBytes);
        Assert.Contains(nameof(FileHosterSelectionViewModel.MaxFileSizeBytes), changed);
        Assert.Contains(nameof(FileHosterSelectionViewModel.MaxFileSizeDisplay), changed);
    }

    // ── "Max parallel" column: the same shape as "Max file size", since both caps are per-tier ──

    [Fact]
    public void MaxConcurrent_WithoutResolver_IsEmpty()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        Assert.Null(vm.MaxConcurrentUploads);
        Assert.Equal(string.Empty, vm.MaxConcurrentUploadsDisplay);
    }

    [Fact]
    public void MaxConcurrent_WithoutCap_ShowsLocalizedNoLimit()
    {
        // The common case by a wide margin — most hosters declare no concurrency limit at all.
        FileHosterSelectionViewModel vm = new("Rapidgator", [Login(1, "alice")], maxConcurrentResolver: _ => null);

        Assert.Null(vm.MaxConcurrentUploads);
        Assert.Equal(Localizer.Instance["Wizard_Step2_NoLimit"], vm.MaxConcurrentUploadsDisplay);
    }

    [Fact]
    public void MaxConcurrent_WithCap_ShowsTheNumber()
    {
        FileHosterSelectionViewModel vm = new("DropMeFiles", [], supportsAnonymous: true, maxConcurrentResolver: _ => 5);

        Assert.Equal(5, vm.MaxConcurrentUploads);
        Assert.Equal("5", vm.MaxConcurrentUploadsDisplay);
    }

    [Fact]
    public void MaxConcurrent_ReResolvesAndNotifies_WhenAccountChanges()
    {
        // Tier-dependent, the ufile shape: free allows fewer parallel uploads than a paid account.
        FileHosterLoginDto[] accounts = [Login(1, "alice")];
        FileHosterSelectionViewModel vm = new(
            "Ufile",
            accounts,
            supportsAnonymous: true,
            maxConcurrentResolver: acct => acct?.IsAnonymous == true ? 10 : 30);

        Assert.Equal(30, vm.MaxConcurrentUploads);

        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedAccount = vm.AccountOptions[^1]; // the synthetic anonymous entry

        Assert.Equal(10, vm.MaxConcurrentUploads);
        Assert.Contains(nameof(FileHosterSelectionViewModel.MaxConcurrentUploads), changed);
        Assert.Contains(nameof(FileHosterSelectionViewModel.MaxConcurrentUploadsDisplay), changed);
    }

    // ── "Kept for" column: retention, the third per-tier resolver ──

    [Fact]
    public void Retention_WithoutResolver_IsEmptyAndTooltipless()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        Assert.Equal(FileRetention.Unspecified, vm.Retention);
        Assert.Null(vm.RetentionSortKey);
        Assert.Equal(string.Empty, vm.RetentionDisplay);
        Assert.Null(vm.RetentionTooltip);
    }

    [Fact]
    public void Retention_Unspecified_ShowsDashWithExplainingTooltip()
    {
        // The majority case: the host publishes nothing, and the cell must not pretend otherwise.
        FileHosterSelectionViewModel vm = new(
            "Rapidgator", [Login(1, "alice")], retentionResolver: _ => FileRetention.Unspecified);

        Assert.Equal(Localizer.Instance["Wizard_Step2_Retention_Unknown"], vm.RetentionDisplay);
        Assert.Equal(Localizer.Instance["Wizard_Step2_Retention_UnknownTooltip"], vm.RetentionTooltip);
        Assert.Null(vm.RetentionSortKey);
    }

    [Fact]
    public void Retention_Permanent_ShowsThatWordAndNoTooltip()
    {
        FileHosterSelectionViewModel vm = new(
            "Catbox", [], supportsAnonymous: true, retentionResolver: _ => FileRetention.Permanent);

        Assert.Equal(Localizer.Instance["Wizard_Step2_Retention_Permanent"], vm.RetentionDisplay);
        Assert.Null(vm.RetentionTooltip);
        Assert.Equal(double.PositiveInfinity, vm.RetentionSortKey);
    }

    [Fact]
    public void Retention_WholeDaysOfThreeOrMore_FormatAsDays()
    {
        FileHosterSelectionViewModel vm = new(
            "Temp.sh", [], supportsAnonymous: true, retentionResolver: _ => FileRetention.DaysAfterUpload(3));

        string expected = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Step2_Retention_Days_Format"],
            3);
        Assert.Equal(expected, vm.RetentionDisplay);
        Assert.Null(vm.RetentionTooltip); // an after-upload cell says it all itself
    }

    [Fact]
    public void Retention_UnderThreeDays_FormatsAsHours()
    {
        // Hostize's 24 hours must not read "1 days" — below three whole days the unit is hours,
        // which also keeps every string plural.
        FileHosterSelectionViewModel vm = new(
            "Hostize",
            [],
            supportsAnonymous: true,
            retentionResolver: _ => FileRetention.AfterUpload(TimeSpan.FromHours(24)));

        string expected = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Step2_Retention_Hours_Format"],
            24);
        Assert.Equal(expected, vm.RetentionDisplay);
    }

    [Fact]
    public void Retention_AfterLastDownload_GetsStarAndSpelledOutTooltip()
    {
        FileHosterSelectionViewModel vm = new(
            "VikingFile", [], supportsAnonymous: true, retentionResolver: _ => FileRetention.DaysAfterLastDownload(15));

        string days = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Step2_Retention_Days_Format"],
            15);
        Assert.Equal(days + " *", vm.RetentionDisplay);
        Assert.Equal(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Step2_Retention_LastDownloadTooltip_Format"],
                days),
            vm.RetentionTooltip);
    }

    [Fact]
    public void Retention_ReResolvesAndNotifies_WhenAccountChanges()
    {
        // Tier-dependent, the upload.ee shape: anonymous 50 days, signed in 120.
        FileHosterLoginDto[] accounts = [Login(1, "alice")];
        FileHosterSelectionViewModel vm = new(
            "Upload.ee",
            accounts,
            supportsAnonymous: true,
            retentionResolver: acct => FileRetention.DaysAfterLastDownload(acct?.IsAnonymous == true ? 50 : 120));

        Assert.Equal(TimeSpan.FromDays(120), vm.Retention.Duration);

        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedAccount = vm.AccountOptions[^1]; // the synthetic anonymous entry

        Assert.Equal(TimeSpan.FromDays(50), vm.Retention.Duration);
        Assert.Contains(nameof(FileHosterSelectionViewModel.RetentionDisplay), changed);
        Assert.Contains(nameof(FileHosterSelectionViewModel.RetentionSortKey), changed);
        Assert.Contains(nameof(FileHosterSelectionViewModel.RetentionTooltip), changed);
    }

    [Fact]
    public void DownloadCaptcha_WithoutValue_IsBlank()
    {
        // No pipeline (registry-less test wizard) means no claim at all: the cell stays EMPTY like
        // the size/parallel/kept-for columns do, distinct from a pipeline that answered Unknown.
        FileHosterSelectionViewModel vm = new("Rapidgator", [Login(1, "alice")]);

        Assert.Null(vm.DownloadCaptcha);
        Assert.Equal(string.Empty, vm.DownloadCaptchaDisplay);
        Assert.Null(vm.DownloadCaptchaSortKey);
        Assert.Null(vm.DownloadCaptchaTooltip);
    }

    [Fact]
    public void DownloadCaptcha_Unknown_ShowsDashWithExplainingTooltip()
    {
        FileHosterSelectionViewModel vm = new(
            "Rapidgator", [Login(1, "alice")], downloadCaptcha: DownloadCaptchaRequirement.Unknown);

        Assert.Equal("—", vm.DownloadCaptchaDisplay);
        Assert.Equal(Localizer.Instance["Wizard_Step2_Captcha_UnknownTooltip"], vm.DownloadCaptchaTooltip);

        // Localizer falls back to the raw key when a resx entry is missing — the tooltip must be a
        // sentence, not the key echoed back.
        Assert.NotEqual("Wizard_Step2_Captcha_UnknownTooltip", vm.DownloadCaptchaTooltip);
        Assert.Null(vm.DownloadCaptchaSortKey);
    }

    [Fact]
    public void DownloadCaptcha_NotRequired_ShowsNoWithoutTooltip()
    {
        FileHosterSelectionViewModel vm = new(
            "Catbox", [], supportsAnonymous: true, downloadCaptcha: DownloadCaptchaRequirement.NotRequired);

        Assert.Equal(Localizer.Instance["Common_No"], vm.DownloadCaptchaDisplay);
        Assert.Null(vm.DownloadCaptchaTooltip);
        Assert.Equal(0, vm.DownloadCaptchaSortKey);
    }

    [Fact]
    public void DownloadCaptcha_Required_ShowsYesWithoutTooltip()
    {
        // No cell tooltip on Yes either: the header tooltip carries the column's definition, and a
        // per-cell "premium probably skips it" would be a claim nothing was verified against.
        FileHosterSelectionViewModel vm = new(
            "Rapidgator", [Login(1, "alice")], downloadCaptcha: DownloadCaptchaRequirement.Required);

        Assert.Equal(Localizer.Instance["Common_Yes"], vm.DownloadCaptchaDisplay);
        Assert.Null(vm.DownloadCaptchaTooltip);
        Assert.Equal(1, vm.DownloadCaptchaSortKey);
    }

    private static FileHosterLoginDto Login(int id, string username) => new()
    {
        Id = id,
        FileHosterName = "Rapidgator",
        Username = username,
        Password = "pw",
    };
}
