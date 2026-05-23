// <copyright file="PauseToken.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

public readonly struct PauseToken(PauseTokenSource source)
{
    // Nullable to match `default(PauseToken)` semantics: the struct can be
    // zero-initialized without going through the constructor, in which case
    // `_source` is null and the `?.` fallbacks below kick in. Without the
    // nullable annotation the field looked non-null but was read defensively,
    // producing a confusing NRT-vs-runtime mismatch.
    private readonly PauseTokenSource? _source = source;

    public Task<bool> IsPaused() => _source?.IsPaused() ?? Task.FromResult(false);

    public Task PauseIfRequestedAsync(CancellationToken token = default) => _source?.PauseIfRequestedAsync(token) ?? Task.CompletedTask;
}
