// <copyright file="AccountManagerRefreshAllTests.cs" company="CSUploader">
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
public class AccountManagerRefreshAllTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FileHosterLoginRepository _loginRepo;

    public AccountManagerRefreshAllTests()
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
        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

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
        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

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
        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

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
        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

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

        AccountManagerViewModel vm = CreateVm(Verifier(valid: true).Object);
        await vm.LoadAccountsAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        // A silent skip would read as "everything is fine" — the count is what sends the user to the
        // one row that needs them.
        Assert.Contains("sign", vm.CheckAccountStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAll_WithNothingToSkip_KeepsThePlainSummary()
    {
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "a"));

        AccountManagerViewModel vm = CreateVm(Verifier(valid: true).Object);
        await vm.LoadAccountsAsync();

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
        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

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

        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        verifier.Verify(
            v => v.CheckAsync("Alfafile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshAll_ChecksAccountsConcurrently_NotOneAtATime()
    {
        // The point of the change: twelve accounts one at a time is twelve round-trips end to end.
        // Each check here blocks until released, so if any two are ever in flight together the gate
        // opens; a sequential implementation would deadlock on the first and time out.
        // Real registry names, and all plain username/password ones: an unknown hoster is skipped as
        // "no implementation" and a browser-sign-in one is skipped by design, so either would have
        // measured nothing. (My first draft used Host0..Host11 and measured exactly that.)
        string[] hosters =
        [
            "1Fichier", "Alfafile", "BRupload", "Catbox", "FileGarden", "Filehoster.io",
            "GigaPeta", "Gofile", "IcerBox", "MediaFire", "Pixeldrain", "Rapidgator",
        ];
        foreach (string hoster in hosters)
        {
            await _loginRepo.InsertAsync(UsernamePassword(hoster, "u"));
        }

        int inFlight = 0;
        int peak = 0;
        using SemaphoreSlim release = new(0);

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                int now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, now);
                await release.WaitAsync(TimeSpan.FromSeconds(10));
                Interlocked.Decrement(ref inFlight);
                return new AccountCheckResult(true, AccountType.Free, "OK");
            });

        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

        Task refresh = vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        // Let them pile up, then let every waiter through.
        await WaitUntilAsync(() => Volatile.Read(ref peak) > 1, TimeSpan.FromSeconds(10));
        release.Release(64);
        await refresh;

        Assert.True(peak > 1, $"expected overlapping checks, peak was {peak}");

        // …and bounded: the fan-out must not become "all of them at once".
        Assert.True(peak <= 10, $"expected at most 10 in flight, peak was {peak}");
    }

    [Fact]
    public async Task RefreshAll_NeverChecksTwoAccountsOnTheSameHostAtOnce()
    {
        // Several of these sign-ins are rate-limited per account or per IP — UploadGIG answers a
        // second login within the minute with "you can't login a few minutes". Overlapping DIFFERENT
        // hosts is the win; overlapping the same one is how a refresh earns a lockout.
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "one"));
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "two"));
        await _loginRepo.InsertAsync(UsernamePassword("Alfafile", "three"));

        int rapidgatorInFlight = 0;
        int rapidgatorPeak = 0;

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string hoster, string _, string _, string? _, string? _, CancellationToken _) =>
            {
                if (hoster == "Rapidgator")
                {
                    InterlockedMax(ref rapidgatorPeak, Interlocked.Increment(ref rapidgatorInFlight));
                    await Task.Delay(60);
                    Interlocked.Decrement(ref rapidgatorInFlight);
                }
                else
                {
                    await Task.Delay(60);
                }

                return new AccountCheckResult(true, AccountType.Free, "OK");
            });

        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        Assert.Equal(1, rapidgatorPeak);
    }

    [Fact]
    public async Task RefreshAll_AppliesEveryResult_EvenWhenTheyLandOutOfOrder()
    {
        // Results are applied as they complete, not in list order, so a slow first account must not
        // cost the others their status.
        await _loginRepo.InsertAsync(UsernamePassword("Rapidgator", "slow"));
        await _loginRepo.InsertAsync(UsernamePassword("Alfafile", "fast"));
        await _loginRepo.InsertAsync(UsernamePassword("Easybytez", "fast2"));

        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string hoster, string _, string _, string? _, string? _, CancellationToken _) =>
            {
                await Task.Delay(hoster == "Rapidgator" ? 150 : 10);
                return new AccountCheckResult(true, AccountType.Free, $"{hoster} ok");
            });

        AccountManagerViewModel vm = CreateVm(verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        Assert.All(vm.Accounts, a => Assert.Equal(AccountCheckStatus.Valid, a.CheckStatus));
        Assert.All(vm.Accounts, a => Assert.NotNull(a.LastRefreshedDateTime));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int was = Interlocked.CompareExchange(ref target, value, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }
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

    private AccountManagerViewModel CreateVm(IAccountVerifier verifier) => new(
        _loginRepo,
        Mock.Of<IDialogService>(),
        Mock.Of<IAppLogger>(),
        verifier);

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
