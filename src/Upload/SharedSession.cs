// <copyright file="SharedSession.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;

namespace CSUploader.Upload;

/// <summary>
/// Thread-safe session cache shared across all FileHosterClient instances
/// for the same hoster within a single package. Ensures login happens only once.
/// </summary>
public class SharedSession : IDisposable
{
    private readonly ConcurrentDictionary<string, object?> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loginLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Gets a cached value, or calls the factory to create it (exactly once, even under concurrency).
    /// </summary>
    public async Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T?>> factory) where T : class
    {
        if (_cache.TryGetValue(key, out object? existing) && existing is T cached)
        {
            return cached;
        }

        await _loginLock.WaitAsync();
        try
        {
            // Double-check after acquiring the lock
            if (_cache.TryGetValue(key, out existing) && existing is T cached2)
            {
                return cached2;
            }

            T? result = await factory();
            if (result is not null)
            {
                _cache[key] = result;
            }

            return result;
        }
        finally
        {
            _loginLock.Release();
        }
    }
}
