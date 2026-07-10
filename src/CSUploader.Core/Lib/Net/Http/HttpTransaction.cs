// <copyright file="HttpTransaction.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Captures a complete HTTP request + response pair for logging/inspection.
/// </summary>
public class HttpTransaction
{
    // ── Request ──

    public string Method { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the proxy used for this request, e.g. "http://1.2.3.4:8080"
    /// or "(direct)" when no proxy was configured. Surfaced in the Logs tab so users can tell
    /// at a glance which proxy a failure went through.
    /// </summary>
    public string Proxy { get; set; } = "(direct)";

    public Dictionary<string, string[]> RequestHeaders { get; set; } = [];

    public string? RequestBody { get; set; }

    public byte[]? RequestBodyBytes { get; set; }

    // ── Response ──

    public int StatusCode { get; set; }

    public string StatusReason { get; set; } = string.Empty;

    public Dictionary<string, string[]> ResponseHeaders { get; set; } = [];

    public string? ResponseBody { get; set; }

    public byte[]? ResponseBodyBytes { get; set; }

    // ── Timing ──

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public TimeSpan Duration => EndTime - StartTime;

    // ── Helpers ──

    public string RequestHeadersText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{Method} {Url} HTTP/1.1");
            foreach ((string? key, string[]? values) in RequestHeaders)
            {
                foreach (string value in values)
                {
                    sb.AppendLine($"{key}: {value}");
                }
            }

            return sb.ToString();
        }
    }

    public string ResponseHeadersText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"HTTP/1.1 {StatusCode} {StatusReason}");
            foreach ((string? key, string[]? values) in ResponseHeaders)
            {
                foreach (string value in values)
                {
                    sb.AppendLine($"{key}: {value}");
                }
            }

            return sb.ToString();
        }
    }

    public string Summary => $"{Method} {Url} → {StatusCode} {StatusReason} ({Duration.TotalMilliseconds:F0}ms) [proxy: {Proxy}]";

    public static string ToHexDump(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return "(empty)";
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"{i:X8}  ");

            // Hex bytes
            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                {
                    sb.Append($"{data[i + j]:X2} ");
                }
                else
                {
                    sb.Append("   ");
                }

                if (j == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");

            // ASCII
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                byte b = data[i + j];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            sb.AppendLine("|");
        }

        return sb.ToString();
    }

    public static string PrettyPrintJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "(empty)";
        }

        try
        {
            JsonElement element = JsonSerializer.Deserialize<JsonElement>(json);
            return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
