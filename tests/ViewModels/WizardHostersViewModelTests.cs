// <copyright file="WizardHostersViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.ViewModels;

/// <summary>
/// The File Hosters step's row construction: what <see cref="WizardHostersViewModel.LoadFileHostersAsync"/>
/// carries from each hoster's pipeline into its row. (The filter behaviors live in
/// <see cref="UploadWizardHosterFilterTests"/>, which predates this class.)
/// </summary>
public class WizardHostersViewModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WizardHostersViewModel _vm;

    public WizardHostersViewModelTests()
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

        _vm = new WizardHostersViewModel(
            new FileHosterLoginRepository(factory),
            Mock.Of<IDialogService>(),
            Mock.Of<IAppLogger>(),
            [],
            markSummaryDirty: () => { },
            fileHosterRegistry: new DefaultFileHosterRegistry([new CaptchaFreePipeline(), new DefaultingPipeline()]));
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LoadFileHostersAsync_CarriesEachPipelinesDownloadCaptchaIntoItsRow()
    {
        await _vm.LoadFileHostersAsync();

        // The one hoster whose (stub) pipeline answers: its verdict lands on the row.
        FileHosterSelectionViewModel catbox = _vm.FileHosters.Single(h => h.FileHosterName == "Catbox");
        Assert.Equal(DownloadCaptchaRequirement.NotRequired, catbox.DownloadCaptcha);

        // A registered pipeline that declares nothing surfaces the interface default — the row
        // shows the dash, which is a different claim from the blank below.
        FileHosterSelectionViewModel defaulting = _vm.FileHosters.Single(h => h.FileHosterName == "Pixeldrain");
        Assert.Equal(DownloadCaptchaRequirement.Unknown, defaulting.DownloadCaptcha);

        // A hoster the registry doesn't know keeps the no-claim blank, not a dash.
        FileHosterSelectionViewModel unregistered = _vm.FileHosters.Single(h => h.FileHosterName == "Rapidgator");
        Assert.Null(unregistered.DownloadCaptcha);
    }

    /// <summary>Stub Pixeldrain pipeline that declares nothing, so its row must surface the
    /// interface's Unknown default (the dash), distinct from an unregistered hoster's blank.</summary>
    private sealed class DefaultingPipeline : IFileHosterPipeline
    {
        public string Name => "Pixeldrain";

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, CSUploader.Lib.Net.ProxyChoice proxy, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Stub Catbox pipeline that declares a captcha-free download flow — implemented
    /// implicitly so the wizard must read it through <see cref="IFileHosterPipeline"/>.</summary>
    private sealed class CaptchaFreePipeline : IFileHosterPipeline
    {
        public string Name => "Catbox";

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

        public IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, CSUploader.Lib.Net.ProxyChoice proxy, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
