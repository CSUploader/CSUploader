// <copyright file="UploadNowStubs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.TestSupport;

/// <summary>
/// UploadNow's four-stage API, stubbed. Shared because the shape is long enough that a second copy
/// would drift from the first — the JSON here is the live capture the pipeline was built against.
/// </summary>
internal static class UploadNowStubs
{
    internal const string SignUpJson =
        """{"kind":"identitytoolkit#SignupNewUserResponse","idToken":"tok-abc","refreshToken":"r","expiresIn":"3600","localId":"OWNER123"}""";

    internal const string FolderJson = """{"id":"Hzg2ZNZ"}""";

    internal const string DeclareJson = """
        {"ids":["b483010d-76bd-4a01-9574-3447237bbd5b"],"bucketConfig":{"signerUrl":"/signer/buckets/43057deb/sign-url","aws_key":"2f488bd324502ec2","awsSignatureVersion":"4","bucket":"upnow-prod","cloudfront":false,"computeContentMd5":true,"awsRegion":"auto","aws_url":"https://acct.r2.cloudflarestorage.com/upnow-prod","maxConcurrentParts":5}}
        """;

    internal const string InitiateXml =
        """<?xml version="1.0" encoding="UTF-8"?><InitiateMultipartUploadResult><UploadId>UP-1</UploadId></InitiateMultipartUploadResult>""";

    internal const string CompleteXml =
        """<?xml version="1.0" encoding="UTF-8"?><CompleteMultipartUploadResult><ETag>&quot;abc-1&quot;</ETag></CompleteMultipartUploadResult>""";

    /// <summary>Routes by URL, as the live service's stages are distinguished only by path.</summary>
    internal static HttpResponseSnapshot Reply(string url)
    {
        if (url.Contains("signUp", StringComparison.Ordinal))
        {
            return new HttpResponseSnapshot(200, SignUpJson, Array.Empty<string>());
        }

        if (url.EndsWith("/api/file/folders", StringComparison.Ordinal))
        {
            return new HttpResponseSnapshot(201, FolderJson, Array.Empty<string>());
        }

        if (url.EndsWith("/api/file/files", StringComparison.Ordinal))
        {
            return new HttpResponseSnapshot(201, DeclareJson, Array.Empty<string>());
        }

        if (url.Contains("sign-url", StringComparison.Ordinal))
        {
            return new HttpResponseSnapshot(200, "deadbeef", Array.Empty<string>());
        }

        if (url.EndsWith("?uploads", StringComparison.Ordinal))
        {
            return new HttpResponseSnapshot(200, InitiateXml, Array.Empty<string>());
        }

        if (url.Contains("upload-done", StringComparison.Ordinal))
        {
            return new HttpResponseSnapshot(200, """{"message":"OK"}""", Array.Empty<string>());
        }

        return new HttpResponseSnapshot(200, CompleteXml, Array.Empty<string>());
    }
}
