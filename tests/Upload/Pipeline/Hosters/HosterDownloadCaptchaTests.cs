// <copyright file="HosterDownloadCaptchaTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Pins every hoster's declared download-captcha verdict to the research matrix in
/// <c>docs/hoster-download-captcha.md</c>, PARSED from that file so the register and the pipelines
/// cannot drift apart — a drive-by edit to either fails here. Everything is asserted THROUGH
/// <see cref="IFileHosterPipeline"/>: for subclasses of a base that binds the interface slot
/// (XFS, YetiShare, XfsPro, MoneyPlatform), a same-named property that fails to override would
/// never be reached through the interface, and calling it directly would hide exactly that bug.
/// </summary>
public class HosterDownloadCaptchaTests
{
    [Fact]
    public void InterfaceDefault_IsUnknown_NotAClaim()
    {
        // The resting state for a pipeline that declares nothing must be "not verified" — most
        // hosts start unresearched, and Unknown is the only honest default.
        IFileHosterPipeline pipeline = new PipelineWithNoOverrides();

        Assert.Equal(DownloadCaptchaRequirement.Unknown, pipeline.DownloadCaptcha);
    }

    /// <summary>
    /// The whole catalogue against the research matrix, THROUGH the DI-composed registry the wizard
    /// itself reads. Covers every shipping hoster including the researched-Unknown rows, and pins
    /// the matrix's name set to <see cref="FileHosterClient.NamesAlphabetical"/> — adding a hoster
    /// without a conscious verdict here (and a row in docs/hoster-download-captcha.md) fails the
    /// build, exactly like removing one without cleaning up.
    /// </summary>
    [Fact]
    public void EveryHoster_MatchesTheResearchMatrix()
    {
        // The matrix is PARSED from docs/hoster-download-captcha.md rather than duplicated here —
        // a hard-coded copy and the document could drift while both claim they cannot.
        Dictionary<string, DownloadCaptchaRequirement> matrix = ParseResearchMatrix();
        Assert.True(matrix.Count > 0, "no rows parsed from docs/hoster-download-captcha.md");

        // The matrix must cover exactly the shipping catalogue — no more, no fewer.
        Assert.Equal(
            FileHosterClient.NamesAlphabetical.OrderBy(n => n, StringComparer.Ordinal),
            matrix.Keys.OrderBy(n => n, StringComparer.Ordinal));

        // The same composition the app boots with, minus the heads: pipelines resolve from
        // AddCoreServices plus a double for IInteractiveAuthService (the one head interface some
        // pipelines constructor-inject).
        ServiceCollection services = new();
        services.AddCoreServices(Path.GetTempPath());
        services.AddSingleton(Mock.Of<IInteractiveAuthService>());
        using ServiceProvider provider = services.BuildServiceProvider();
        IFileHosterRegistry registry = provider.GetRequiredService<IFileHosterRegistry>();

        List<string> mismatches = [];
        foreach ((string name, DownloadCaptchaRequirement expected) in matrix)
        {
            IFileHosterPipeline? pipeline = registry.Find(name);
            if (pipeline is null)
            {
                mismatches.Add($"{name}: no pipeline registered");
            }
            else if (pipeline.DownloadCaptcha != expected)
            {
                mismatches.Add($"{name}: pipeline declares {pipeline.DownloadCaptcha}, matrix says {expected}");
            }
        }

        Assert.True(mismatches.Count == 0, "download-captcha drift:\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// Reads the verdict table out of docs/hoster-download-captcha.md: one row per hoster,
    /// <c>| Name | Verdict | Confidence | Checked | Evidence |</c>. Malformed verdict/confidence
    /// cells fail loudly rather than parsing to nothing.
    /// </summary>
    private static Dictionary<string, DownloadCaptchaRequirement> ParseResearchMatrix()
    {
        string path = Path.Combine(FindRepoRoot(), "docs", "hoster-download-captcha.md");
        Dictionary<string, DownloadCaptchaRequirement> matrix = new(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal))
            {
                continue;
            }

            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);

            // Split on a "| a | b |" row yields ["", a, b, ..., ""] — 5 columns = 7 parts.
            if (cells.Length != 7 || cells[1] is "Hoster" or "---")
            {
                continue;
            }

            // Canonical names only — Enum.TryParse would also accept "1", which no row should say.
            DownloadCaptchaRequirement? verdict = cells[2] switch
            {
                nameof(DownloadCaptchaRequirement.Required) => DownloadCaptchaRequirement.Required,
                nameof(DownloadCaptchaRequirement.NotRequired) => DownloadCaptchaRequirement.NotRequired,
                nameof(DownloadCaptchaRequirement.Unknown) => DownloadCaptchaRequirement.Unknown,
                _ => null,
            };
            Assert.True(verdict is not null, $"unparseable verdict '{cells[2]}' for {cells[1]} in hoster-download-captcha.md");
            Assert.Contains(cells[3], (string[])["high", "medium"]);
            Assert.True(
                DateOnly.TryParseExact(cells[4], "yyyy-MM-dd", out _),
                $"unparseable Checked date '{cells[4]}' for {cells[1]}");
            Assert.False(string.IsNullOrWhiteSpace(cells[5]), $"blank Evidence cell for {cells[1]}");
            Assert.False(matrix.ContainsKey(cells[1]), $"duplicate row for {cells[1]}");
            matrix[cells[1]] = verdict.Value;
        }

        return matrix;
    }

    // CallerFilePath, NOT AppContext.BaseDirectory: the repo can build to a temp OutDir, putting
    // the binary outside the repo. Same pattern + rationale as I18nRegenGateTests.FindRepoRoot.
    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "")
    {
        DirectoryInfo? dir = Directory.GetParent(thisFilePath);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSUploader.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from " + thisFilePath);
    }

    /// <summary>A minimal pipeline that overrides nothing optional, so every interface default is
    /// observable exactly as a real no-override hoster would surface it.</summary>
    private sealed class PipelineWithNoOverrides : IFileHosterPipeline
    {
        public string Name => "TestHost";

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, CSUploader.Lib.Net.ProxyChoice proxy, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
