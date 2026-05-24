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

    private SettingsViewModel CreateVm(IDialogService? dialog = null, LogEntryRepository? logRepo = null, IAccountVerifier? verifier = null) =>
        new(_settingRepo, _loginRepo, _appSettings, dialog ?? Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), logEntryRepository: logRepo, accountVerifier: verifier);

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
        dialog.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string?>())).Returns(true);

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
        dialog.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string?>())).Returns(false);

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
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        SettingsViewModel vm = CreateVm(verifier: verifier.Object);
        await vm.LoadAsync();

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
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DNS failure"));

        SettingsViewModel vm = CreateVm(verifier: verifier.Object);
        await vm.LoadAsync();

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
            .Setup(v => v.CheckAsync("Rapidgator", "u", "p", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(false, AccountType.Free, "Wrong password"));

        SettingsViewModel vm = CreateVm(verifier: verifier.Object);
        await vm.LoadAsync();

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
    public async Task AddAccountFromDialogAsync_UnsupportedHoster_SkipsVerifierAndMarksUnsupported()
    {
        // FileHosterClient.FindByHost only returns non-null for hosters in the master
        // FileHosters dictionary. "TotallyMadeUpHoster" isn't there, so willCheck=false
        // — the verifier is never invoked and the row lands as Unsupported (grey).
        Mock<IAccountVerifier> verifier = new();
        verifier
            .Setup(v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("verifier should never be called"));

        SettingsViewModel vm = CreateVm(verifier: verifier.Object);
        await vm.LoadAsync();

        await vm.AddAccountFromDialogAsync(new FileHosterLoginDto
        {
            FileHosterName = "TotallyMadeUpHoster",
            Username = "u",
            Password = "p",
        });

        FileHosterLoginDto row = Assert.Single(vm.Accounts);
        Assert.Equal(AccountCheckStatus.Unsupported, row.CheckStatus);
        verifier.Verify(
            v => v.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
