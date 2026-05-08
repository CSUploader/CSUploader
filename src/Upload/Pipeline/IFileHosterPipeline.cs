// <copyright file="IFileHosterPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Per-hoster strategy. Implementations own their auth shape (token, cookie, OAuth, API
/// key, anything) — <see cref="AttemptRunner"/> never inspects credentials beyond passing
/// them in via <see cref="AttemptContext.Credentials"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting concerns the runner has already handled before <see cref="RunAsync"/>:
/// proxy selection, <see cref="Lib.Net.Http.HttpHandler"/> construction, logging hookup,
/// cancellation propagation. Implementations must use <c>ctx.Handler</c> for all HTTP —
/// it is non-null by type and pre-configured with the chosen proxy.
/// </para>
/// <para>
/// Implementations are typically singletons holding per-credentials caches (e.g. a
/// <c>ConcurrentDictionary&lt;int, AuthState&gt;</c> keyed by <c>Credentials.Id</c>) so
/// the same login is reused across files. Cache invalidation on auth failure is the
/// pipeline's responsibility.
/// </para>
/// </remarks>
/// <example>
/// A token-based hoster (Rapidgator-style):
/// <code>
/// var auth = await GetOrLoginAsync(ctx);
/// var folder = await CreateFolderAsync(ctx, auth);
/// await UploadAsync(ctx, auth, folder);
/// yield return new TransferCompleted(url);
/// </code>
/// A cookie-based hoster: stash a <c>CookieContainer</c> in the auth state; reuse on
/// subsequent attempts. The runner doesn't care.
/// </example>
public interface IFileHosterPipeline
{
    /// <summary>Hoster name, must match the key used by <see cref="IFileHosterRegistry"/>.</summary>
    string Name { get; }

    /// <summary>True when the hoster needs the file's content hash before upload (e.g. Rapidgator MD5).</summary>
    bool RequiresHashingBeforeUpload { get; }

    /// <summary>True when the hoster computes a hash post-upload (rare, usually false).</summary>
    bool RequiresHashingAfterUpload { get; }

    /// <summary>
    /// Runs the protocol-specific portion of an upload attempt. Yields events for progress
    /// and outcomes. Must terminate with no more than one of <see cref="TransferCompleted"/>,
    /// <see cref="AttemptFailed"/>, or <see cref="AttemptCancelled"/> — the runner adds the
    /// <see cref="AttemptCompleted"/> envelope itself.
    /// </summary>
    IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct);
}
