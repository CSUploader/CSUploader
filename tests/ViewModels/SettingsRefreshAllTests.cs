// <copyright file="SettingsRefreshAllTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// The Accounts page's bottom "Refresh all" button. It always checked every account — the confusion
/// was that the row context menu's "Check / Refresh" looks the same and checks only the selection.
/// <para>
/// What it must NOT do is open a sign-in browser per account: one press over two dozen accounts
/// would become a queue of popups, which is a worse bulk action than none. Those are reported
/// instead, and enabling such a row signs it in one at a time.
/// </para>
/// </summary>
public class SettingsRefreshAllTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly SettingRepository _settingRepo;

    public SettingsRefreshAllTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        TestDbContextFactory factory = new(options);
        using (CSUploaderDbContext db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _loginRepo = new FileHosterLoginRepository(factory);
        _settingRepo = new SettingRepository(factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RefreshAll_ChecksEveryAccount_NotJustASelection()
    {
        // All three sign in with a username and password. KatFile would NOT belong here: it is an
        // API-key hoster, so an account without a key can only get one through a browser and is
        // skipped by design — which is what the next test pins.
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "a"));
        await _loginRepo.InsertAsync(UsernamePassword("Alfafile", "b"));
        await _loginRepo.InsertAsync(UsernamePassword("Easybytez", "c"));

        Mock<IAccountVerifier> verifier = Verifier(valid: true);
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        verifier.Verify(
            v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task RefreshAll_SkipsAnAccountWhoseSessionExpired_RatherThanOpeningABrowser()
    {
        // The case that prompted this: a session hoster whose cookie ran out can only be checked by
        // signing in again, and refresh-all is precisely the unattended path.
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "a"));
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
        });

        Mock<IAccountVerifier> verifier = Verifier(valid: true);
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        // The healthy one was checked; the expired one was not asked at all.
        verifier.Verify(
            v => v.CheckAsync("Rapidgator", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        verifier.Verify(
            v => v.CheckAsync("BowFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshAll_SkipsABrowserSignInHosterThatHoldsNoCredentialYet()
    {
        // KatFile derives its API key behind a sign-in window. With no key stored there is nothing to
        // check WITH, so asking would open that window — the one thing a bulk refresh must not do.
        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "KatFile", Username = "someone" });

        Mock<IAccountVerifier> verifier = Verifier(valid: true);
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        verifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefreshAll_ChecksABrowserSignInHosterThatAlreadyHasItsKey()
    {
        // With the key in hand the check is a plain request — Buzzheavier's re-check reuses the
        // stored account id and never re-opens a browser.
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "KatFile",
            Username = "someone",
            ApiKey = "a-real-key",
        });

        Mock<IAccountVerifier> verifier = Verifier(valid: true);
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        verifier.Verify(
            v => v.CheckAsync("KatFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshAll_SaysHowManyNeedSigningIn()
    {
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "a"));
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
        });

        SettingsViewModel vm = CreateVm(Verifier(valid: true).Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        // A silent skip would read as "everything is fine" — the count is what sends the user to the
        // one row that needs them.
        Assert.Contains("sign", vm.CheckAccountStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAll_WithNothingToSkip_KeepsThePlainSummary()
    {
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "a"));

        SettingsViewModel vm = CreateVm(Verifier(valid: true).Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        Assert.DoesNotContain("sign", vm.CheckAccountStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAll_StillChecksASessionAccountWhoseCookieIsAlive()
    {
        // Only an unusable credential is skipped. A live session refreshes over HTTP with no window.
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(6),
        });

        Mock<IAccountVerifier> verifier = Verifier(valid: true);
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        verifier.Verify(
            v => v.CheckAsync("BowFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshAll_OneFailureDoesNotStopTheRest()
    {
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "a"));
        await _loginRepo.InsertAsync(UsernamePassword("Alfafile", "b"));

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("host down"));
        verifier
            .Setup(v => v.CheckAsync("Alfafile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "OK"));

        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        verifier.Verify(
            v => v.CheckAsync("Alfafile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<IAccountVerifier> Verifier(bool valid)
    {
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(valid, AccountType.Free, valid ? "OK" : "no"));
        return verifier;
    }

    private static FileHosterLoginDto UsernamePassword(string hoster, string user) => new()
    {
        FileHosterName = hoster,
        Username = user,
        Password = "p",
    };

    private SettingsViewModel CreateVm(IAccountVerifier verifier) => new(
        _settingRepo,
        _loginRepo,
        new AppSettings(),
        Mock.Of<IDialogService>(),
        Mock.Of<IAppLogger>(),
        verifier);

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
