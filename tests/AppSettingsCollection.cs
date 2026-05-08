// <copyright file="AppSettingsCollection.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests;

/// <summary>
/// xUnit collection used to serialise test classes that mutate the static
/// <see cref="CSUploader.Upload.AppSettings.Current"/>. Without it those tests can run
/// concurrently across classes and stomp on each other's overrides — flaking unrelated
/// proxy tests because <see cref="CSUploader.Lib.Net.ProxyManager.NextProxy"/> reads
/// <c>AppSettings.Current.ProxiesEnabled</c>.
/// </summary>
[CollectionDefinition(nameof(AppSettingsCollection), DisableParallelization = true)]
public sealed class AppSettingsCollection
{
}
