// <copyright file="AttemptInputs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Caller-supplied inputs to <see cref="AttemptRunner.RunAsync"/>. The runner picks the
/// proxy and builds the <see cref="Lib.Net.Http.HttpHandler"/> itself, then promotes
/// these inputs into a full <see cref="AttemptContext"/> for the pipeline.
/// </summary>
public sealed record AttemptInputs
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public string? FileHash { get; init; }
    public required string HosterName { get; init; }
    public required FileHosterLoginDto Credentials { get; init; }
    public required IAppLogger Logger { get; init; }
    public required SpeedBudget SpeedBudget { get; init; }

    /// <summary>
    /// The user's ceiling on concurrent parts within one file. Only the ceiling: this is built by
    /// <c>PackageFile.BuildAttemptInputs</c>, which has no access to the pipeline registry and so
    /// cannot know what the hoster declares. <c>AttemptRunner</c> combines the two.
    /// <para>
    /// Defaulted to 1, not <c>required</c> — many tests construct these directly, and 1 is also the
    /// safe value.
    /// </para>
    /// </summary>
    public int MaxParallelPartsCeiling { get; init; } = 1;
}
