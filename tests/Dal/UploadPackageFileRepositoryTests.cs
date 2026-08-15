// <copyright file="UploadPackageFileRepositoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Tests.Dal;

public class UploadPackageFileRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;
    private readonly UploadPackageFileRepository _fileRepo;
    private readonly UploadPackageRepository _packageRepo;

    public UploadPackageFileRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);
        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _fileRepo = new UploadPackageFileRepository(_factory);
        _packageRepo = new UploadPackageRepository(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HideAsync_FlipsIsHiddenFlagWithoutDeleting()
    {
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        int affected = await _fileRepo.HideAsync(new[] { fileId });

        Assert.Equal(1, affected);
        UploadPackageFileDto? reloaded = await _fileRepo.FindAsync(fileId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsHidden);
    }

    [Fact]
    public async Task HideAsync_OnlyHidesSpecifiedIds()
    {
        int packageId = await InsertPackageAsync("pkg");
        int hidden = await InsertFileAsync(packageId, "hidden.iso", FileState.Completed);
        int kept = await InsertFileAsync(packageId, "kept.iso", FileState.Completed);

        await _fileRepo.HideAsync(new[] { hidden });

        UploadPackageFileDto? hiddenDto = await _fileRepo.FindAsync(hidden);
        UploadPackageFileDto? keptDto = await _fileRepo.FindAsync(kept);
        Assert.True(hiddenDto!.IsHidden);
        Assert.False(keptDto!.IsHidden);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_ReturnsOnlyCompletedFiles()
    {
        // Failed / Cancelled rows must NOT show up in the Uploaded tab — that view is the
        // "successful uploads with URLs" history. Failed rows belong on the Uploads tab
        // where the user retries them.
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "completed.iso", FileState.Completed);
        await InsertFileAsync(packageId, "failed.iso", FileState.Failed);
        await InsertFileAsync(packageId, "cancelled.iso", FileState.Cancelled);
        await InsertFileAsync(packageId, "idle.iso", FileState.Idle);
        await InsertFileAsync(packageId, "uploading.iso", FileState.Uploading);
        await InsertFileAsync(packageId, "hashing.iso", FileState.Hashing);

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        string[] returnedNames = [.. rows.Select(r => r.File.FileName ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.Equal(new[] { "completed.iso" }, returnedNames);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_ExcludesHiddenRows()
    {
        int packageId = await InsertPackageAsync("pkg");
        int visible = await InsertFileAsync(packageId, "visible.iso", FileState.Completed);
        int hidden = await InsertFileAsync(packageId, "hidden.iso", FileState.Completed);

        await _fileRepo.HideAsync(new[] { hidden });

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        Assert.Single(rows);
        Assert.Equal("visible.iso", rows[0].File.FileName);
        Assert.Equal(visible, rows[0].File.Id);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_JoinsPackageNameFromOwningPackage()
    {
        int pkgA = await InsertPackageAsync("Movies");
        int pkgB = await InsertPackageAsync("Music");
        await InsertFileAsync(pkgA, "movie.mkv", FileState.Completed);
        await InsertFileAsync(pkgB, "song.mp3", FileState.Completed);

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        var byName = rows.ToDictionary(r => r.File.FileName ?? string.Empty, r => r.PackageName, StringComparer.Ordinal);
        Assert.Equal("Movies", byName["movie.mkv"]);
        Assert.Equal("Music", byName["song.mp3"]);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_DoesNotFilterByPackageIsCompleted()
    {
        // Even when the parent package is still IsCompleted=false, the file row should
        // appear as soon as it reaches a terminal state. This is the regression that
        // the per-file Uploaded-tab refresh depends on.
        int packageId = await InsertPackageAsync("pkg", isCompleted: false);
        await InsertFileAsync(packageId, "done.iso", FileState.Completed);

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        Assert.Single(rows);
        Assert.Equal("done.iso", rows[0].File.FileName);
    }

    [Fact]
    public async Task GetDoneFilesWithPackageNameAsync_DoesNotFilterByIsRemovedFromUploads()
    {
        // The Uploaded tab uses IsHidden as its own soft-delete flag; IsRemovedFromUploads
        // belongs to the Uploads tab and must not strip rows from the upload history.
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "done.iso", FileState.Completed);
        await _fileRepo.SoftRemoveFromUploadsAsync(new[] { fileId });

        (UploadPackageFileDto File, string PackageName)[] rows =
            await _fileRepo.GetDoneFilesWithPackageNameAsync();

        Assert.Single(rows);
        Assert.Equal("done.iso", rows[0].File.FileName);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_FlipsFlagWithoutDeleting()
    {
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        int affected = await _fileRepo.SoftRemoveFromUploadsAsync(new[] { fileId });

        Assert.Equal(1, affected);
        UploadPackageFileDto? reloaded = await _fileRepo.FindAsync(fileId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_OnlyTouchesSpecifiedIds()
    {
        int packageId = await InsertPackageAsync("pkg");
        int target = await InsertFileAsync(packageId, "target.iso", FileState.Completed);
        int other = await InsertFileAsync(packageId, "other.iso", FileState.Completed);

        await _fileRepo.SoftRemoveFromUploadsAsync(new[] { target });

        UploadPackageFileDto? targetDto = await _fileRepo.FindAsync(target);
        UploadPackageFileDto? otherDto = await _fileRepo.FindAsync(other);
        Assert.True(targetDto!.IsRemovedFromUploads);
        Assert.False(otherDto!.IsRemovedFromUploads);
    }

    [Fact]
    public async Task SoftRemoveFromUploadsAsync_LeavesIsHiddenAlone()
    {
        // IsHidden (Uploaded tab) and IsRemovedFromUploads (Uploads tab) are independent
        // — flipping one must not flip the other.
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        await _fileRepo.SoftRemoveFromUploadsAsync(new[] { fileId });

        UploadPackageFileDto? reloaded = await _fileRepo.FindAsync(fileId);
        Assert.False(reloaded!.IsHidden);
    }

    [Fact]
    public async Task UpdateStateAsync_WithStartedDateTime_OverwritesStartDateTime()
    {
        // The real upload-start time only becomes known at the terminal write; passing it
        // overwrites the add-time captured at insert so the History "Started at" is truthful.
        var addTime = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Local);
        var realStart = new DateTime(2025, 1, 1, 9, 30, 0, DateTimeKind.Local);
        var finished = new DateTime(2025, 1, 1, 9, 45, 0, DateTimeKind.Local);
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Uploading, startDateTime: addTime);

        await _fileRepo.UpdateStateAsync(fileId, (int)FileState.Completed, null, "https://x/a.html", finishedDateTime: finished, startedDateTime: realStart);

        UploadPackageFileDto? reloaded = await _fileRepo.FindAsync(fileId);
        Assert.NotNull(reloaded);
        Assert.Equal(realStart, reloaded!.StartDateTime);
        Assert.Equal(finished, reloaded.FinishedDateTime);
    }

    [Fact]
    public async Task UpdateStateAsync_WithNullStartedDateTime_PreservesInsertedStartDateTime()
    {
        // For Failed/Cancelled the real start may be null; the coalesce must keep the
        // existing add-time rather than wiping it to default(DateTime).
        var addTime = new DateTime(2025, 2, 2, 10, 0, 0, DateTimeKind.Local);
        var finished = new DateTime(2025, 2, 2, 10, 5, 0, DateTimeKind.Local);
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "b.iso", FileState.Uploading, startDateTime: addTime);

        await _fileRepo.UpdateStateAsync(fileId, (int)FileState.Failed, "boom", null, finishedDateTime: finished, startedDateTime: null);

        UploadPackageFileDto? reloaded = await _fileRepo.FindAsync(fileId);
        Assert.NotNull(reloaded);
        Assert.Equal(addTime, reloaded!.StartDateTime);
    }

    [Fact]
    public async Task UpdateQueueOrderAsync_RewritesOrdersForMultipleFiles()
    {
        int p = await InsertPackageAsync("p");
        int a = await InsertFileAsync(p, "a", queueOrder: 1);
        int b = await InsertFileAsync(p, "b", queueOrder: 2);

        await _fileRepo.UpdateQueueOrderAsync(new Dictionary<int, int> { [a] = 2, [b] = 1 });

        Assert.Equal(2, (await _fileRepo.FindAsync(a))!.QueueOrder);
        Assert.Equal(1, (await _fileRepo.FindAsync(b))!.QueueOrder);
    }

    [Fact]
    public async Task PersistTransitionAsync_CommitsStateHashAndPackageFlagTogether()
    {
        // The full terminal shape in one call: the file completed, its hash became valid on the
        // way, and it was the package's last running file.
        var addTime = new DateTime(2026, 3, 3, 8, 0, 0, DateTimeKind.Local);
        var realStart = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Local);
        var finished = new DateTime(2026, 3, 3, 9, 30, 0, DateTimeKind.Local);
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Uploading, startDateTime: addTime);

        FileTransitionResult result = await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.Completed,
            FileUrl = "https://x/a.html",
            FinishedDateTime = finished,
            StartedDateTime = realStart,
            HashToStore = "cafebabe",
            PackageIdNowCompleted = packageId,
        });

        Assert.True(result.FileRowExisted);
        Assert.True(result.PackageCompleted);

        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.Equal(FileState.Completed, file!.State);
        Assert.Equal("https://x/a.html", file.FileUrl);
        Assert.Equal(finished, file.FinishedDateTime);
        Assert.Equal(realStart, file.StartDateTime);
        Assert.Equal("cafebabe", file.FileHash);
        Assert.True(file.IsHashingComplete);

        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.True(package!.IsCompleted);
    }

    [Fact]
    public async Task PersistTransitionAsync_ResetShape_DiscardsTheHashAndReopensThePackage()
    {
        // The reset shape: back to HashQueued, hash thrown away, error cleared, and the package —
        // finished until this moment — is running again. Also pins the date rules for a
        // non-terminal write: no finish stamp, and the insert-time start is left alone.
        var addTime = new DateTime(2026, 4, 4, 10, 0, 0, DateTimeKind.Local);
        int packageId = await InsertPackageAsync("pkg", isCompleted: true);
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed, startDateTime: addTime);
        await _fileRepo.UpdateHashAsync(fileId, "deadbeef");

        // A real error on the row, so the "error cleared" assertion below cannot pass vacuously —
        // the non-terminal branch dropping its Error write is precisely the mutation it guards.
        await _fileRepo.UpdateStateAsync(fileId, (int)FileState.Completed, "old failure", null);

        await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.HashQueued,
            DiscardHash = true,
            PackageIdNoLongerCompleted = packageId,
        });

        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.Equal(FileState.HashQueued, file!.State);
        Assert.True(string.IsNullOrEmpty(file.FileHash));
        Assert.False(file.IsHashingComplete);
        Assert.True(string.IsNullOrEmpty(file.Error));
        Assert.Equal(addTime, file.StartDateTime);

        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.False(package!.IsCompleted);
    }

    [Fact]
    public async Task PersistTransitionAsync_DoesNotMarkThePackageComplete_WhileTheDbHoldsANonTerminalSibling()
    {
        // The caller believes its file was the package's last running one, but the caller only
        // knows its own memory. If a SIBLING's transition failed and rolled back earlier in the
        // chain, that sibling's row is still non-terminal — and stamping the package complete
        // around it would hand the export a "finished" package that is still missing work. The
        // rows decide: the request is declined and the caller told so.
        int packageId = await InsertPackageAsync("pkg");
        await InsertFileAsync(packageId, "sibling.iso", FileState.Uploading); // the failed write's leftovers
        int fileId = await InsertFileAsync(packageId, "b.iso", FileState.Uploading);

        FileTransitionResult result = await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.Completed,
            FinishedDateTime = DateTime.Now,
            PackageIdNowCompleted = packageId,
        });

        Assert.True(result.FileRowExisted);
        Assert.False(result.PackageCompleted);
        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.False(package!.IsCompleted);
    }

    [Fact]
    public async Task PersistTransitionAsync_IgnoresSoftRemovedSiblings_WhenDecidingPackageCompletion()
    {
        // A file removed from the Uploads tab mid-upload keeps its old running state in its row
        // forever — its in-memory counterpart left the package, so nothing will ever finish it.
        // Counting it would hold the package open for good; completion must look only at the rows
        // still listed.
        int packageId = await InsertPackageAsync("pkg");
        int removedId = await InsertFileAsync(packageId, "removed.iso", FileState.Uploading);
        await _fileRepo.SoftRemoveFromUploadsAsync(new[] { removedId });
        int fileId = await InsertFileAsync(packageId, "b.iso", FileState.Uploading);

        FileTransitionResult result = await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.Completed,
            FinishedDateTime = DateTime.Now,
            PackageIdNowCompleted = packageId,
        });

        Assert.True(result.PackageCompleted);
        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.True(package!.IsCompleted);
    }

    [Fact]
    public async Task PersistTransitionAsync_WhenTheFileRowIsGone_WritesNothingAndSaysSo()
    {
        // History cleanup can delete the row between the transition and the chained write reaching
        // it. The transition then has nothing to say about the database: no other statement runs
        // (a reopen would otherwise flip a package flag on behalf of a file that no longer exists)
        // and the caller is told nothing landed, so it announces nothing.
        int packageId = await InsertPackageAsync("pkg", isCompleted: true);
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);
        await _fileRepo.DeleteAsync(fileId);

        FileTransitionResult result = await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.HashQueued,
            DiscardHash = true,
            PackageIdNoLongerCompleted = packageId,
        });

        Assert.False(result.FileRowExisted);
        Assert.False(result.PackageCompleted);
        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.True(package!.IsCompleted); // the reopen flag was NOT applied for a ghost file
    }

    [Fact]
    public async Task PersistTransitionAsync_StoresAHashOnANonTerminalTransition()
    {
        // The everyday hash write: a hash-before-upload hoster finishes hashing and the file moves
        // Hashing → UploadQueued. Non-terminal, so no dates — the hash must land anyway. This is
        // the ONLY route a computed hash reaches the database (UpdateHashAsync has no production
        // caller left), so pairing hash coverage exclusively with terminal writes would leave the
        // routine path free to regress unseen.
        var addTime = new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Local);
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Hashing, startDateTime: addTime);

        await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.UploadQueued,
            HashToStore = "cafebabe",
        });

        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.Equal(FileState.UploadQueued, file!.State);
        Assert.Equal("cafebabe", file.FileHash);
        Assert.True(file.IsHashingComplete);

        // No finish stamp was written — the column keeps its never-set default (non-nullable, so
        // "untouched" reads back as DateTime.MinValue rather than null).
        Assert.Equal(default, file.FinishedDateTime);
        Assert.Equal(addTime, file.StartDateTime);
    }

    [Fact]
    public async Task PersistTransitionAsync_TerminalWithNullStartedDateTime_KeepsTheInsertTimeStart()
    {
        // A file cancelled or failed while still queued goes terminal with StartedDate null. The
        // coalesce must keep the add-time captured at insert rather than wiping it to
        // default(DateTime). The same rule is pinned for UpdateStateAsync above, but that method
        // no longer has a production caller — THIS copy is the live one, so it needs its own guard.
        var addTime = new DateTime(2026, 6, 6, 10, 0, 0, DateTimeKind.Local);
        var finished = new DateTime(2026, 6, 6, 10, 5, 0, DateTimeKind.Local);
        int packageId = await InsertPackageAsync("pkg");
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.UploadQueued, startDateTime: addTime);

        await _fileRepo.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.Cancelled,
            FinishedDateTime = finished,
            StartedDateTime = null,
        });

        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.Equal(addTime, file!.StartDateTime);
        Assert.Equal(finished, file.FinishedDateTime);
    }

    [Fact]
    public async Task PersistTransitionAsync_WhenALaterStatementFails_RollsBackTheWholeTransition()
    {
        // The reason this method exists. The package-flag statement is the LAST one in the
        // transaction; failing it must take the state and hash statements — which had already
        // executed — down with it. Issued as separate autocommitted statements (the old shape),
        // the state and hash land and this test fails.
        int packageId = await InsertPackageAsync("pkg", isCompleted: true);
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);
        await _fileRepo.UpdateHashAsync(fileId, "deadbeef");

        UploadPackageFileRepository faulting = new(
            FaultingFactory(failCommandsTouching: "\"UploadPackage\""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => faulting.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.HashQueued,
            DiscardHash = true,
            PackageIdNoLongerCompleted = packageId,
        }));

        // Nothing landed: the row still shows the pre-transition shape, hash included.
        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.Equal(FileState.Completed, file!.State);
        Assert.Equal("deadbeef", file.FileHash);
        Assert.True(file.IsHashingComplete);

        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.True(package!.IsCompleted);
    }

    [Fact]
    public async Task PersistTransitionAsync_WhenAnEarlierStatementFails_TheLaterOnesNeverLand()
    {
        // The complement of the rollback test above, faulting the FIRST-executed table instead of
        // the last. Today it holds trivially (nothing ran before the fault); its value is under a
        // future statement reorder, where it becomes the proof that the transaction still exists —
        // whichever order the statements run in, one of the pair is always faulting a non-first
        // statement.
        int packageId = await InsertPackageAsync("pkg", isCompleted: true);
        int fileId = await InsertFileAsync(packageId, "a.iso", FileState.Completed);

        UploadPackageFileRepository faulting = new(
            FaultingFactory(failCommandsTouching: "\"UploadPackageFile\""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => faulting.PersistTransitionAsync(new FileTransitionWrite
        {
            FileId = fileId,
            State = (int)FileState.HashQueued,
            DiscardHash = true,
            PackageIdNoLongerCompleted = packageId,
        }));

        UploadPackageDto? package = await _packageRepo.FindAsync(packageId);
        Assert.True(package!.IsCompleted); // the reopen flag, a LATER statement, never landed
        UploadPackageFileDto? file = await _fileRepo.FindAsync(fileId);
        Assert.Equal(FileState.Completed, file!.State);
    }

    /// <summary>
    /// A context factory whose commands fail when their SQL touches the given quoted table name —
    /// fault injection for proving a multi-statement write is genuinely transactional. Matching
    /// includes the identifier quotes because one table name here is a prefix of another
    /// ("UploadPackage" / "UploadPackageFile").
    /// </summary>
    private IDbContextFactory<CSUploaderDbContext> FaultingFactory(string failCommandsTouching)
    {
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new FaultingCommandInterceptor(failCommandsTouching))
            .Options;
        return new TestDbContextFactory(options);
    }

    private sealed class FaultingCommandInterceptor(string failCommandsTouching)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(failCommandsTouching, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"injected fault: statement touches {failCommandsTouching}");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<int> InsertPackageAsync(string name, bool isCompleted = false)
    {
        UploadPackageDto pkg = new()
        {
            Name = name,
            CreatedDateTime = DateTime.Now,
            IsCompleted = isCompleted,
        };
        await _packageRepo.InsertAsync(pkg);
        return pkg.Id;
    }

    private async Task<int> InsertFileAsync(int packageId, string fileName, FileState state = FileState.Idle, int queueOrder = 0, DateTime? startDateTime = null)
    {
        UploadPackageFileDto file = new()
        {
            FileName = fileName,
            FileDirectory = "C:\\test",
            FileSize = 1024,
            FileHoster = "Rapidgator",
            FileHosterName = "Rapidgator",
            State = state,
            PackageId = packageId,
            QueueOrder = queueOrder,
            StartDateTime = startDateTime ?? default,
        };
        await _fileRepo.InsertAsync(file);
        return file.Id;
    }

    private class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
