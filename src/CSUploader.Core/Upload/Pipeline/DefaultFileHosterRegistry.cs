// <copyright file="DefaultFileHosterRegistry.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Default registry constructed from the DI-injected enumerable of pipelines. Each
/// new hoster is registered by adding one DI line; no static factory map needed.
/// </summary>
public sealed class DefaultFileHosterRegistry(IEnumerable<IFileHosterPipeline> pipelines) : IFileHosterRegistry
{
    private readonly Dictionary<string, IFileHosterPipeline> _byName = pipelines.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IFileHosterPipeline? Find(string hosterName)
        => _byName.TryGetValue(hosterName, out IFileHosterPipeline? p) ? p : null;
}
