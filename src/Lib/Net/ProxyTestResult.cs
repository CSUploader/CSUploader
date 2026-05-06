// <copyright file="ProxyTestResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Lib.Net;

/// <summary>
/// Result of a single proxy connectivity test. <see cref="Success"/> = true means the
/// HTTP request through the proxy completed; <see cref="LatencyMs"/> is the round-trip
/// time and <see cref="DetectedIp"/> is the IP the upstream endpoint saw (handy for
/// confirming a proxy actually masks the user's real IP).
/// </summary>
public sealed record ProxyTestResult
{
    public bool Success { get; init; }

    public long LatencyMs { get; init; }

    public string? DetectedIp { get; init; }

    /// <summary>
    /// Raw response body from the test endpoint (or the exception text on failure). Held
    /// separately from <see cref="DetectedIp"/> so the Connection Manager grid can show
    /// a short status line and stash the full response behind a Details button — Squid
    /// proxies and the like return multi-kilobyte HTML error pages.
    /// </summary>
    public string? Body { get; init; }

    public string? Message { get; init; }

    /// <summary>
    /// Full HTTP transaction (request + response, with headers and body) for the test.
    /// Surfaced via the Details button so the user gets the same diagnostic view as
    /// the Logs tab uses for upload traffic.
    /// </summary>
    public HttpTransaction? Transaction { get; init; }

    public static ProxyTestResult Ok(long latencyMs, string? body)
    {
        string? trimmed = body?.Trim();
        return new ProxyTestResult
        {
            Success = true,
            LatencyMs = latencyMs,
            DetectedIp = LooksLikeIpAddress(trimmed) ? trimmed : null,
            Body = trimmed,
        };
    }

    public static ProxyTestResult Failed(string message) =>
        new() { Success = false, Message = message, Body = message };

    private static bool LooksLikeIpAddress(string? value)
    {
        // Restrictive on purpose: the upstream test endpoint (api.ipify.org) returns a
        // bare IPv4/IPv6, anything else is a misbehaving proxy and shouldn't be shown
        // as the "detected IP".
        return !string.IsNullOrWhiteSpace(value) && System.Net.IPAddress.TryParse(value, out _);
    }
}
