// <copyright file="FileHosterLoginDtoTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;

namespace CSUploader.Tests.Dal;

public class FileHosterLoginDtoTests
{
    [Fact]
    public void StorageAvailableBytes_BothQuotaAndUsedKnown_ReturnsDifference()
    {
        FileHosterLoginDto dto = new()
        {
            StorageUsedBytes = 695056440L,
            StorageQuotaBytes = 10737418240L,
        };

        Assert.Equal(10737418240L - 695056440L, dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_QuotaMissing_ReturnsNull()
    {
        // Hosters that report usage but no cap (none currently — but cover the shape).
        FileHosterLoginDto dto = new() { StorageUsedBytes = 100L };
        Assert.Null(dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_UsedMissing_ReturnsNull()
    {
        // Common XFS-family case: quota known, current usage not exposed.
        FileHosterLoginDto dto = new() { StorageQuotaBytes = 10737418240L };
        Assert.Null(dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_OverQuota_ClampsAtZero()
    {
        // FileBoom doesn't allow going over, but other K2S-family clones might lazily
        // sync; render the cell as "0 B" rather than a negative value.
        FileHosterLoginDto dto = new() { StorageUsedBytes = 100L, StorageQuotaBytes = 50L };
        Assert.Equal(0L, dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_BothNull_ReturnsNull()
    {
        FileHosterLoginDto dto = new();
        Assert.Null(dto.StorageAvailableBytes);
    }

    [Fact]
    public void SetCheckStatus_RaisesPropertyChanged_ForCheckStatusAndStatusMessage()
    {
        // The Accounts grid relies on these notifications to re-render a row in place
        // (instead of replacing the DTO instance) on refresh/enable/disable.
        FileHosterLoginDto dto = new();
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.SetCheckStatus(AccountCheckStatus.Valid, "ok");

        Assert.Contains(nameof(FileHosterLoginDto.CheckStatus), changed);
        Assert.Contains(nameof(FileHosterLoginDto.StatusMessage), changed);
        Assert.Equal(AccountCheckStatus.Valid, dto.CheckStatus);
        Assert.Equal("ok", dto.StatusMessage);
    }

    [Fact]
    public void Username_Setter_RaisesPropertyChanged()
    {
        // ApplySessionCookieIfPresent sets Username in place from the verifier's DerivedUsername
        // (API-key hosters like HitFile), so the grid's {Binding Username} column must be notified.
        FileHosterLoginDto dto = new();
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.Username = "user@example.com";

        Assert.Contains(nameof(FileHosterLoginDto.Username), changed);
        Assert.Equal("user@example.com", dto.Username);
    }

    [Fact]
    public void StorageUsedBytes_Setter_RaisesPropertyChanged_ForUsedAndAvailable()
    {
        // StorageAvailableBytes is computed from StorageUsedBytes/StorageQuotaBytes, so the
        // "Available" column must be told to re-read when either operand changes.
        FileHosterLoginDto dto = new() { StorageQuotaBytes = 1000L };
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.StorageUsedBytes = 250L;

        Assert.Contains(nameof(FileHosterLoginDto.StorageUsedBytes), changed);
        Assert.Contains(nameof(FileHosterLoginDto.StorageAvailableBytes), changed);
        Assert.Equal(750L, dto.StorageAvailableBytes);
    }

    [Fact]
    public void DisplayName_WithUsername_PrefersUsernameOverApiKey()
    {
        FileHosterLoginDto dto = new() { Username = "user@example.com", ApiKey = "0123456789abcdef" };
        Assert.Equal("user@example.com", dto.DisplayName);
    }

    [Fact]
    public void DisplayName_NoUsername_WithApiKey_ReturnsFirstSixCharsMasked()
    {
        // API-key hosters (Ufile, NitroFlare) capture only a key, no email — the picker shows a
        // partly-masked key so several key-only accounts stay distinguishable.
        FileHosterLoginDto dto = new() { Username = null, ApiKey = "12GHte7890abcdef" };
        Assert.Equal("12GHte**", dto.DisplayName);
    }

    [Fact]
    public void DisplayName_WhitespaceUsername_FallsBackToMaskedApiKey()
    {
        FileHosterLoginDto dto = new() { Username = "   ", ApiKey = "12GHte7890abcdef" };
        Assert.Equal("12GHte**", dto.DisplayName);
    }

    [Fact]
    public void DisplayName_ApiKeyShorterThanSix_MasksWholeKey()
    {
        FileHosterLoginDto dto = new() { Username = null, ApiKey = "ab12" };
        Assert.Equal("ab12**", dto.DisplayName);
    }

    [Fact]
    public void DisplayName_NoUsernameNoApiKey_ReturnsEmpty()
    {
        FileHosterLoginDto dto = new();
        Assert.Equal(string.Empty, dto.DisplayName);
    }

    [Fact]
    public void DisplayName_AnApiKeyThatIsAUrl_IsNotMaskedIntoALabel()
    {
        // Reported from the app: a FileStore account showed as "https:**". That slot holds its
        // captured upload NODE, and six characters of a URL is "https:" for every account on the
        // host — a name that distinguishes nothing and reads like a bug. Empty is at least honest
        // about knowing no name.
        FileHosterLoginDto dto = new()
        {
            Username = null,
            ApiKey = "https://srv9.filestore.me/cgi-bin/upload.cgi?upload_type=file&utype=reg",
        };

        Assert.Equal(string.Empty, dto.DisplayName);
    }

    [Theory]
    [InlineData("htt9sk20xyz")]
    [InlineData("http7a9x2b")]     // a real key may begin with those four letters
    [InlineData("httpsecret1")]
    public void DisplayName_AKeyThatMerelyStartsLikeAScheme_IsStillMasked(string key)
    {
        // The guard keys on an actual URL SCHEME ("http://" / "https://"), not on the letters — a key
        // beginning "http" is still a key, and still the only thing telling two key-only accounts
        // apart. Keying on the letters alone would silently blank those accounts' names.
        FileHosterLoginDto dto = new() { Username = null, ApiKey = key };

        Assert.Equal(string.Concat(key.AsSpan(0, Math.Min(6, key.Length)), "**"), dto.DisplayName);
    }

    [Fact]
    public void Username_Setter_AlsoNotifiesDisplayName()
    {
        // The account pickers bind DisplayName; a live refresh that fills Username in place must
        // re-render the dropdown/label, so the Username setter cascades DisplayName too.
        FileHosterLoginDto dto = new() { ApiKey = "12GHte7890abcdef" };
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.Username = "user@example.com";

        Assert.Contains(nameof(FileHosterLoginDto.Username), changed);
        Assert.Contains(nameof(FileHosterLoginDto.DisplayName), changed);
    }

    [Fact]
    public void ApiKey_Setter_AlsoNotifiesDisplayName()
    {
        // The Settings Accounts grid (live-refreshing) binds DisplayName; a refresh that rotates a
        // key-only account's ApiKey in place must re-render, so the ApiKey setter cascades DisplayName.
        FileHosterLoginDto dto = new();
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.ApiKey = "12GHte7890abcdef";

        Assert.Contains(nameof(FileHosterLoginDto.ApiKey), changed);
        Assert.Contains(nameof(FileHosterLoginDto.DisplayName), changed);
    }
}
