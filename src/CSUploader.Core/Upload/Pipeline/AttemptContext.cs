// <copyright file="AttemptContext.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Immutable per-attempt context flowing through <see cref="AttemptRunner"/> and into
/// <see cref="IFileHosterPipeline"/>. Every property is non-nullable except where genuinely
/// optional (<see cref="FileHash"/> — only present once the hashing stage completes).
/// </summary>
public sealed record AttemptContext
{
    public required Guid AttemptId { get; init; }

    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required long FileSize { get; init; }

    /// <summary>Hex-lowercased hash, set after hashing completes. Null on first construction.</summary>
    public string? FileHash { get; init; }

    public required string HosterName { get; init; }

    public required FileHosterLoginDto Credentials { get; init; }

    public required ProxyChoice Proxy { get; init; }

    public required HttpHandler Handler { get; init; }

    public required IAppLogger Logger { get; init; }

    public required SpeedBudget SpeedBudget { get; init; }

    public required CancellationToken Cancellation { get; init; }
}
