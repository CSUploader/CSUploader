// <copyright file="LogEntryRepository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class LogEntryRepository(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Repository<LogEntryDbm, LogEntryDto>(dbFactory)
{
    /// <summary>
    /// Returns the most recent <paramref name="maxCount"/> entries ordered ascending
    /// by timestamp so the UI can append them in chronological order.
    /// </summary>
    public async Task<LogEntryDto[]> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        LogEntryDbm[] entities = await db.LogEntries
            .OrderByDescending(e => e.Id)
            .Take(maxCount)
            .ToArrayAsync(cancellationToken);

        return [.. entities.Reverse().Select(MapToDto)];
    }

    /// <summary>
    /// Deletes entries older than <paramref name="cutoff"/>. Returns the number of rows removed.
    /// </summary>
    public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
        => DeleteByPredicateAsync(e => e.DateTime < cutoff, cancellationToken);

    protected override LogEntryDto MapToDto(LogEntryDbm entity) => new()
    {
        Id = entity.Id,
        DateTime = entity.DateTime,
        LogType = (LogType)entity.LogType,
        Filename = entity.Filename,
        Function = entity.Function,
        LineNumber = entity.LineNumber,
        ThreadId = entity.ThreadId,
        Message = entity.Message,
    };

    protected override void MapToDto(LogEntryDbm entity, LogEntryDto dto)
    {
        dto.Id = entity.Id;
        dto.DateTime = entity.DateTime;
        dto.LogType = (LogType)entity.LogType;
        dto.Filename = entity.Filename;
        dto.Function = entity.Function;
        dto.LineNumber = entity.LineNumber;
        dto.ThreadId = entity.ThreadId;
        dto.Message = entity.Message;
    }

    protected override LogEntryDbm MapToDbm(LogEntryDto dto) => new()
    {
        Id = dto.Id,
        DateTime = dto.DateTime,
        LogType = (int)dto.LogType,
        Filename = dto.Filename,
        Function = dto.Function,
        LineNumber = dto.LineNumber,
        ThreadId = dto.ThreadId,
        Message = dto.Message,
    };
}
