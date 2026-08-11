// <copyright file="ExpiredSessionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.ViewModels;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Knowing a stored sign-in is dead BEFORE an upload run.
/// <para>
/// A BowFile session lives 18 hours. Three days later a 716-link package reached it, the pipeline
/// correctly tried to sign in again, and signing in there means a browser window nobody was present
/// to answer — so every file queued for that hoster failed with the same message. The expiry was on
/// the account the whole time; nothing outside the pipeline looked at it.
/// </para>
/// </summary>
public class ExpiredSessionTests
{
    [Fact]
    public void APastExpiryOnASessionAccount_IsKnownWithoutAskingAnyone()
    {
        FileHosterLoginDto account = new()
        {
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(-1),
        };

        Assert.True(account.HasExpiredSession);
    }

    [Fact]
    public void AliveSession_IsNotReportedExpired()
    {
        FileHosterLoginDto account = new()
        {
            FileHosterName = "BowFile",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(1),
        };

        Assert.False(account.HasExpiredSession);
    }

    [Theory]
    // A username/password hoster signs in on demand; it holds no session to lose.
    [InlineData(null, false)]
    // A session with no stated expiry: the app has no basis to call it dead, and guessing would
    // lock people out of accounts that work.
    [InlineData("abc", true)]
    public void OnlyAnAccountThatActuallyStoresAnExpiringSession_CanBeExpired(string? cookie, bool hasExpiry)
    {
        FileHosterLoginDto account = new()
        {
            FileHosterName = "Somewhere",
            SessionCookie = cookie,
            SessionCookieExpiresUtc = hasExpiry ? null : DateTime.UtcNow.AddHours(-1),
        };

        Assert.False(account.HasExpiredSession);
    }

    [Fact]
    public void AnAnonymousEntry_IsNeverExpired()
    {
        // The wizard's synthetic "(anonymous)" option carries no real credentials; treating it as
        // expired would lock the guest route out of every hoster that offers one.
        FileHosterLoginDto anonymous = new()
        {
            FileHosterName = "Catbox",
            IsAnonymous = true,
            SessionCookie = "left-over",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddYears(-1),
        };

        Assert.False(anonymous.HasExpiredSession);
    }

    [Fact]
    public void TheWizardLocksAHosterWhoseChosenAccountHasExpired()
    {
        FileHosterLoginDto expired = new()
        {
            Id = 1,
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
        };

        FileHosterSelectionViewModel row = new("BowFile", [expired]);

        // It has an account, so the old gate would have let it be ticked — and every file would have
        // failed at upload time asking for a sign-in.
        Assert.True(row.HasAccounts);
        Assert.False(row.CanUse);
        Assert.True(row.SelectedAccountSessionExpired);
    }

    [Fact]
    public void ChoosingAnAccountThatStillWorks_UnlocksTheRow()
    {
        // The lock is per ACCOUNT, so a second, healthy account is the fix — no need to leave the
        // wizard when one is already there.
        FileHosterLoginDto expired = new()
        {
            Id = 1,
            FileHosterName = "BowFile",
            Username = "stale",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
        };
        FileHosterLoginDto healthy = new()
        {
            Id = 2,
            FileHosterName = "BowFile",
            Username = "fresh",
            SessionCookie = "def",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(6),
        };

        FileHosterSelectionViewModel row = new("BowFile", [expired, healthy]);
        Assert.False(row.CanUse);

        row.SelectedAccount = healthy;

        Assert.True(row.CanUse);
        Assert.False(row.SelectedAccountSessionExpired);
    }

    [Fact]
    public void SwitchingBackToAnExpiredAccount_UnticksTheRow()
    {
        FileHosterLoginDto expired = new()
        {
            Id = 1,
            FileHosterName = "BowFile",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
        };
        FileHosterLoginDto healthy = new()
        {
            Id = 2,
            FileHosterName = "BowFile",
            SessionCookie = "def",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(6),
        };

        FileHosterSelectionViewModel row = new("BowFile", [healthy, expired]);
        row.Use = true;

        row.SelectedAccount = expired;

        // Use is what the upload reads, so a locked row must not keep a tick from before.
        Assert.False(row.CanUse);
        Assert.False(row.Use);
    }

    [Fact]
    public void ThePadlockExplainsWhichProblemItIs()
    {
        FileHosterLoginDto expired = new()
        {
            Id = 1,
            FileHosterName = "BowFile",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
        };

        FileHosterSelectionViewModel expiredRow = new("BowFile", [expired]);
        FileHosterSelectionViewModel noAccountRow = new("Nowhere", []);

        // "Add an account" is unhelpful advice when one is already there.
        Assert.NotEqual(noAccountRow.UseBlockedTooltip, expiredRow.UseBlockedTooltip);
        Assert.Contains("expired", expiredRow.UseBlockedTooltip, StringComparison.OrdinalIgnoreCase);
    }
}
