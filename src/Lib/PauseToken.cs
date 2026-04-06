// <copyright file="PauseToken.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

public struct PauseToken
{
    private readonly PauseTokenSource source;

    public PauseToken(PauseTokenSource source)
    {
        this.source = source;
    }

    public Task<bool> IsPaused()
    {
        return source.IsPaused();
    }

    public Task PauseIfRequestedAsync(CancellationToken token = default)
    {
        return source.PauseIfRequestedAsync(token);
    }
}
