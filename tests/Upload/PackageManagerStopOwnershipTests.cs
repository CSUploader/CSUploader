// <copyright file="PackageManagerStopOwnershipTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Upload;

/// <summary>
/// One thread owns a file's <see cref="PackageFile.Cts"/> and <see cref="PackageFile.State"/>: the
/// scheduler's pump. The per-row Stop, Reset and Remove must hand their work to it rather than
/// reaching in from the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <c>StopFile</c> used to run straight from the UI thread, and it read <c>file.Cts</c> three
/// separate times — once for <c>Cancel</c>, once for <c>Dispose</c>, once to clear it. The pump
/// could install a brand-new source in between:
/// </para>
/// <list type="number">
/// <item>the row is UploadQueued with no source, so the UI thread's <c>Cancel</c> finds null;</item>
/// <item>the pump launches the file and publishes attempt B's source;</item>
/// <item>the UI thread's next read finds B and disposes it — without ever cancelling it.</item>
/// </list>
/// <para>
/// That left an upload running with nothing left to stop it, behind a row marked Cancelled, holding
/// a token whose source was disposed but never cancelled — the one state in which the pipelines'
/// own cancellation registrations throw <see cref="ObjectDisposedException"/>. No amount of
/// per-field care fixes an interleaving; the operation has to happen where the launches happen.
/// </para>
/// </remarks>
public sealed class PackageManagerStopOwnershipTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly UploadScheduler _scheduler;
    private readonly PackageManager _packageManager;
    private readonly string _tempDir;

    public PackageManagerStopOwnershipTests()
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

        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-own-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Both caps at zero: these tests are about WHERE the Cts/State mutation happens, not about
        // scheduling. Left at the defaults, the slot-fill that Reset posts behind its mutation
        // immediately relaunches the file it just re-queued and installs a fresh source, so the
        // assertions would be reading the next attempt instead of the outcome of the reset.
        AppSettings settings = new()
        {
            MaxConcurrentUploadJobs = 0,
            MaxConcurrentCPUJobs = 0,
        };
        DefaultFileHosterRegistry registry = new([]);
        _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _packageManager = new PackageManager(
            settings, _scheduler, new UploadPackageRepository(factory), new UploadPackageFileRepository(factory),
            new FileHosterLoginRepository(factory), Mock.Of<IAppLogger>(), registry); // starts the pump
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _connection.Dispose();

        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StopPackage_DoesNothingOnTheCallingThread_AndTakesEffectOnThePump()
    {
        (Package package, PackageFile file) = MakeRunningFile();
        CancellationTokenSource attempt = file.Cts!;

        using PumpBlock block = BlockPump();

        _packageManager.StopPackage(file);

        // The pump is held, so if Stop still reached in from this thread the row would already be
        // Cancelled with its source torn down. Nothing may have moved yet.
        Assert.Same(attempt, file.Cts);
        Assert.Equal(FileState.Uploading, file.State);
        Assert.False(attempt.IsCancellationRequested);

        block.Release();
        await _scheduler.DrainAsync();

        Assert.True(attempt.IsCancellationRequested); // cancelled BEFORE it was disposed
        Assert.Null(file.Cts);
        Assert.Equal(FileState.Cancelled, file.State);
        Assert.False(file.ForceStart);
        Assert.NotEmpty(package);
    }

    [Fact]
    public async Task ResetPackage_DoesNothingOnTheCallingThread_AndTakesEffectOnThePump()
    {
        (Package _, PackageFile file) = MakeRunningFile();
        file.FileHash = "abc";
        file.IsHashingComplete = true;
        CancellationTokenSource attempt = file.Cts!;

        using PumpBlock block = BlockPump();

        _packageManager.ResetPackage(file);

        Assert.Same(attempt, file.Cts);
        Assert.Equal(FileState.Uploading, file.State);
        Assert.False(attempt.IsCancellationRequested);

        block.Release();
        await _scheduler.DrainAsync();

        Assert.True(attempt.IsCancellationRequested);
        Assert.Null(file.Cts);
        Assert.Equal(FileState.HashQueued, file.State); // reset always restarts from the hash
        Assert.Null(file.FileHash);
        Assert.False(file.IsHashingComplete);
    }

    [Fact]
    public async Task RemovePackage_File_DoesNothingOnTheCallingThread_AndTakesEffectOnThePump()
    {
        (Package package, PackageFile file) = MakeRunningFile();
        CancellationTokenSource attempt = file.Cts!;

        using PumpBlock block = BlockPump();

        _packageManager.RemovePackage(file);

        // The row leaves the package immediately — that part is the caller's, and the grid depends
        // on it — but the source is the scheduler's to tear down.
        Assert.Empty(package);
        Assert.Same(attempt, file.Cts);
        Assert.False(attempt.IsCancellationRequested);

        block.Release();
        await _scheduler.DrainAsync();

        Assert.True(attempt.IsCancellationRequested);
        Assert.Null(file.Cts);
        Assert.False(file.ForceStart);
    }

    [Fact]
    public async Task RemovePackage_Package_CancelsItsFiles_EvenThoughTheListIsEmptiedFirst()
    {
        // The scheduler's removal runs on the pump, but the caller empties the package the instant
        // it returns. Reading the package from the pump would therefore find nothing left to cancel
        // and every running upload would carry on unstoppable — so the file list is snapshotted on
        // the calling thread and handed to the pump.
        (Package package, PackageFile file) = MakeRunningFile();
        CancellationTokenSource attempt = file.Cts!;

        _packageManager.RemovePackage(package);
        Assert.Empty(package); // emptied synchronously, before the pump gets anywhere near it

        await _scheduler.DrainAsync();

        Assert.True(attempt.IsCancellationRequested);
        Assert.Null(file.Cts);
        Assert.Equal(0, _scheduler.RegisteredPackageCount);
    }

    [Fact]
    public async Task StartPackage_DoesNothingOnTheCallingThread_AndTakesEffectOnThePump()
    {
        // Start writes file.State too (ForceQueueIfStartable), so it belongs on the pump for the
        // same reason Stop and Reset do.
        (Package _, PackageFile file) = MakeRunningFile();
        file.State = FileState.Failed;
        file.Error = "boom";
        file.Cts!.Dispose();
        file.Cts = null;

        using PumpBlock block = BlockPump();

        _packageManager.StartPackage(file);

        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("boom", file.Error);

        block.Release();
        await _scheduler.DrainAsync();

        Assert.Equal(FileState.HashQueued, file.State);
        Assert.Null(file.Error);
    }

    [Theory]
    [InlineData(StopKind.Stop)]
    [InlineData(StopKind.Reset)]
    [InlineData(StopKind.Remove)]
    public async Task Cancelling_RetiresTheAttempt_SoItsPendingCompletionCannotActOnTheRow(StopKind kind)
    {
        // Cancelling a worker does not recall a completion it has already posted. If the hash
        // SUCCEEDED just before the stop was processed, its callback still arrives — and unless the
        // attempt has been retired, the scheduler treats it as current: Stop's row gets moved to
        // UploadQueued and uploads anyway, and Reset's freshly-queued row gets painted back to
        // Cancelled. UploadSchedulerCancellationTests proves the end-to-end consequence for
        // StopAll with a real worker; this pins that each per-row entry point retires the attempt.
        (Package package, PackageFile file) = MakeRunningFile();
        int attempt = file.AttemptGeneration;

        switch (kind)
        {
            case StopKind.Stop: _packageManager.StopPackage(file); break;
            case StopKind.Reset: _packageManager.ResetPackage(file); break;
            default: _packageManager.RemovePackage(file); break;
        }

        await _scheduler.DrainAsync();

        Assert.NotEqual(attempt, file.AttemptGeneration);
        Assert.NotNull(package);
    }

    public enum StopKind
    {
        Stop,
        Reset,
        Remove,
    }

    [Fact]
    public void PackageRemove_LeavesCancellationToTheScheduler()
    {
        // Package is a container, not a lifetime owner. It used to cancel and dispose the source
        // itself, which meant doing it from whichever thread called Remove — the UI one.
        (Package package, PackageFile file) = MakeRunningFile();
        CancellationTokenSource attempt = file.Cts!;

        package.Remove(file);

        Assert.Empty(package);
        Assert.Same(attempt, file.Cts);
        Assert.False(attempt.IsCancellationRequested);

        attempt.Dispose(); // nothing else owns it in this test
    }

    /// <summary>
    /// Occupies the scheduler's single consumer so that anything posted while it is held is
    /// provably still unprocessed — which is how these tests tell "posted to the pump" apart from
    /// "done on the calling thread" without any timing guesswork.
    /// </summary>
    private PumpBlock BlockPump()
    {
        PumpBlock block = new();
        _scheduler.PostFileMutation(block.Occupy);
        block.WaitUntilPumpIsHeld();
        return block;
    }

    /// <summary>
    /// A package holding one file dressed as a live attempt: Uploading, force-started, with its own
    /// cancellation source — the exact shape every one of these operations has to take apart.
    /// </summary>
    private (Package Package, PackageFile File) MakeRunningFile()
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        Package package = new(new PackageOptions
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        });

        string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [1]);
        PackageFile file = new(package, path, hoster, login);
        package.AddPackageFiles([file]);

        // Registered the way CreatePackageAsync would — the revival paths (Start/Reset/ForceStart)
        // decline files whose package the manager doesn't own, so a raw unregistered package would
        // sidestep the very methods these tests drive.
        _packageManager.Packages.Add(package);

        file.State = FileState.Uploading;
        file.ForceStart = true;
        file.Cts = new CancellationTokenSource();
        return (package, file);
    }

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

    /// <summary>
    /// Parks the pump thread until released. Disposal releases too, so a failing assertion cannot
    /// leave the scheduler wedged and take the rest of the class down with it.
    /// </summary>
    private sealed class PumpBlock : IDisposable
    {
        private readonly ManualResetEventSlim _held = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public void Occupy()
        {
            _held.Set();
            _release.Wait();
        }

        public void WaitUntilPumpIsHeld() => Assert.True(_held.Wait(TimeSpan.FromSeconds(10)), "the pump never picked up the blocking action");

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _held.Dispose();
            _release.Dispose();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
