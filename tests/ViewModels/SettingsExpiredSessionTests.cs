// <copyright file="SettingsExpiredSessionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// What happens to an account whose stored sign-in has run out: it is switched OFF when the list
/// loads (which happens at startup), and switching it back on RE-VERIFIES it rather than trusting
/// the flag.
/// <para>
/// The two belong together. Enabling used to be a plain flag flip, so on its own the auto-disable
/// would be a merry-go-round: expired, switched off, switched on by the user, still expired, off
/// again at the next start.
/// </para>
/// </summary>
public class SettingsExpiredSessionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly SettingRepository _settingRepo;

    public SettingsExpiredSessionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _loginRepo = new FileHosterLoginRepository(_factory);
        _settingRepo = new SettingRepository(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadingTheList_SwitchesOffAnAccountWhoseSessionHasExpired()
    {
        await _loginRepo.InsertAsync(Expired());

        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.True(row.Disabled);
        Assert.Equal(AccountCheckStatus.Failed, row.CheckStatus);

        // Persisted, not just shown: the wizard reads the repository, and the next start must not
        // have to work it out again.
        FileHosterLoginDto[] stored = await _loginRepo.FindAsync("BowFile");
        Assert.True(Assert.Single(stored).Disabled);
    }

    [Fact]
    public async Task LoadingTheList_LeavesAHealthySessionAlone()
    {
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(6),
        });

        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        Assert.False(Assert.Single(vm.Accounts).Disabled);
    }

    [Fact]
    public async Task LoadingTheList_DoesNotTouchAUsernamePasswordAccount()
    {
        // No stored session means nothing to expire — those hosters sign in on demand.
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        Assert.False(Assert.Single(vm.Accounts).Disabled);
    }

    [Fact]
    public async Task EnablingAnExpiredAccount_SignsItInAgain()
    {
        await _loginRepo.InsertAsync(Expired());

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("BowFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(
                true,
                AccountType.Free,
                "Signed in",
                SessionCookie: "fresh",
                SessionCookieExpiresUtc: DateTime.UtcNow.AddHours(18)));

        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();
        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.True(row.Disabled);   // switched off by the load

        await vm.EnableSelectedAccountsCommand.ExecuteAsync(new List<object> { row });

        // The verifier really ran — that is the whole difference from the old flag flip.
        verifier.Verify(
            v => v.CheckAsync("BowFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        FileHosterLoginDto reloaded = Assert.Single(vm.Accounts);
        Assert.False(reloaded.Disabled);
        Assert.False(reloaded.HasExpiredSession);
    }

    [Fact]
    public async Task EnablingAnExpiredAccount_ThatFailsToSignIn_StaysOff()
    {
        // A cancelled browser sign-in or stale credentials must not leave a switch reading "on"
        // with nothing behind it — the next upload would fail every file.
        await _loginRepo.InsertAsync(Expired());

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("BowFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Sign-in was cancelled"));

        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.EnableSelectedAccountsCommand.ExecuteAsync(new List<object> { Assert.Single(vm.Accounts) });

        FileHosterLoginDto reloaded = Assert.Single(vm.Accounts);
        Assert.True(reloaded.Disabled);
        Assert.Equal(AccountCheckStatus.Failed, reloaded.CheckStatus);

        FileHosterLoginDto[] stored = await _loginRepo.FindAsync("BowFile");
        Assert.True(Assert.Single(stored).Disabled);
    }

    [Fact]
    public async Task AFailedReVerify_DoesNotRenewTheSession_WhichIsWhyTheAccountStaysOff()
    {
        // The mechanism behind the test above, pinned so it can't quietly change: a failed check
        // does NOT copy back a session even when the verifier hands one over (that copy is guarded
        // by IsValid), so the account is still expired when the list reloads — and the reload's rule
        // is what switches it off. I had this backwards first and wrote a test asserting the
        // opposite; it failed, which is how the redundancy in the enable path came out.
        await _loginRepo.InsertAsync(Expired());

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("BowFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(
                false,
                AccountType.Free,
                "Rejected",
                SessionCookie: "offered-but-rejected",
                SessionCookieExpiresUtc: DateTime.UtcNow.AddHours(18)));

        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.EnableSelectedAccountsCommand.ExecuteAsync(new List<object> { Assert.Single(vm.Accounts) });

        FileHosterLoginDto reloaded = Assert.Single(vm.Accounts);
        Assert.True(reloaded.HasExpiredSession);   // the rejected session was NOT taken
        Assert.True(reloaded.Disabled);
    }

    [Fact]
    public async Task EnablingAnAccountWithNoSession_DoesNotCallTheVerifier()
    {
        // Only an expired SESSION forces a re-check. Turning a username/password account back on
        // stays the cheap flag flip it always was.
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
            Disabled = true,
        });

        Mock<IAccountVerifier> verifier = new();
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.EnableSelectedAccountsCommand.ExecuteAsync(new List<object> { Assert.Single(vm.Accounts) });

        verifier.VerifyNoOtherCalls();
        Assert.False(Assert.Single(vm.Accounts).Disabled);
    }

    [Fact]
    public async Task DisablingNeverCallsTheVerifier()
    {
        await _loginRepo.InsertAsync(new FileHosterLoginDto
        {
            FileHosterName = "BowFile",
            Username = "someone",
            SessionCookie = "abc",
            SessionCookieExpiresUtc = DateTime.UtcNow.AddHours(6),
        });

        Mock<IAccountVerifier> verifier = new();
        SettingsViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAsync();

        await vm.DisableSelectedAccountsCommand.ExecuteAsync(new List<object> { Assert.Single(vm.Accounts) });

        verifier.VerifyNoOtherCalls();
        Assert.True(Assert.Single(vm.Accounts).Disabled);
    }

    private static FileHosterLoginDto Expired() => new()
    {
        FileHosterName = "BowFile",
        Username = "someone",
        SessionCookie = "abc",
        SessionCookieExpiresUtc = DateTime.UtcNow.AddDays(-3),
    };

    private SettingsViewModel CreateVm(IAccountVerifier? verifier = null) => new(
        _settingRepo,
        _loginRepo,
        new AppSettings(),
        Mock.Of<IDialogService>(),
        Mock.Of<IAppLogger>(),
        verifier ?? Mock.Of<IAccountVerifier>());

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
