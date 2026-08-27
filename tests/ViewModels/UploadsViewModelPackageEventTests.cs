// <copyright file="UploadsViewModelPackageEventTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
/// Covers the Uploads tab's Post-routed event handlers — the exact paths the Avalonia head will drive
/// through a real dispatcher. Uses <see cref="InlineUiDispatcher"/> so every <c>_uiDispatcher.Post</c>
/// runs inline, then drives a REAL <see cref="PackageManager"/> + <see cref="UploadScheduler"/> (with a
/// pipeline that completes instantly) so the manager raises <c>PackageAdded</c> / <c>FileCompleted</c> /
/// <c>PackageCompleted</c> through production code rather than a synthetic re-raise. Also pins the two
/// distinct VisibleRows-affecting routes apart: FilterText edits raise <c>FilterInvalidated</c> without
/// touching VisibleRows, while an IsExpanded toggle mutates VisibleRows without raising it.
/// </summary>
// IAsyncLifetime so DisposeAsync can drain PackageManager's fire-and-forget persistence before the
// shared SqliteConnection closes (see tests/CLAUDE.md; same rationale as PackageManagerSoftRemoveTests).
public sealed class UploadsViewModelPackageEventTests : IAsyncLifetime
{
    private const string Hoster = "Rapidgator";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageRepository _packageRepo;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly FileHosterLoginRepository _loginRepo;
    private readonly string _tempDir;

    private readonly List<UploadScheduler> _schedulers = [];
    private readonly List<PackageManager> _managers = [];
    private readonly List<UploadsViewModel> _vms = [];

    public UploadsViewModelPackageEventTests()
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

        _packageRepo = new UploadPackageRepository(_factory);
        _fileRepo = new UploadPackageFileRepository(_factory);
        _loginRepo = new FileHosterLoginRepository(_factory);

        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-pkgevt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (UploadsViewModel vm in _vms)
        {
            vm.Dispose();
        }

        // Stop the source of new FileStateChanged events before draining, then let each manager's
        // in-flight persistence finish so no EF Core write races the connection dispose.
        foreach (UploadScheduler scheduler in _schedulers)
        {
            scheduler.Dispose();
        }

        foreach (PackageManager manager in _managers)
        {
            await manager.DrainPendingPersistenceAsync();
        }

        _connection.Dispose();
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task PackageAdded_BuildsVisibleRow()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack(new AppSettings());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("added", "a.bin"));

        // AddPackageOnlyAsync raises PackageAdded synchronously; the VM's Post-routed handler runs
        // inline, so the package row — and its file row, since packages default to expanded — land in
        // VisibleRows with no pumping.
        PackageFile file = package.Single();
        Assert.Contains(package, vm.VisibleRows);
        Assert.Contains(file, vm.VisibleRows);
        Assert.Contains(package, vm.Packages);
    }

    [Fact]
    public async Task FileCompleted_ImmediatelyMode_PrunesRowAndEmptyPackage()
    {
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        (PackageManager manager, UploadsViewModel vm) = BuildStack(settings, new CompletingPipeline(Hoster));

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("done", "a.bin"));
        PackageFile file = package.Single();
        Assert.Contains(file, vm.VisibleRows); // built by PackageAdded, before the upload runs

        manager.SchedulePackage(package);

        // The pipeline drives the file to Completed; the manager raises FileCompleted, whose VM handler
        // (Immediately mode) posts RemoveFileAndPruneEmptyPackage inline — the file row and its now-empty
        // package both vanish.
        await WaitForDrained(manager, () => !vm.VisibleRows.Contains(file) && !vm.VisibleRows.Contains(package));

        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.Packages);
    }

    [Fact]
    public async Task PackageCompleted_WhenPackageIsReadyMode_RemovesPackage()
    {
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.WhenPackageIsReady };
        (PackageManager manager, UploadsViewModel vm) = BuildStack(settings, new CompletingPipeline(Hoster));

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("ready", "a.bin"));
        PackageFile file = package.Single();
        Assert.Contains(package, vm.VisibleRows);

        manager.SchedulePackage(package);

        // WhenPackageIsReady: the per-file FileCompleted handler no-ops; only PackageCompleted (raised
        // once every file succeeded) removes the package via the inline Post.
        await WaitForDrained(manager, () => !vm.VisibleRows.Contains(package));

        Assert.DoesNotContain(package, vm.VisibleRows);
        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.Packages);
    }

    [Fact]
    public async Task FileCompleted_ImmediatelyMode_RowRevivedInTheDispatchGap_StaysListed()
    {
        // The dispatch-gap race. FileCompleted fires on the persistence thread and the handler
        // posts the prune — but the event carries the LIVE PackageFile, and before the dispatcher
        // gets to the prune the user can reset the row, requeuing it. The prune must decide from
        // the state at REMOVAL time and decline; deciding from event time removed the revived row
        // mid-retry and detached its new attempt. DeferredUiDispatcher is what lets the test stand
        // inside that gap.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher);

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("revived", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted(); // PackageAdded → the rows exist
        Assert.Contains(file, vm.VisibleRows);

        // SchedulePackage re-raises PackageAdded synchronously (the scheduler registration), so the
        // baseline is taken after it — the NEXT capture is the prune itself.
        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);
        Assert.Equal(FileState.Completed, file.State);

        // The revival, through the real path. Caps to zero first so the requeued file SITS in the
        // queue — the test pipeline completes uploads instantly, and a legitimate second completion
        // would re-post the prune and muddy what is being proven.
        settings.MaxConcurrentUploadJobs = 0;
        settings.MaxConcurrentCPUJobs = 0;
        manager.ResetPackage(file);
        await scheduler.DrainAsync();
        Assert.NotEqual(FileState.Completed, file.State);

        dispatcher.RunPosted(); // the stale prune finally gets its turn — and must decline

        Assert.Contains(file, vm.VisibleRows);
        Assert.Contains(package, vm.VisibleRows);
        Assert.Contains(file, package); // still owned — not detached, its retry not cancelled
    }

    [Fact]
    public async Task PackageCompleted_WhenPackageIsReadyMode_FileRevivedInTheDispatchGap_PackageStays()
    {
        // Package-level flavour of the same race: the whole package completed and its removal is
        // posted, then one file is reset in the gap. The package must stay on screen with its
        // requeued row.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.WhenPackageIsReady };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher);

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("revived-pkg", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);

        settings.MaxConcurrentUploadJobs = 0;
        settings.MaxConcurrentCPUJobs = 0;
        manager.ResetPackage(file);
        await scheduler.DrainAsync();

        dispatcher.RunPosted();

        Assert.Contains(package, vm.VisibleRows);
        Assert.Contains(package, vm.Packages);
        Assert.Contains(file, package);
    }

    [Fact]
    public async Task FileCompleted_ImmediatelyMode_ResetRequestedButNotYetApplied_PruneDeclines()
    {
        // The harder half of the dispatch-gap race. The tests above revive the file and WAIT for
        // the pump to apply it, so the prune's State recheck sees the revival. Here the pump is
        // deliberately held: the user's Reset is QUEUED and the state still reads Completed when
        // the prune runs. Only the veto the command set on the UI thread can save the row — and
        // the user's click came first, so it must.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(
            settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher, ConfirmingDialogService());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("queued-reset", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);
        Assert.Equal(FileState.Completed, file.State);

        using PumpBlock block = new(scheduler);

        // The user resets the visibly-completed row. The command vetoes on this thread and posts
        // the revival — which the held pump cannot apply.
        await vm.ResetFileCommand.ExecuteAsync(new List<object> { file });
        Assert.Equal(FileState.Completed, file.State); // the revival is queued, NOT applied

        dispatcher.RunPosted(); // the prune runs against a still-Completed row — and must decline

        Assert.Contains(file, vm.VisibleRows);
        Assert.Contains(package, vm.VisibleRows);
        Assert.Contains(file, package);

        block.Release();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task PackageCompleted_WhenPackageIsReadyMode_ResetRequestedButNotYetApplied_RemovalDeclines()
    {
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.WhenPackageIsReady };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(
            settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher, ConfirmingDialogService());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("queued-reset-pkg", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);

        using PumpBlock block = new(scheduler);
        await vm.ResetFileCommand.ExecuteAsync(new List<object> { file });

        dispatcher.RunPosted();

        Assert.Contains(package, vm.VisibleRows);
        Assert.Contains(package, vm.Packages);

        block.Release();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task FileCompleted_ImmediatelyMode_ReuploadRequestedButNotYetApplied_PruneDeclines()
    {
        // The re-upload flavour of the queued-revival race: ForceStartSelected is the OTHER command
        // that revives a completed row, and it must veto exactly like Reset does.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(
            settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher, ConfirmingDialogService());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("queued-reupload", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);

        using PumpBlock block = new(scheduler);
        await vm.ForceStartSelectedCommand.ExecuteAsync(new List<object> { file });

        dispatcher.RunPosted();

        Assert.Contains(file, vm.VisibleRows);
        Assert.Contains(file, package);

        block.Release();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task AutoRemove_SurvivesAFailedRevivalWrite()
    {
        // The veto lifts on FileRevived — the scheduler APPLYING the revival — not on the
        // persisted FileReopened. The distinction is this test: the revival's database write fails
        // (one transient error), so FileReopened never fires. A veto keyed on it would outlive the
        // revival, and when the re-upload later completes for real, its legitimate prune would be
        // declined — the row silently exempted from the user's auto-remove setting forever after.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        DeferredUiDispatcher dispatcher = new();
        ToggleableFaultingInterceptor fault = new();
        DbContextOptions<CSUploaderDbContext> faultableOptions = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(fault)
            .Options;
        (PackageManager manager, UploadsViewModel vm) = BuildStack(
            settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher, ConfirmingDialogService(),
            new UploadPackageFileRepository(new TestDbContextFactory(faultableOptions)));

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("flaky-write", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);

        // Reset while the prune is pending; the revival's write fails.
        settings.MaxConcurrentUploadJobs = 0;
        settings.MaxConcurrentCPUJobs = 0;
        using (PumpBlock block = new(scheduler))
        {
            await vm.ResetFileCommand.ExecuteAsync(new List<object> { file });
            fault.Faulting = true;
            block.Release();
        }

        await scheduler.DrainAsync();
        await WaitForDrained(manager, () => file.State != FileState.Completed);
        fault.Faulting = false;
        dispatcher.RunPosted(); // stale prune declines (state); the FileRevived lift consumes the veto

        // The re-run completes for real — and THIS completion must prune, failed write or not.
        settings.MaxConcurrentUploadJobs = 4;
        settings.MaxConcurrentCPUJobs = 4;
        int baseline2 = dispatcher.PostedCount;
        manager.StartPackage(file);
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline2 && file.State == FileState.Completed);

        dispatcher.RunPosted();

        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.VisibleRows);
    }

    [Fact]
    public async Task ResetConfirmedAfterTheRowWasAlreadyPruned_DoesNotResurrectAGhostPackage()
    {
        // The dialog leg of the same race. The reset command awaits its confirmation, the
        // dispatcher keeps pumping, and the prune legitimately removes the row mid-dialog — its
        // state really is Completed and no revival is queued yet, so nothing may stop it. The
        // damage came AFTER: confirming the reset re-registered the dead package with the
        // scheduler, whose PackageAdded resurrected an empty phantom row in the grid, while the
        // detached file was mutated but never schedulable again. A confirmed revival of a
        // removed row must simply do nothing.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(
            settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher, ConfirmingDialogService());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("ghost", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);

        // The prune runs first — the row and its now-empty package are legitimately gone.
        dispatcher.RunPosted();
        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.Packages);
        await scheduler.DrainAsync();
        Assert.Equal(0, scheduler.RegisteredPackageCount);
        FileState stateBefore = file.State;

        // The user's dialog closes now — the command acts on the stale selection.
        await vm.ResetFileCommand.ExecuteAsync(new List<object> { file });
        await scheduler.DrainAsync();
        await manager.DrainPendingPersistenceAsync();
        dispatcher.RunPosted(); // any resurrection would arrive as a posted PackageAdded

        Assert.Equal(0, scheduler.RegisteredPackageCount); // the dead package was NOT re-registered
        Assert.DoesNotContain(package, vm.Packages);       // no phantom row
        Assert.DoesNotContain(package, vm.VisibleRows);
        Assert.Equal(stateBefore, file.State);             // the detached file was left alone

        // Same for the package item itself.
        await vm.ResetFileCommand.ExecuteAsync(new List<object> { package });
        await scheduler.DrainAsync();
        dispatcher.RunPosted();
        Assert.Equal(0, scheduler.RegisteredPackageCount);
        Assert.DoesNotContain(package, vm.Packages);
    }

    [Fact]
    public async Task AutoRemove_WorksAgainAfterTheRevivalRunsItsCourse()
    {
        // The veto must EXPIRE. If it stuck to the file forever, the first reset would disable
        // auto-remove for that row for good — its next legitimate completion would sit on the
        // Uploads tab, silently exempt from the user's setting. FileRevived lifts it once the
        // scheduler applies the revival, and the FIFO dispatcher guarantees every prune posted
        // before the revival has already run (and declined) by then.
        AppSettings settings = new() { RemoveFinishedUploads = RemoveFinishedUploadsMode.Immediately };
        DeferredUiDispatcher dispatcher = new();
        (PackageManager manager, UploadsViewModel vm) = BuildStack(
            settings, out UploadScheduler scheduler, new CompletingPipeline(Hoster), dispatcher, ConfirmingDialogService());

        Package package = await manager.AddPackageOnlyAsync(MakeOptions("expiry", "a.bin"));
        PackageFile file = package.Single();
        dispatcher.RunPosted();

        manager.SchedulePackage(package);
        int baseline = dispatcher.PostedCount;
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline);

        // Round 1: reset while the prune is pending; the veto declines it.
        settings.MaxConcurrentUploadJobs = 0; // the applied reset must SIT queued, not re-complete
        settings.MaxConcurrentCPUJobs = 0;
        using (PumpBlock block = new(scheduler))
        {
            await vm.ResetFileCommand.ExecuteAsync(new List<object> { file });
            dispatcher.RunPosted();
            Assert.Contains(file, vm.VisibleRows);
            block.Release();
        }

        // The revival applies; FileRevived lifts the veto.
        await scheduler.DrainAsync();
        await WaitForDrained(manager, () => file.State != FileState.Completed);
        dispatcher.RunPosted();

        // Round 2: the re-run completes for real — and THIS completion must prune normally.
        settings.MaxConcurrentUploadJobs = 4;
        settings.MaxConcurrentCPUJobs = 4;
        int baseline2 = dispatcher.PostedCount;
        manager.StartPackage(file); // kicks FillAvailableSlots; the file is already queued
        await WaitForDrained(manager, () => dispatcher.PostedCount > baseline2 && file.State == FileState.Completed);

        dispatcher.RunPosted();

        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.DoesNotContain(package, vm.VisibleRows);
    }

    [Fact]
    public async Task IsExpandedToggle_AddsAndRemovesFileRows_WithoutRaisingFilterInvalidated()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack(new AppSettings());
        Package package = await manager.AddPackageOnlyAsync(MakeOptions("toggle", "a.bin"));
        PackageFile file = package.Single();

        // Package defaults to expanded, so the file row is present after PackageAdded.
        Assert.Contains(file, vm.VisibleRows);

        int filterInvalidated = 0;
        vm.FilterInvalidated += (_, _) => filterInvalidated++;

        // Collapse: Package_PropertyChanged posts RemovePackageFiles (inline) — the file row disappears.
        // This route deliberately raises NO FilterInvalidated (that was the dead RebuildVisibleRows path).
        package.IsExpanded = false;
        Assert.DoesNotContain(file, vm.VisibleRows);
        Assert.Contains(package, vm.VisibleRows);

        // Expand again: the file row reappears.
        package.IsExpanded = true;
        Assert.Contains(file, vm.VisibleRows);

        Assert.Equal(0, filterInvalidated);
    }

    [Fact]
    public async Task FilterTextChange_RaisesFilterInvalidated_ButLeavesVisibleRowsUntouched()
    {
        (PackageManager manager, UploadsViewModel vm) = BuildStack(new AppSettings());
        await manager.AddPackageOnlyAsync(MakeOptions("filter", "a.bin"));
        int before = vm.VisibleRows.Count;

        int filterInvalidated = 0;
        vm.FilterInvalidated += (_, _) => filterInvalidated++;

        // The OTHER route: editing FilterText raises FilterInvalidated (each head re-runs its own view
        // filter) but never mutates the VM's VisibleRows itself.
        vm.FilterText = "abc";

        Assert.Equal(1, filterInvalidated);
        Assert.Equal(before, vm.VisibleRows.Count);
    }

    private (PackageManager Manager, UploadsViewModel Vm) BuildStack(AppSettings settings, IFileHosterPipeline? pipeline = null)
        => BuildStack(settings, out _, pipeline);

    private (PackageManager Manager, UploadsViewModel Vm) BuildStack(
        AppSettings settings,
        out UploadScheduler scheduler,
        IFileHosterPipeline? pipeline = null,
        IUiDispatcher? dispatcher = null,
        IDialogService? dialogService = null,
        UploadPackageFileRepository? fileRepo = null)
    {
        DefaultFileHosterRegistry registry = new(pipeline is null ? [] : [pipeline]);
        scheduler = new(settings, BuildAttemptRunner(registry), Mock.Of<IAppLogger>(), new HashingService(), registry);
        _schedulers.Add(scheduler);

        PackageManager manager = new(settings, scheduler, _packageRepo, fileRepo ?? _fileRepo, _loginRepo, Mock.Of<IAppLogger>(), registry);
        _managers.Add(manager);

        UploadsViewModel vm = new(manager, settings, dialogService ?? Mock.Of<IDialogService>(), dispatcher ?? new InlineUiDispatcher(), Mock.Of<IClipboardService>());
        _vms.Add(vm);

        return (manager, vm);
    }

    /// <summary>
    /// A command interceptor whose faulting can be switched on mid-test: while on, every write
    /// touching the file table fails — the way one transient SQLite error would.
    /// </summary>
    private sealed class ToggleableFaultingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public volatile bool Faulting;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Faulting && command.CommandText.Contains("\"UploadPackageFile\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected transient fault");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>A dialog service that answers yes to every confirmation, so the reset/re-upload
    /// commands proceed past their prompts.</summary>
    private static IDialogService ConfirmingDialogService()
    {
        Mock<IDialogService> dialog = new();
        dialog.Setup(d => d.ShowOptOutConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(true);
        dialog.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(true);
        return dialog.Object;
    }

    /// <summary>
    /// Parks the scheduler's pump so a posted revival stays QUEUED — the exact window the veto in
    /// <c>UploadsViewModel</c> exists for. Release() (or Dispose, so a failed assertion can't wedge
    /// the fixture) lets the pump continue.
    /// </summary>
    private sealed class PumpBlock : IDisposable
    {
        private readonly ManualResetEventSlim _held = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public PumpBlock(UploadScheduler scheduler)
        {
            scheduler.PostFileMutation(() =>
            {
                _held.Set();
                _release.Wait();
            });

            Assert.True(_held.Wait(TimeSpan.FromSeconds(10)), "the pump never picked up the blocking action");
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _held.Dispose();
            _release.Dispose();
        }
    }

    private PackageOptions MakeOptions(string title, params string[] fileNames)
    {
        List<string> paths = [];
        foreach (string name in fileNames)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, [1]);
            paths.Add(path);
        }

        FileHosterClient hoster = new(Hoster, Protocol.Http);
        return new PackageOptions
        {
            Title = title,
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            SelectedFiles = paths,
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = Hoster, IsAnonymous = true } } },
        };
    }

    /// <summary>
    /// Polls <paramref name="condition"/> to a timeout, draining the manager's fire-and-forget
    /// persistence before each check — the FileCompleted/PackageCompleted events (and thus the VM's
    /// inline prune) fire from inside that persistence callback, so draining is what makes the outcome
    /// observable, and it leaves the collection settled for the assertion.
    /// </summary>
    private static async Task WaitForDrained(PackageManager manager, Func<bool> condition, int timeoutMs = 5000)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            await manager.DrainPendingPersistenceAsync();
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
            waited += 20;
        }

        await manager.DrainPendingPersistenceAsync();
        Assert.True(condition(), "condition was not met within the timeout");
    }

    private static AttemptRunner BuildAttemptRunner(IFileHosterRegistry registry)
    {
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(() => new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    /// <summary>
    /// Test pipeline whose upload completes immediately — drives a file straight to
    /// <see cref="FileState.Completed"/> through the real scheduler so the manager raises the
    /// FileCompleted/PackageCompleted events the VM's Post-routed handlers consume.
    /// </summary>
    private sealed class CompletingPipeline(string name) : IFileHosterPipeline
    {
        public string Name { get; } = name;

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "ok"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();
            yield return new TransferCompleted("https://done/" + ctx.FileName);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
