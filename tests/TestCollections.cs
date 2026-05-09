// <copyright file="TestCollections.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests;

// Localizer.Instance is a process-wide singleton with a mutable Culture. Test classes that
// either mutate Culture or read localized strings must share this collection so xUnit
// serializes them — otherwise per-test IDisposable cleanup races against parallel classes
// reading the mid-flight value.
[CollectionDefinition(LocalizerCollection.Name, DisableParallelization = true)]
public sealed class LocalizerCollection
{
    public const string Name = "Localizer";
}
