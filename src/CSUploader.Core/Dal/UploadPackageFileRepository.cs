// <copyright file="UploadPackageFileRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class UploadPackageFileRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<UploadPackageFileDbm, UploadPackageFileDto>(dbFactory)
{
    public async Task<UploadPackageFileDto?> FindAsync(int fileId, CancellationToken cancellationToken = default)
    {
        UploadPackageFileDbm? entity = await FindFirstOrDefaultAsync(fu => fu.Id == fileId, cancellationToken);
        return entity is not null ? MapToDto(entity) : null;
    }

    public Task<int> DeleteAsync(int fileId, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => fu.Id == fileId, cancellationToken);

    public Task<int> DeleteAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(fu => fileIds.Contains(fu.Id), cancellationToken);

    /// <summary>
    /// Soft-deletes files by flipping <see cref="UploadPackageFileDbm.IsHidden"/> to true.
    /// The rows remain in the database so history is preserved.
    /// </summary>
    public async Task<int> HideAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        return await db.Set<UploadPackageFileDbm>()
            .Where(f => fileIds.Contains(f.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsHidden, true), cancellationToken);
    }

    /// <summary>
    /// Returns every file that has successfully uploaded together with its owning package
    /// name, regardless of whether the package itself has been marked <c>IsCompleted</c>.
    /// Used by the Uploaded tab so files appear as soon as they finish, not only when the
    /// whole package finishes. Failed/Cancelled rows are intentionally excluded — those have
    /// no URL and the user re-tries them from the Uploads tab; the Uploaded tab is the
    /// "successful uploads with URLs" history.
    /// </summary>
    public async Task<(UploadPackageFileDto File, string PackageName)[]> GetDoneFilesWithPackageNameAsync(CancellationToken ct = default)
    {
        int completed = (int)FileState.Completed;

        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        var rows = await db.Set<UploadPackageFileDbm>()
            .Where(f => f.State == completed && !f.IsHidden)
            .Join(
                db.Set<UploadPackageDbm>(),
                f => f.PackageId,
                p => p.Id,
                (f, p) => new { File = f, PackageName = p.Name })
            .ToArrayAsync(ct);

        return [.. rows.Select(r => (MapToDto(r.File), r.PackageName ?? string.Empty))];
    }

    public async Task UpdateStateAsync(int fileId, int state, string? error, string? fileUrl, DateTime? finishedDateTime = null, DateTime? startedDateTime = null, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        if (finishedDateTime is { } finished)
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.State, state)
                    .SetProperty(f => f.Error, error ?? string.Empty)
                    .SetProperty(f => f.FileUrl, fileUrl ?? string.Empty)
                    .SetProperty(f => f.FinishedDateTime, finished)
                    .SetProperty(f => f.StartDateTime, f => startedDateTime ?? f.StartDateTime), ct);
        }
        else
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.State, state)
                    .SetProperty(f => f.Error, error ?? string.Empty)
                    .SetProperty(f => f.FileUrl, fileUrl ?? string.Empty), ct);
        }
    }

    /// <summary>
    /// Persists the hex-encoded hash + the IsHashingComplete flag once a hoster's pre-upload
    /// hashing pass finishes successfully. Separate from <see cref="UpdateStateAsync"/> so we
    /// don't widen its signature for a column only some hosters touch.
    /// </summary>
    public async Task UpdateHashAsync(int fileId, string fileHash, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageFileDbm>()
            .Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.FileHash, fileHash)
                .SetProperty(f => f.IsHashingComplete, true), ct);
    }

    /// <summary>
    /// Commits everything one file-state transition implies — the state row, a hash stored or
    /// discarded with it, a package-completed flag flipped by it — as ONE transaction, and reports
    /// what actually landed.
    /// </summary>
    /// <remarks>
    /// One transaction because the pieces only make sense together. The state update landing
    /// without its hash clear is a reset that comes back hashed after a restart; a reopened file
    /// whose package flag write failed leaves queued rows inside a package the export still calls
    /// finished. A mid-write failure now rolls the whole transition back, leaving the previous
    /// consistent shape for the next write in the chain to replace. The date handling mirrors
    /// <see cref="UpdateStateAsync"/>: the finish stamp only exists for terminal transitions, and
    /// the start time never overwrites the insert-time default with null.
    /// </remarks>
    public async Task<FileTransitionResult> PersistTransitionAsync(FileTransitionWrite write, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        int fileRows;
        if (write.FinishedDateTime is { } finished)
        {
            fileRows = await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == write.FileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.State, write.State)
                    .SetProperty(f => f.Error, write.Error ?? string.Empty)
                    .SetProperty(f => f.FileUrl, write.FileUrl ?? string.Empty)
                    .SetProperty(f => f.FinishedDateTime, finished)
                    .SetProperty(f => f.StartDateTime, f => write.StartedDateTime ?? f.StartDateTime), ct);
        }
        else
        {
            fileRows = await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == write.FileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.State, write.State)
                    // Written on non-terminal transitions too — a retry's requeue is exactly where
                    // the previous attempt's error must come OFF the row, or a restart restores it.
                    .SetProperty(f => f.Error, write.Error ?? string.Empty)
                    .SetProperty(f => f.FileUrl, write.FileUrl ?? string.Empty), ct);
        }

        if (fileRows == 0)
        {
            // The row is gone — history cleanup deleted it between the transition and this write.
            // This transition has nothing to say about the database any more: write nothing (the
            // dispose rolls the empty transaction back) and let the caller announce nothing.
            return new FileTransitionResult(FileRowExisted: false, PackageCompleted: false);
        }

        if (write.HashToStore is not null)
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == write.FileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.FileHash, write.HashToStore)
                    .SetProperty(f => f.IsHashingComplete, true), ct);
        }
        else if (write.DiscardHash)
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == write.FileId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.FileHash, (string?)null)
                    .SetProperty(f => f.IsHashingComplete, false), ct);
        }

        if (write.PackageIdNoLongerCompleted is int reopenedId)
        {
            await db.Set<UploadPackageDbm>()
                .Where(p => p.Id == reopenedId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCompleted, false), ct);
        }

        bool packageCompleted = false;
        if (write.PackageIdNowCompleted is int completedId)
        {
            // The caller believes this was the package's last running file, but it only knows its
            // own memory — an earlier transition in the chain may have failed and rolled back,
            // leaving that file's ROW non-terminal. The rows are the record, so the rows decide:
            // the flag is only set when no still-listed file of the package is non-terminal.
            // Runs inside this transaction, after this file's own state update, so it sees it.
            // Rows soft-removed from Uploads don't count — the user took them out of the queue,
            // and their in-memory counterparts left the package, so they must not hold the
            // package open forever (a file removed mid-upload keeps its old running state).
            int completed = (int)FileState.Completed;
            int failed = (int)FileState.Failed;
            int cancelled = (int)FileState.Cancelled;
            packageCompleted = await db.Set<UploadPackageDbm>()
                .Where(p => p.Id == completedId && !db.Set<UploadPackageFileDbm>().Any(f =>
                    f.PackageId == completedId
                    && !f.IsRemovedFromUploads
                    && f.State != completed
                    && f.State != failed
                    && f.State != cancelled))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsCompleted, true), ct) > 0;
        }

        await tx.CommitAsync(ct);
        return new FileTransitionResult(FileRowExisted: true, PackageCompleted: packageCompleted);
    }

    public async Task UpdateFinishedAsync(int fileId, DateTime finishedDateTime, string? fileUrl, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await db.Set<UploadPackageFileDbm>()
            .Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.FinishedDateTime, finishedDateTime)
                .SetProperty(f => f.FileUrl, fileUrl ?? string.Empty), ct);
    }

    /// <summary>
    /// Rewrites <see cref="UploadPackageFileDbm.QueueOrder"/> for many files in one
    /// transaction. A single reorder renumbers the whole queue, so this is called with the
    /// full changed set rather than one row at a time.
    /// </summary>
    public async Task UpdateQueueOrderAsync(IReadOnlyDictionary<int, int> ordersByFileId, CancellationToken ct = default)
    {
        if (ordersByFileId.Count == 0)
        {
            return;
        }

        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach ((int fileId, int order) in ordersByFileId)
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.QueueOrder, order), ct);
        }

        await tx.CommitAsync(ct);
    }

    protected override UploadPackageFileDto MapToDto(UploadPackageFileDbm entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FileDirectory = entity.FileDirectory,
        FileSize = entity.FileSize,
        FileHoster = entity.FileHoster,
        StartDateTime = entity.StartDateTime,
        FinishedDateTime = entity.FinishedDateTime,
        FileUrl = entity.FileUrl,
        FileHosterName = entity.FileHosterName,
        FileHosterAccount = entity.FileHosterAccount,
        State = (FileState)entity.State,
        Error = entity.Error,
        IsHashingComplete = entity.IsHashingComplete,
        FileHash = entity.FileHash,
        FileHosterLoginId = entity.FileHosterLoginId,
        SortOrder = entity.SortOrder,
        QueueOrder = entity.QueueOrder,
        PackageId = entity.PackageId,
        IsHidden = entity.IsHidden,
        IsRemovedFromUploads = entity.IsRemovedFromUploads,
    };

    protected override void MapToDto(UploadPackageFileDbm entity, UploadPackageFileDto dto)
    {
        dto.Id = entity.Id;
        dto.FileName = entity.FileName;
        dto.FileDirectory = entity.FileDirectory;
        dto.FileSize = entity.FileSize;
        dto.FileHoster = entity.FileHoster;
        dto.StartDateTime = entity.StartDateTime;
        dto.FinishedDateTime = entity.FinishedDateTime;
        dto.FileUrl = entity.FileUrl;
        dto.FileHosterName = entity.FileHosterName;
        dto.FileHosterAccount = entity.FileHosterAccount;
        dto.State = (FileState)entity.State;
        dto.Error = entity.Error;
        dto.IsHashingComplete = entity.IsHashingComplete;
        dto.FileHash = entity.FileHash;
        dto.FileHosterLoginId = entity.FileHosterLoginId;
        dto.SortOrder = entity.SortOrder;
        dto.QueueOrder = entity.QueueOrder;
        dto.PackageId = entity.PackageId;
        dto.IsHidden = entity.IsHidden;
        dto.IsRemovedFromUploads = entity.IsRemovedFromUploads;
    }

    protected override UploadPackageFileDbm MapToDbm(UploadPackageFileDto dto) => ToDbm(dto);

    /// <summary>
    /// The dto→row mapping as a static, so <see cref="UploadPackageRepository.InsertWithFilesAsync"/>
    /// can build file rows for its single-save package graph without duplicating the field list.
    /// </summary>
    internal static UploadPackageFileDbm ToDbm(UploadPackageFileDto dto) => new()
    {
        Id = dto.Id,
        FileName = dto.FileName ?? string.Empty,
        FileDirectory = dto.FileDirectory ?? string.Empty,
        FileSize = dto.FileSize,
        FileHoster = dto.FileHoster ?? string.Empty,
        StartDateTime = dto.StartDateTime,
        FinishedDateTime = dto.FinishedDateTime,
        FileUrl = dto.FileUrl ?? string.Empty,
        FileHosterName = dto.FileHosterName ?? string.Empty,
        FileHosterAccount = dto.FileHosterAccount,
        State = (int)dto.State,
        Error = dto.Error,
        IsHashingComplete = dto.IsHashingComplete,
        FileHash = dto.FileHash,
        FileHosterLoginId = dto.FileHosterLoginId,
        SortOrder = dto.SortOrder,
        QueueOrder = dto.QueueOrder,
        PackageId = dto.PackageId,
        IsHidden = dto.IsHidden,
        IsRemovedFromUploads = dto.IsRemovedFromUploads,
    };

    /// <summary>
    /// Soft-removes one or more files from the Uploads tab. The Uploaded tab keeps
    /// showing them (it filters by <see cref="UploadPackageFileDbm.IsHidden"/>, not
    /// <see cref="UploadPackageFileDbm.IsRemovedFromUploads"/>).
    /// </summary>
    public async Task<int> SoftRemoveFromUploadsAsync(IEnumerable<int> fileIds, CancellationToken ct = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        return await db.Set<UploadPackageFileDbm>()
            .Where(f => fileIds.Contains(f.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsRemovedFromUploads, true), ct);
    }
}
