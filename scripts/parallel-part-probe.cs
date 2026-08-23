// <copyright file="parallel-part-probe.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>
//
// THROWAWAY probe for docs/superpowers/plans/2026-08-23-parallel-chunk-upload.md, Task 0.
//
// Question: do these hosts throttle per-CONNECTION? If they do, splitting one file across N
// concurrent presigned part PUTs is a real speed-up; if they throttle per-account, it is churn.
//
// Method: ask the host for a multipart handle (which yields independent presigned R2 PUT URLs),
// then push the SAME total number of bytes at degree 1, 2 and 4 and compare wall-clock. Bytes are
// random and complete-upload is never called, so nothing is ever published — it leaves only an
// unfinalised multipart that R2 lifecycle-expires.
//
// Run:  dotnet run scripts/parallel-part-probe.cs -- [totalMiB]

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

int totalMiB = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 40;
long totalBytes = (long)totalMiB * 1024 * 1024;
int[] degrees = [1, 4, 8];

// A declared size big enough that the host hands back at least 4 part URLs. VikingFile's live
// partSize is 100 MiB, so 4 parts needs ~400 MiB declared. We never send that much — a multipart
// part may be short (S3 requires >= 5 MiB only for non-final parts, and we never finalise).
const long DeclaredSize = 8L * 100 * 1024 * 1024;

using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

byte[] payload = new byte[8 * 1024 * 1024];
Random.Shared.NextBytes(payload);

Console.WriteLine($"Probe: {totalMiB} MiB per run, degrees {string.Join(", ", degrees)}");
Console.WriteLine();

await ProbeAsync("VikingFile", GetVikingFilePartUrlsAsync);

Console.WriteLine();
Console.WriteLine("Reminder: no complete-upload was called, so nothing was published.");

async Task ProbeAsync(string hostName, Func<Task<string[]>> getPartUrls)
{
    Console.WriteLine($"=== {hostName} ===");
    Console.WriteLine($"{"degree",-8}{"seconds",-12}{"MiB/s",-10}{"vs degree 1"}");

    double baseline = 0;
    foreach (int degree in degrees)
    {
        string[] urls;
        try
        {
            urls = await getPartUrls();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  init failed: {ex.Message}");
            return;
        }

        if (urls.Length < degree)
        {
            Console.WriteLine($"  host returned only {urls.Length} part URLs; cannot test degree {degree}");
            continue;
        }

        long perPart = totalBytes / degree;
        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            await Task.WhenAll(Enumerable.Range(0, degree).Select(i => PutAsync(urls[i], perPart)));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  degree {degree} failed: {ex.Message}");
            continue;
        }

        clock.Stop();
        double seconds = clock.Elapsed.TotalSeconds;
        double mibPerSecond = totalMiB / seconds;
        if (degree == 1)
        {
            baseline = mibPerSecond;
        }

        string ratio = baseline > 0 ? $"{mibPerSecond / baseline:F2}x" : "-";
        Console.WriteLine($"{degree,-8}{seconds,-12:F2}{mibPerSecond,-10:F2}{ratio}");
    }
}

async Task PutAsync(string url, long bytes)
{
    using StreamContent content = new(new RepeatingStream(payload, bytes));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    content.Headers.ContentLength = bytes;

    using HttpResponseMessage response = await http.PutAsync(url, content);
    if (!response.IsSuccessStatusCode)
    {
        string body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
    }
}

async Task<string[]> GetVikingFilePartUrlsAsync()
{
    using FormUrlEncodedContent form = new([
        new KeyValuePair<string, string>("size", DeclaredSize.ToString(CultureInfo.InvariantCulture)),
    ]);

    using HttpResponseMessage response = await http.PostAsync("https://vikingfile.com/api/get-upload-url", form);
    string body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"get-upload-url HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
    }

    using JsonDocument doc = JsonDocument.Parse(body);
    return [.. doc.RootElement.GetProperty("urls").EnumerateArray().Select(u => u.GetString()!)];
}

/// <summary>Serves `length` bytes by cycling a small in-memory buffer, so a large PUT costs no disk
/// and no allocation. Random content, so nothing compresses away in transit.</summary>
internal sealed class RepeatingStream(byte[] source, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int allowed = (int)Math.Min(count, length - _position);
        if (allowed <= 0)
        {
            return 0;
        }

        int from = (int)(_position % source.Length);
        int take = Math.Min(allowed, source.Length - from);
        Array.Copy(source, from, buffer, offset, take);
        _position += take;
        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
