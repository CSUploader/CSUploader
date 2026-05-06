// <copyright file="SettingsViewModelTests.cs" company="CSUploader">
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

public class SettingsViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly SettingRepository _settingRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly AppSettings _appSettings;

    public SettingsViewModelTests()
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

        _settingRepo = new SettingRepository(_factory);
        _loginRepo = new FileHosterLoginRepository(_factory);
        _appSettings = new AppSettings();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private SettingsViewModel CreateVm(IDialogService? dialog = null) =>
        new(_settingRepo, _loginRepo, _appSettings, dialog ?? Mock.Of<IDialogService>(), Mock.Of<IAppLogger>());

    [Fact]
    public async Task HasUnsavedChanges_AfterLoad_IsFalse()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task HasUnsavedChanges_AfterEditingProperty_BecomesTrue()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.GridFontFamily = "Comic Sans MS";

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task TryConfirmDiscardChanges_WithNoChanges_ReturnsTrueWithoutPrompting()
    {
        Mock<IDialogService> dialog = new();
        SettingsViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();

        Assert.True(vm.TryConfirmDiscardChanges());

        dialog.Verify(
            d => d.ShowOptOutConfirmation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task TryConfirmDiscardChanges_WhenUserConfirms_RevertsPropertiesAndReturnsTrue()
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(
                ConfirmationKeys.DiscardSettingsChanges,
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(true);

        SettingsViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();
        string original = vm.GridFontFamily;
        vm.GridFontFamily = "Comic Sans MS";

        bool result = vm.TryConfirmDiscardChanges();

        Assert.True(result);
        Assert.Equal(original, vm.GridFontFamily);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task TryConfirmDiscardChanges_WhenUserDeclines_KeepsChangesAndReturnsFalse()
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(
                ConfirmationKeys.DiscardSettingsChanges,
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(false);

        SettingsViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();
        vm.GridFontFamily = "Comic Sans MS";

        bool result = vm.TryConfirmDiscardChanges();

        Assert.False(result);
        Assert.Equal("Comic Sans MS", vm.GridFontFamily);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveCommand_AfterEditing_ClearsUnsavedChanges()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();
        vm.GridFontFamily = "Comic Sans MS";
        Assert.True(vm.HasUnsavedChanges);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task HasUnsavedChanges_AfterEditingMinimizeToTray_BecomesTrue()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.MinimizeToTray = !vm.MinimizeToTray;

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveCommand_PersistsCloseActionAndMinimizeToTray()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();
        vm.MinimizeToTray = true;
        vm.CloseAction = CloseAction.MinimizeToTray;

        await vm.SaveCommand.ExecuteAsync(null);

        // Reload from DB into a fresh VM and check that values stuck.
        SettingsViewModel reloaded = CreateVm();
        await reloaded.LoadAsync();

        Assert.True(reloaded.MinimizeToTray);
        Assert.Equal(CloseAction.MinimizeToTray, reloaded.CloseAction);
    }

    [Fact]
    public async Task HasUnsavedChanges_AfterEditingCloseAction_BecomesTrue()
    {
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();

        vm.CloseAction = CloseAction.Exit;

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task TryConfirmDiscardChanges_WhenUserConfirms_RevertsWindowBehaviorSettings()
    {
        // CloseAction and MinimizeToTray go through the same snapshot/restore path as the
        // other tracked settings — make sure the discard flow actually rolls them back.
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmation(
                ConfirmationKeys.DiscardSettingsChanges,
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(true);

        SettingsViewModel vm = CreateVm(dialog.Object);
        await vm.LoadAsync();
        CloseAction originalAction = vm.CloseAction;
        bool originalMinimize = vm.MinimizeToTray;
        vm.CloseAction = CloseAction.Exit;
        vm.MinimizeToTray = !originalMinimize;

        bool result = vm.TryConfirmDiscardChanges();

        Assert.True(result);
        Assert.Equal(originalAction, vm.CloseAction);
        Assert.Equal(originalMinimize, vm.MinimizeToTray);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task HasUnsavedChanges_AfterSaveThenEdit_BecomesTrueAgain()
    {
        // Regression: snapshot must be re-captured on Save, otherwise a second round of
        // edits would still compare against the original-on-load snapshot and falsely
        // appear dirty (or clean) at the wrong time.
        SettingsViewModel vm = CreateVm();
        await vm.LoadAsync();
        vm.GridFontFamily = "Consolas";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasUnsavedChanges);

        vm.GridFontFamily = "Verdana";

        Assert.True(vm.HasUnsavedChanges);
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
