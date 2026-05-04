// <copyright file="ProxyTestResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

/// <summary>
/// Result of a single proxy connectivity test. <see cref="Success"/> = true means the
/// HTTP request through the proxy completed; <see cref="LatencyMs"/> is the round-trip
/// time and <see cref="DetectedIp"/> is the IP the upstream endpoint saw (handy for
/// confirming a proxy actually masks the user's real IP).
/// </summary>
public sealed class ProxyTestResult
{
    public bool Success { get; init; }

    public long LatencyMs { get; init; }

    public string? DetectedIp { get; init; }

    public string? Message { get; init; }

    public static ProxyTestResult Ok(long latencyMs, string? detectedIp) =>
        new() { Success = true, LatencyMs = latencyMs, DetectedIp = detectedIp };

    public static ProxyTestResult Failed(string message) =>
        new() { Success = false, Message = message };
}
