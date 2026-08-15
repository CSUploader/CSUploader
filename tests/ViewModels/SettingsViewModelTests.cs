// <copyright file="SettingsViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

[Collection(LocalizerCollection.Name)]
public class SettingsViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly SettingRepository _settingRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly AppSettings _appSettings;
    private readonly CultureInfo _originalCulture;

    public SettingsViewModelTests()
    {
        // Several tests mutate Localizer.Instance.Culture (LoadAsync auto-detects, the
        // language-edit test reassigns). Snapshot now and restore on dispose so we don't
        // bleed into other classes — even though the Localizer collection serializes us,
        // a leaked culture would still affect the next test in this class.
        _originalCulture = Localizer.Instance.Culture;

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

        _settingRepo = new SettingRepository(_factory);
        _loginRepo = new FileHosterLoginRepository(_factory);
        _appSettings = new AppSettings();
    }

    public void Dispose()
    {
        _connection.Dispose();
        Localizer.Instance.Culture = _originalCulture;
        GC.SuppressFinalize(this);
    }

    private SettingsViewModel CreateVm(
        IDialogService? dialog = null,
        LogEntryRepository? logRepo = null,
        IAccountVerifier? verifier = null,
        CSUploader.Upload.Pipeline.IFileHosterRegistry? registry = null) =>
        new(_settingRepo, CreateAccountVm(dialog, verifier, registry), _appSettings, dialog ?? Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), logEntryRepository: logRepo);

    /// <summary>The account-manager half, for the tests that drive account behavior directly (the
    /// split moved those members off SettingsViewModel).</summary>
    private AccountManagerViewModel CreateAccountVm(
        IDialogService? dialog = null,
        IAccountVerifier? verifier = null,
        CSUploader.Upload.Pipeline.IFileHosterRegistry? registry = null) =>
        new(_loginRepo, dialog ?? Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), verifier ?? Mock.Of<IAccountVerifier>(), registry);

    [Fact]
    public void AvailableHosters_LeavesOutHostsThatHaveNoLoginAtAll()
    {
        // Reported: GigaFile was selectable under Add Account, and GigaFile has no login anywhere on
        // the site. Picking it could only ever end in a check failing with "this host has no accounts".
        CSUploader.Upload.Pipeline.DefaultFileHosterRegistry registry = new([
            new CSUploader.Upload.Pipeline.Hosters.GigaFilePipeline(),
            new CSUploader.Upload.Pipeline.Hosters.TempShPipeline(),
            new CSUploader.Upload.Pipeline.Hosters.UpZurPipeline(),
        ]);

        string[] offered = CreateAccountVm(registry: registry).AvailableHosters;

        Assert.DoesNotContain("GigaFile", offered);
        Assert.DoesNotContain("Temp.sh", offered);

        // UpZur is anonymous-capable AND has accounts — being one must not exclude it, or every
        // dual-mode host (catbox, gofile, ufile, upload.ee) would vanish from the dialog too.
        Assert.Contains("UpZur", offered);

        // Hosters with no registered pipeline are left in rather than silently dropped.
        Assert.Contains("Rapidgator", offered);
    }

    [Fact]
    public void AvailableHosters_WithoutARegistry_KeepsEveryHoster()
    {
        // The registry is optional; without it the property behaves exactly as it did before, so a
        // test or head that doesn't wire one can't end up with an empty dialog.
        Assert.Contains("GigaFile", CreateAccountVm().AvailableHosters);
    }

    // Polls the DB briefly because each property's auto-save is fire-and-forget.
    private async Task<string?> WaitForSettingValueAsync(string key)
    {
        for (int i = 0; i < 50; i++)
        {
            SettingDto? row = await _settingRepo.FindByKeyAsync(key);
            if (row is not null)
            {
                return row.Value;
            }

            await Task.Delay(20);
        }

        return null;
    }

    [Fact]
    public void Language_NullOrBlankPush_IsIgnored_KeepsCurrentAndDoesNotThrow()
    {
        // The language ComboBox binds SelectedValue two-way with a SelectedValueBinding, and Avalonia
        // pushes a transient null back on attach (it matches SelectedValue before the value binding
        // resolves). The setter must ignore null/blank: it previously crashed OnLanguageChanged's
        // new CultureInfo(null) with ArgumentNullException, and storing the null would blank the
        // dropdown. The current language must be preserved.
        SettingsViewModel vm = CreateVm();
        string current = vm.Language;

        vm.Language = null!;
        vm.Language = string.Empty;
        vm.Language = "   ";

        Assert.Equal(current, vm.Language);
    }

    [Fact]
    public async Task LoadAsync_DoesNotPersistOnHydrate()
    {
        // The auto-save partials must short-circuit during LoadAsync — otherwise hydrating
        // the VM from existing DB rows would re-write them on every launch.
        SettingsViewModel vm = CreateVm();

        await vm.LoadAsync();

        // No properties were edited; the row count for the auto-saved keys should be zero.
        Assert.Null(await _settingRepo.FindByKeyAsync(SettingKey.MaxConcurrentCPUJobs));
        Assert.Null(await _settingRepo.FindByKeyAsync(SettingKey.GridFontFamily));
        Assert.Null(await _settingRepo.FindByKeyAsync(SettingKey.MinimizeToTray));
    }

    [Fact]
    public async Task EditingMaxConcurrentCPUJobs_AutoPersistsToDatabase()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.MaxConcurrentCPUJobs = 7;

        Assert.Equal("7", await WaitForSettingValueAsync(SettingKey.MaxConcurrentCPUJobs));
        Assert.Equal(7, _appSettings.MaxConcurrentCPUJobs);
    }

    [Fact]
    public async Task EditingGridFontFamily_AutoPersistsToDatabase()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.GridFontFamily = "Comic Sans MS";

        Assert.Equal("Comic Sans MS", await WaitForSettingValueAsync(SettingKey.GridFontFamily));
        Assert.Equal("Comic Sans MS", _appSettings.GridFontFamily);
    }

    [Fact]
    public async Task EditingMinimizeToTray_AutoPersistsAndUpdatesAppSettings()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.MinimizeToTray = true;

        Assert.Equal("true", await WaitForSettingValueAsync(SettingKey.MinimizeToTray));
        Assert.True(_appSettings.MinimizeToTray);
    }

    [Fact]
    public async Task EditingShowCompletionToasts_AutoPersistsAndUpdatesAppSettings()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.ShowCompletionToasts = false;

        Assert.Equal("false", await WaitForSettingValueAsync(SettingKey.ShowCompletionToasts));
        Assert.False(_appSettings.ShowCompletionToasts);
    }

    [Fact]
    public async Task EditingCloseAction_AutoPersistsAndUpdatesAppSettings()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.CloseAction = CloseAction.MinimizeToTray;

        Assert.Equal(nameof(CloseAction.MinimizeToTray), await WaitForSettingValueAsync(SettingKey.CloseAction));
        Assert.Equal(CloseAction.MinimizeToTray, _appSettings.CloseAction);
    }

    [Fact]
    public async Task EditingAutostartUploads_AutoPersistsToDatabase()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.AutostartUploads = AutostartUploadsMode.Never;

        Assert.Equal(nameof(AutostartUploadsMode.Never), await WaitForSettingValueAsync(SettingKey.AutostartUploads));
        Assert.Equal(AutostartUploadsMode.Never, _appSettings.AutostartUploads);
    }

    [Fact]
    public async Task EditingRemoveFinishedUploads_AutoPersistsToDatabase()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately;

        Assert.Equal(nameof(RemoveFinishedUploadsMode.Immediately), await WaitForSettingValueAsync(SettingKey.RemoveFinishedUploads));
        Assert.Equal(RemoveFinishedUploadsMode.Immediately, _appSettings.RemoveFinishedUploads);
    }

    [Fact]
    public async Task LoadAsync_AutoDetectsLanguage_WhenNoneSaved()
    {
        // Empty Language in DB → PickSupportedLanguage walks the OS culture chain. We can't
        // assert a specific tag (depends on the test host's culture) but it must be one of
        // the four shipped languages and Localizer must end up on that culture.
        SettingsViewModel vm = CreateVm();

        await vm.LoadAsync();

        Assert.Contains(vm.Language, Localizer.SupportedLanguages);
        Assert.Equal(vm.Language, _appSettings.Language);
        Assert.Equal(vm.Language, Localizer.Instance.Culture.Name);
    }

    [Fact]
    public async Task LoadAsync_HydratesSavedLanguage()
    {
        await _settingRepo.InsertAsync(new SettingDto { Key = SettingKey.Language, Value = "ja" });
        SettingsViewModel vm = CreateVm();

        await vm.LoadAsync();

        Assert.Equal("ja", vm.Language);
        Assert.Equal("ja", _appSettings.Language);
        Assert.Equal("ja", Localizer.Instance.Culture.Name);
    }

    [Fact]
    public async Task EditingLanguage_AutoPersistsAndAppliesCultureLive()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();
        Localizer.Instance.Culture = new CultureInfo("en");

        vm.Language = "zh-Hans";

        Assert.Equal("zh-Hans", await WaitForSettingValueAsync(SettingKey.Language));
        Assert.Equal("zh-Hans", _appSettings.Language);
        Assert.Equal("zh-Hans", Localizer.Instance.Culture.Name);
    }

    [Fact]
    public async Task ClearLogsAsync_WhenConfirmed_DeletesEveryPersistedEntry()
    {
        LogEntryRepository logRepo = new(_factory);
        await logRepo.InsertAsync(new LogEntryDto { DateTime = DateTime.Now.AddDays(-1), LogType = LogType.Status, Message = "old" });
        await logRepo.InsertAsync(new LogEntryDto { DateTime = DateTime.Now, LogType = LogType.Error, Message = "new" });

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(true);

        SettingsViewModel vm = CreateVm(dialog.Object, logRepo);

        await vm.ClearLogsCommand.ExecuteAsync(null);

        LogEntryDto[] remaining = await logRepo.GetAllAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task ClearLogsAsync_WhenDeclined_KeepsEveryEntry()
    {
        LogEntryRepository logRepo = new(_factory);
        await logRepo.InsertAsync(new LogEntryDto { DateTime = DateTime.Now, LogType = LogType.Status, Message = "kept" });

        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(false);

        SettingsViewModel vm = CreateVm(dialog.Object, logRepo);

        await vm.ClearLogsCommand.ExecuteAsync(null);

        LogEntryDto[] remaining = await logRepo.GetAllAsync();
        Assert.Single(remaining);
    }

    [Fact]
    public async Task EditingSpeedLimitEnabled_AutoPersistsToDatabase()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();
        vm.SpeedLimitValue = 2048;
        await Task.Delay(40); // let the SpeedLimitValue write settle

        vm.SpeedLimitEnabled = true;

        Assert.Equal("2048", await WaitForSettingValueAsync(SettingKey.SpeedLimit));
        Assert.Equal(2048, _appSettings.SpeedLimit);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_ShowsRowWithCheckingStatusBeforeVerifierReturns()
    {
        // Regression: the dialog flow used to await the verifier (~3s) BEFORE inserting,
        // so the new account appeared in the grid only after verification finished. The
        // fix inserts first with StatusMessage = "Checking..." then updates the row once
        // the verifier returns. This test gates the verifier on a TCS so we can observe
        // the in-flight state.
        TaskCompletionSource<AccountCheckResult> gate = new();
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        FileHosterLoginDto dto = new()
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
            AccountType = AccountType.Free,
        };

        // Don't await — we want to observe the state while the verifier is still pending.
        Task addTask = vm.AddAccountFromDialogAsync(dto);

        // Phase 1: row visible with CheckStatus=Checking before verification completes.
        await WaitForAsync(() => vm.Accounts.Count == 1 && vm.IsCheckingAccount);
        FileHosterLoginDto inFlight = Assert.Single(vm.Accounts);
        Assert.Equal("u", inFlight.Username);
        Assert.Equal(AccountCheckStatus.Checking, inFlight.CheckStatus);
        Assert.Equal(Localizer.Instance["Settings_Accounts_Status_CheckingShort"], inFlight.StatusMessage);
        Assert.True(vm.IsCheckingAccount);

        // Phase 2: release the verifier — the row's status flips to Valid with the
        // verifier's message, the type is updated, and the in-flight flag clears.
        gate.SetResult(new AccountCheckResult(true, AccountType.Premium, "Premium until 2099"));
        await addTask;

        FileHosterLoginDto settled = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Valid, settled.CheckStatus);
        Assert.Equal("Premium until 2099", settled.StatusMessage);
        Assert.Equal(AccountType.Premium, settled.AccountType);
        Assert.False(vm.IsCheckingAccount);

        // Persisted account type matches what the verifier returned. StatusMessage is
        // UI-only (not in the schema), so the DB-loaded copy always reads "Not checked"
        // — the live VM collection carries the transient verification message.
        FileHosterLoginDto[] persisted = await _loginRepo.FindAsync("Rapidgator");
        FileHosterLoginDto row = Assert.Single(persisted);
        Assert.Equal(AccountType.Premium, row.AccountType);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_VerifierFailure_SetsFailedStatusOnRow()
    {
        // Pre-refactor the row was left with an empty StatusMessage when the verifier
        // threw — only the global status bar carried the error. Now the row carries
        // CheckStatus=Failed (drives red cell) and the exception message.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DNS failure"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Failed, row.CheckStatus);
        Assert.Contains("DNS failure", row.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.IsCheckingAccount);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_VerifierReturnsInvalid_SetsFailedStatusOnRow()
    {
        // Verifier IsValid=false (e.g. wrong password) buckets into Failed — same red
        // cell as a transport exception. The user-visible difference is the message.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Failed, row.CheckStatus);
        Assert.Equal("Wrong password", row.StatusMessage);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_VerifierReturnsNullStorage_PreservesDtoStorage()
    {
        // Regression: HitFile captures storage usage at WebView sign-in and the dialog carries
        // it onto the DTO. The post-add re-verify runs CheckAccountAsync with the stored appId,
        // which early-returns IsValid with NULL storage (it has no live session to re-read it).
        // ApplySessionCookieIfPresent must preserve the carried value on null — otherwise the
        // Used/Available cells go blank right after adding the account.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("HitFile", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "HitFile account ready.", ApiKey: "APPID123"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "HitFile",
            Username = "user@example.com",
            ApiKey = "APPID123",
            StorageUsedBytes = 15663360, // carried from the sign-in probe
            StorageQuotaBytes = null,     // unlimited
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Valid, row.CheckStatus);
        Assert.Equal(15663360, row.StorageUsedBytes); // not clobbered by the null re-verify
        Assert.Null(row.StorageQuotaBytes);
        Assert.Equal("user@example.com", row.Username); // likewise preserved
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_UnsupportedHoster_SkipsVerifierAndMarksUnsupported()
    {
        // FileHosterClient.FindByHost only returns non-null for hosters in the master
        // FileHosters dictionary. "TotallyMadeUpHoster" isn't there, so willCheck=false
        // — the verifier is never invoked and the row lands as Unsupported (grey).
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("verifier should never be called"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "TotallyMadeUpHoster",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Unsupported, row.CheckStatus);
        verifier.Verify(
            v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No verifier round-trip happened → LastRefreshedDateTime stays null.
        Assert.Null(row.LastRefreshedDateTime);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_VerifierSuccess_StampsLastRefreshedDateTime()
    {
        // Verifier round-trip completed (even though it succeeded) — the row's
        // LastRefreshedDateTime should be stamped close to DateTime.Now and persisted.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "Logged in"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        DateTime before = DateTime.Now;

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.NotNull(row.LastRefreshedDateTime);
        Assert.InRange(row.LastRefreshedDateTime!.Value, before.AddSeconds(-1), DateTime.Now.AddSeconds(1));

        // And it survived the persist → mapper round-trip.
        FileHosterLoginDto persisted = (await _loginRepo.FindAsync(row.Id))!;
        Assert.NotNull(persisted.LastRefreshedDateTime);
        Assert.InRange(persisted.LastRefreshedDateTime!.Value, before.AddSeconds(-1), DateTime.Now.AddSeconds(1));
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_StampsCreatedDateTimeOnce()
    {
        // The "Added at" column: a fresh add gets stamped now and persists. (The dialog never
        // carries a CreatedDateTime on an add, so the VM sets it.)
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "Logged in"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        DateTime before = DateTime.Now;

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.NotNull(row.CreatedDateTime);
        Assert.InRange(row.CreatedDateTime!.Value, before.AddSeconds(-1), DateTime.Now.AddSeconds(1));

        FileHosterLoginDto persisted = (await _loginRepo.FindAsync(row.Id))!;
        Assert.NotNull(persisted.CreatedDateTime);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_VerifierFailure_StillStampsLastRefreshedDateTime()
    {
        // "We tried" — not "we succeeded". Failed verify still records the attempt
        // timestamp so the user sees freshness regardless of outcome.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        DateTime before = DateTime.Now;

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Failed, row.CheckStatus);
        Assert.NotNull(row.LastRefreshedDateTime);
        Assert.InRange(row.LastRefreshedDateTime!.Value, before.AddSeconds(-1), DateTime.Now.AddSeconds(1));
    }

    [Fact]
    public async Task RefreshSelectedAccounts_VerifierFailure_StampsLastRefreshedDateTime()
    {
        // Refresh-Selected (context menu) wires through RefreshSingleAccountAsync. Both
        // the IsValid and !IsValid branches now stamp LastRefreshedDateTime on the DTO
        // and persist; this test exercises the !IsValid branch.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        FileHosterLoginDto seed = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        await _loginRepo.InsertAsync(seed);

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        DateTime before = DateTime.Now;

        FileHosterLoginDto target = vm.Accounts.Single(a => a.Id == seed.Id);
        await vm.RefreshSelectedAccountsCommand.ExecuteAsync(new List<FileHosterLoginDto> { target });

        FileHosterLoginDto persisted = (await _loginRepo.FindAsync(seed.Id))!;
        Assert.NotNull(persisted.LastRefreshedDateTime);
        Assert.InRange(persisted.LastRefreshedDateTime!.Value, before.AddSeconds(-1), DateTime.Now.AddSeconds(1));
    }

    [Fact]
    public async Task RefreshSelectedAccounts_PreservesSelectedRow()
    {
        // Refresh now updates the selected row IN PLACE (FileHosterLoginDto is observable),
        // so the collection is NOT rebuilt and the selected DTO instance is NOT replaced.
        // The bound SelectedAccount must therefore remain the SAME live instance, proving the
        // refresh updated the row in place rather than reloading — which preserves the grid's
        // highlight naturally. The status/timestamp assertions confirm the in-place update
        // actually landed on that instance.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "OK"));

        FileHosterLoginDto seed = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        await _loginRepo.InsertAsync(seed);

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        FileHosterLoginDto target = vm.Accounts.Single(a => a.Id == seed.Id);
        vm.SelectedAccount = target;

        await vm.RefreshSelectedAccountsCommand.ExecuteAsync(new List<FileHosterLoginDto> { target });

        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal(seed.Id, vm.SelectedAccount!.Id);
        Assert.Contains(vm.SelectedAccount, vm.Accounts);
        // SAME live instance — the row was updated in place, not replaced by a reload.
        Assert.Same(target, vm.SelectedAccount);
        // ...and the in-place update actually happened on that instance.
        Assert.Equal(AccountCheckStatus.Valid, target.CheckStatus);
        Assert.NotNull(target.LastRefreshedDateTime);
    }

    [Fact]
    public async Task RefreshSelectedAccounts_VerifierFailure_AutoDisablesAccount()
    {
        // A failed check auto-disables the account so a broken account is excluded from uploads
        // until fixed. Refresh-selected updates in place (no reload) — the SAME live instance flips
        // Disabled=true (the grid unticks/dims via INPC), and the flag is persisted.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        FileHosterLoginDto seed = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        await _loginRepo.InsertAsync(seed);

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        FileHosterLoginDto target = vm.Accounts.Single(a => a.Id == seed.Id);
        Assert.False(target.Disabled);

        await vm.RefreshSelectedAccountsCommand.ExecuteAsync(new List<FileHosterLoginDto> { target });

        Assert.True(target.Disabled);
        Assert.Equal(AccountCheckStatus.Failed, target.CheckStatus);
        Assert.True((await _loginRepo.FindAsync(seed.Id))!.Disabled);
    }

    [Fact]
    public async Task RefreshSelectedAccounts_VerifierSuccess_DoesNotReEnableDisabledAccount()
    {
        // Asymmetry by design (a deliberate product decision): auto-disable fires on failure, but a later PASSING
        // check must NOT silently re-enable an account — re-enabling stays a deliberate user action
        // via the Enable context-menu command. Disable first (real command), then a valid refresh
        // leaves it disabled.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "OK"));

        FileHosterLoginDto seed = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        await _loginRepo.InsertAsync(seed);

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        FileHosterLoginDto target = vm.Accounts.Single(a => a.Id == seed.Id);
        await vm.DisableSelectedAccountsCommand.ExecuteAsync(new List<FileHosterLoginDto> { target });
        // The disable path reloads the collection — re-resolve the live instance.
        target = vm.Accounts.Single(a => a.Id == seed.Id);
        Assert.True(target.Disabled);

        await vm.RefreshSelectedAccountsCommand.ExecuteAsync(new List<FileHosterLoginDto> { target });

        Assert.True(vm.Accounts.Single(a => a.Id == seed.Id).Disabled);
        Assert.True((await _loginRepo.FindAsync(seed.Id))!.Disabled);
    }

    [Fact]
    public async Task RefreshAllAccounts_VerifierFailure_AutoDisablesAccount()
    {
        // Refresh-all reloads the collection at the end — the rebuilt row reads Disabled=true from
        // the DB (the in-loop UpdateAsync persisted it).
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        FileHosterLoginDto seed = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        await _loginRepo.InsertAsync(seed);

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.RefreshAllAccountsCommand.ExecuteAsync(null);

        FileHosterLoginDto row = vm.Accounts.Single(a => a.Id == seed.Id);
        Assert.True(row.Disabled);
        Assert.Equal(AccountCheckStatus.Failed, row.CheckStatus);
        Assert.True((await _loginRepo.FindAsync(seed.Id))!.Disabled);
    }

    [Fact]
    public async Task AddAccountFromDialogAsync_VerifierFailure_AutoDisablesAccount()
    {
        // A newly added account whose check fails is added but auto-disabled (same rule as refresh).
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Failed, row.CheckStatus);
        Assert.True(row.Disabled);
        Assert.True((await _loginRepo.FindAsync(row.Id))!.Disabled);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        for (int i = 0; i < timeoutMs / 10; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("Condition was not met within timeout");
    }

    [Fact]
    public async Task ReloadAccountsAsync_PicksUpAnAccountAddedOUTSIDEThisView()
    {
        // The upload wizard's "Add account…" writes straight to the repository, and this VM is a
        // singleton whose list is otherwise filled once at startup — so the account was invisible in
        // Settings until the app restarted. MainViewModel calls this when the Settings tab is shown.
        AccountManagerViewModel vm = CreateAccountVm();
        await vm.LoadAccountsAsync();
        Assert.Empty(vm.Accounts);

        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "DDownload", Username = "added_by_wizard" });

        vm.ReloadAccountsAsync();
        await WaitForAsync(() => vm.Accounts.Count == 1);
        Assert.Equal("added_by_wizard", Assert.Single(vm.Accounts).Username);
    }

    [Fact]
    public async Task ReloadAccountsAsync_KeepsTheHighlightedRow()
    {
        // A refresh on every visit to the tab must not yank the user's selection out from under them.
        AccountManagerViewModel vm = CreateAccountVm();
        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "KatFile", Username = "first" });
        await _loginRepo.InsertAsync(new FileHosterLoginDto { FileHosterName = "Uploady", Username = "second" });
        await vm.LoadAccountsAsync();

        FileHosterLoginDto second = vm.Accounts.Single(a => a.Username == "second");
        vm.SelectedAccount = second;

        vm.ReloadAccountsAsync();
        await WaitForAsync(() => vm.Accounts.Count == 2 && !ReferenceEquals(vm.SelectedAccount, second));

        // Re-selected by id onto the FRESH instance, not left pointing at the discarded one.
        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal("second", vm.SelectedAccount!.Username);
        Assert.Contains(vm.SelectedAccount, vm.Accounts);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task SaveEditedAccountAsync_ReChecksTheCorrectedCredentials()
    {
        // Reported: fix a wrong username, press OK, and the row keeps its old (red) verdict — an edit is
        // nearly always a correction, so it has to re-check the way Add does.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "corrected", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Premium, "Signed in"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "Rapidgator",
            Username = "wrong",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        row.Username = "corrected";
        await vm.SaveEditedAccountAsync(row);

        verifier.Verify(
            v => v.CheckAsync("Rapidgator", "corrected", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        FileHosterLoginDto after = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Valid, after.CheckStatus);
        Assert.NotNull(after.LastRefreshedDateTime);
        Assert.False(vm.IsCheckingAccount);
    }

    [Fact]
    public async Task SaveEditedAccountAsync_PersistsTheKeyTheReCheckDerives()
    {
        // For FileMirage (and Pixeldrain) the real upload credential is an API key the verifier hands
        // back at sign-in. Without the re-check a corrected account is saved with a stale key — or with
        // none at all — and every upload afterwards fails.
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "ok", ApiKey: "FRESH-DERIVED-KEY"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "u", Password = "p" });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        row.ApiKey = null;
        await vm.SaveEditedAccountAsync(row);

        Assert.Equal("FRESH-DERIVED-KEY", Assert.Single(vm.Accounts).ApiKey);
    }

    [Fact]
    public async Task SaveEditedAccountAsync_AFailedReCheckShowsOnTheRow()
    {
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync("Rapidgator", "still-wrong", "p", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        AccountManagerViewModel vm = CreateAccountVm(verifier: verifier.Object);
        await vm.LoadAccountsAsync();
        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "u", Password = "p" });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        row.Username = "still-wrong";
        await vm.SaveEditedAccountAsync(row);

        FileHosterLoginDto after = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Failed, after.CheckStatus);
        Assert.Equal("Wrong password", after.StatusMessage);
        Assert.False(vm.IsCheckingAccount);
    }
}
