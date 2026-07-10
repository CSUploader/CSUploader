// <copyright file="UploadsViewModelFilterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
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
/// Filter behaviour of the Uploads tab. The ViewModel no longer owns an <c>ICollectionView</c>; it
/// exposes the raw <see cref="UploadsViewModel.VisibleRows"/> collection, a
/// <see cref="UploadsViewModel.MatchesFilter"/> predicate (each head assigns it to its native collection
/// view's filter), and a <see cref="UploadsViewModel.FilterInvalidated"/> event raised where the old code
/// called <c>FilteredRows.Refresh()</c>. These tests pin the predicate's rules and the invalidation
/// signal so the framework-free VM stays behaviour-compatible with the removed WPF-bound property.
/// </summary>
public class UploadsViewModelFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;

    public UploadsViewModelFilterTests()
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

        AppSettings settings = new();
        DefaultFileHosterRegistry registry = new([]);
        UploadPackageFileRepository fileRepo = new(factory);
        UploadPackageRepository packageRepo = new(factory);
        FileHosterLoginRepository loginRepo = new(factory);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _packageManager = new PackageManager(settings, _scheduler, packageRepo, fileRepo, loginRepo, Mock.Of<IAppLogger>(), registry);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MatchesFilter_EmptyOrWhitespaceFilter_MatchesEveryRow()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage("Holiday");
        PackageFile file = MakeFile(pkg, hoster, login, @"C:\d\clip.bin", "clip.bin");
        UploadsViewModel vm = CreateVm();

        // No filter → everything matches, including a row type the predicate doesn't recognise.
        vm.FilterText = string.Empty;
        Assert.True(vm.MatchesFilter(pkg));
        Assert.True(vm.MatchesFilter(file));
        Assert.True(vm.MatchesFilter(new object()));

        // Whitespace-only is treated as no filter too.
        vm.FilterText = "   ";
        Assert.True(vm.MatchesFilter(pkg));
        Assert.True(vm.MatchesFilter(file));
    }

    [Fact]
    public void MatchesFilter_NonEmptyFilter_MatchesPackageByNameAndFileByName_CaseInsensitiveAndTrimmed()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage("Holiday Photos");
        PackageFile file = MakeFile(pkg, hoster, login, @"C:\d\sunset.jpg", "sunset.jpg");
        UploadsViewModel vm = CreateVm();

        // Needle is trimmed and matched case-insensitively against the PACKAGE name.
        vm.FilterText = "  HOLIDAY  ";
        Assert.True(vm.MatchesFilter(pkg));
        Assert.False(vm.MatchesFilter(file));

        // Case-insensitive substring of the FILE name.
        vm.FilterText = "SUN";
        Assert.True(vm.MatchesFilter(file));
        Assert.False(vm.MatchesFilter(pkg));
    }

    [Fact]
    public void MatchesFilter_NonEmptyFilter_NoNameMatchOrForeignType_ReturnsFalse()
    {
        (Package pkg, FileHosterClient hoster, FileHosterLoginDto login) = MakePackage("Docs");
        PackageFile file = MakeFile(pkg, hoster, login, @"C:\d\report.pdf", "report.pdf");
        UploadsViewModel vm = CreateVm();

        vm.FilterText = "zzz";
        Assert.False(vm.MatchesFilter(pkg));
        Assert.False(vm.MatchesFilter(file));

        // A row type that is neither Package nor PackageFile never matches a non-empty filter.
        Assert.False(vm.MatchesFilter(new object()));
    }

    [Fact]
    public void FilterTextChange_RaisesFilterInvalidated_Once()
    {
        UploadsViewModel vm = CreateVm();
        int raised = 0;
        vm.FilterInvalidated += (_, _) => raised++;

        vm.FilterText = "abc";

        Assert.Equal(1, raised);
    }

    private UploadsViewModel CreateVm()
        => new(_packageManager, new AppSettings(), Mock.Of<IDialogService>(), new WpfUiDispatcher(), Mock.Of<IClipboardService>());

    private static (Package Package, FileHosterClient Hoster, FileHosterLoginDto Login) MakePackage(string name)
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        PackageOptions options = new()
        {
            Title = name,
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        };
        return (new Package(options), hoster, login);
    }

    private static PackageFile MakeFile(Package pkg, FileHosterClient hoster, FileHosterLoginDto login, string path, string name)
        => new(pkg, path, hoster, login) { Name = name };

    private static AttemptRunner BuildAttemptRunner()
    {
        DefaultFileHosterRegistry registry = new([]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
