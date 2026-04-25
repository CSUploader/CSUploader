// <copyright file="PauseToken.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

public readonly struct PauseToken(PauseTokenSource source)
{
    private readonly PauseTokenSource _source = source;

    public Task<bool> IsPaused() => _source?.IsPaused() ?? Task.FromResult(false);

    public Task PauseIfRequestedAsync(CancellationToken token = default) => _source?.PauseIfRequestedAsync(token) ?? Task.CompletedTask;
}
