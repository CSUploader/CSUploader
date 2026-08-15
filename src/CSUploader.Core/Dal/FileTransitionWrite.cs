// <copyright file="FileTransitionWrite.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

/// <summary>
/// Everything one file-state transition implies for the database, gathered so
/// <see cref="UploadPackageFileRepository.PersistTransitionAsync"/> can commit it as a single
/// transaction: the state itself, a hash that became valid or was thrown away with it, and a
/// package-completed flag the transition flips in either direction.
/// </summary>
/// <remarks>
/// The point is atomicity. Issued as separate statements, a failure in the middle left the row
/// half-transitioned — a persisted state whose hash clear never landed, or a reopened file inside a
/// package still marked complete — and those partial shapes are exactly the stale-data bugs the
/// persisted transition exists to prevent. All-or-nothing means a failure leaves the previous
/// consistent shape in place, which the next successful write then replaces wholesale.
/// </remarks>
public sealed record FileTransitionWrite
{
    /// <summary>Gets the id of the file row being written.</summary>
    public required int FileId { get; init; }

    /// <summary>Gets the new <see cref="Upload.FileState"/>, as its stored int.</summary>
    public required int State { get; init; }

    /// <summary>Gets the row's error text; null clears it.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the uploaded file's URL; null clears it.</summary>
    public string? FileUrl { get; init; }

    /// <summary>
    /// Gets the finish stamp for a terminal transition. When set, <see cref="StartedDateTime"/> is
    /// considered too; when null, neither date column is touched.
    /// </summary>
    public DateTime? FinishedDateTime { get; init; }

    /// <summary>
    /// Gets the attempt's real start time. Only written alongside <see cref="FinishedDateTime"/>,
    /// and only when non-null — a null keeps the add-time captured at insert.
    /// </summary>
    public DateTime? StartedDateTime { get; init; }

    /// <summary>
    /// Gets a hash that became valid with this transition (stored with IsHashingComplete = true).
    /// Mutually exclusive with <see cref="DiscardHash"/>.
    /// </summary>
    public string? HashToStore { get; init; }

    /// <summary>
    /// Gets a value indicating whether the stored hash is discarded with this transition
    /// (FileHash = null, IsHashingComplete = false) — Reset, or the re-upload of a completed file.
    /// </summary>
    public bool DiscardHash { get; init; }

    /// <summary>
    /// Gets the id of a package this transition re-opens (IsCompleted = false): the file was
    /// terminal and is queued again.
    /// </summary>
    public int? PackageIdNoLongerCompleted { get; init; }

    /// <summary>
    /// Gets the id of a package this transition MAY have finished: this file just went terminal
    /// and, as far as the in-memory package knows, it was the last non-terminal one. The database
    /// decides — <see cref="UploadPackageFileRepository.PersistTransitionAsync"/> only sets
    /// IsCompleted when the stored rows agree, and reports the verdict in
    /// <see cref="FileTransitionResult.PackageCompleted"/>.
    /// </summary>
    /// <remarks>
    /// A request rather than a command because memory and disk can disagree: an earlier file's
    /// transition may have failed and rolled back, leaving its row non-terminal. Trusting memory
    /// would then stamp a package complete around a row the database says is still running — and
    /// announce it. The check runs inside the same transaction as this file's state update, so it
    /// sees that update and everything the write chain committed before it.
    /// </remarks>
    public int? PackageIdNowCompleted { get; init; }
}

/// <summary>
/// What <see cref="UploadPackageFileRepository.PersistTransitionAsync"/> actually committed.
/// Events announce persisted facts, so the caller fires them from this — not from what it asked
/// for.
/// </summary>
/// <param name="FileRowExisted">
/// False when the file row was gone (deleted by history cleanup between the transition and the
/// write): nothing was written and nothing may be announced.
/// </param>
/// <param name="PackageCompleted">
/// True when the package-completed flag was actually set — the database agreed that every
/// still-listed row of the package is terminal.
/// </param>
public readonly record struct FileTransitionResult(bool FileRowExisted, bool PackageCompleted);
