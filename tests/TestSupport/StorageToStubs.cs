// <copyright file="StorageToStubs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests.TestSupport;

/// <summary>
/// storage.to's bootstrap and confirm responses, shared so the contract suite and the pipeline's own
/// suite cannot drift apart on the shape the live service returns.
/// </summary>
internal static class StorageToStubs
{
    internal const string HomeHtml = """
        <!DOCTYPE html><html><head>
        <meta name="csrf-token" content="TESTCSRF123">
        </head><body>storage.to</body></html>
        """;

    internal const string ConfirmJson = """
        {"success":true,"results":{"0":{"success":true,"file":{"id":"qTKjLKmo1","url":"https://storage.to/qTKjLKmo1"},"owner_token":"owner_v1_xyz"}}}
        """;
}
