// <copyright file="PackageTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Moq;

namespace CSUploader.Tests.Upload;

public class PackageTests
{
    [Fact]
    public void AddPackageFiles_NoArg_AddsOneFilePerSelectedFilePerHoster()
    {
        string tempA = Path.Combine(Path.GetTempPath(), "task1-a.bin");
        string tempB = Path.Combine(Path.GetTempPath(), "task1-b.bin");
        File.WriteAllText(tempA, "a");
        File.WriteAllText(tempB, "b");
        try
        {
            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [tempA, tempB],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = new(options);

            package.AddPackageFiles();

            PackageFile[] files = [.. package];
            Assert.Equal(2, files.Length);
            Assert.Contains(files, f => string.Equals(f.Name, Path.GetFileName(tempA), StringComparison.Ordinal));
            Assert.Contains(files, f => string.Equals(f.Name, Path.GetFileName(tempB), StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public void AddPackageFiles_IncludedFilesPerHoster_RestrictsToListedFilesPerHoster()
    {
        // The wizard's Summary-page fit: Rapidgator keeps only file A, BRupload keeps both.
        string tempA = Path.Combine(Path.GetTempPath(), $"task1-inc-a-{Guid.NewGuid():N}.bin");
        string tempB = Path.Combine(Path.GetTempPath(), $"task1-inc-b-{Guid.NewGuid():N}.bin");
        File.WriteAllText(tempA, "a");
        File.WriteAllText(tempB, "b");
        try
        {
            FileHosterClient rg = new("Rapidgator", Protocol.Http);
            FileHosterClient br = new("BRupload", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [tempA, tempB],
                FileHosters = new()
                {
                    { rg, new FileHosterLoginDto { FileHosterName = "Rapidgator" } },
                    { br, new FileHosterLoginDto { FileHosterName = "BRupload" } },
                },
                IncludedFilesPerHoster = new(StringComparer.Ordinal)
                {
                    ["Rapidgator"] = [tempA],
                    ["BRupload"] = [tempA, tempB],
                },
            };
            Package package = new(options);

            package.AddPackageFiles();

            PackageFile[] files = [.. package];
            // 3 pairs, not the 4 of a full cross-product: Rapidgator(A) + BRupload(A,B).
            Assert.Equal(3, files.Length);
            PackageFile rgFile = Assert.Single(files, f => f.FileHoster.Name == "Rapidgator");
            Assert.Equal(Path.GetFileName(tempA), rgFile.Name);
            Assert.Equal(2, files.Count(f => f.FileHoster.Name == "BRupload"));
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public void AddPackageFiles_IncludedFilesPerHoster_NoEntryForHoster_StaysUnrestricted()
    {
        // Map present but only Rapidgator listed → BRupload (no entry) keeps the full cross-product.
        string tempA = Path.Combine(Path.GetTempPath(), $"task1-noent-a-{Guid.NewGuid():N}.bin");
        string tempB = Path.Combine(Path.GetTempPath(), $"task1-noent-b-{Guid.NewGuid():N}.bin");
        File.WriteAllText(tempA, "a");
        File.WriteAllText(tempB, "b");
        try
        {
            FileHosterClient rg = new("Rapidgator", Protocol.Http);
            FileHosterClient br = new("BRupload", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [tempA, tempB],
                FileHosters = new()
                {
                    { rg, new FileHosterLoginDto { FileHosterName = "Rapidgator" } },
                    { br, new FileHosterLoginDto { FileHosterName = "BRupload" } },
                },
                IncludedFilesPerHoster = new(StringComparer.Ordinal) { ["Rapidgator"] = [tempA] },
            };
            Package package = new(options);

            package.AddPackageFiles();

            PackageFile[] files = [.. package];
            Assert.Single(files, f => f.FileHoster.Name == "Rapidgator");
            Assert.Equal(2, files.Count(f => f.FileHoster.Name == "BRupload")); // unrestricted
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public void AddPackageFiles_IncludedFilesPerHoster_EmptySetForHoster_UploadsNothingToIt()
    {
        // An explicit empty set (every file deselected for that hoster on Page 3) → no pairs for it.
        string temp = Path.Combine(Path.GetTempPath(), $"task1-empty-{Guid.NewGuid():N}.bin");
        File.WriteAllText(temp, "a");
        try
        {
            FileHosterClient rg = new("Rapidgator", Protocol.Http);
            FileHosterClient br = new("BRupload", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [temp],
                FileHosters = new()
                {
                    { rg, new FileHosterLoginDto { FileHosterName = "Rapidgator" } },
                    { br, new FileHosterLoginDto { FileHosterName = "BRupload" } },
                },
                IncludedFilesPerHoster = new(StringComparer.Ordinal) { ["Rapidgator"] = [] },
            };
            Package package = new(options);

            package.AddPackageFiles();

            PackageFile only = Assert.Single(package); // Rapidgator excluded, BRupload unrestricted
            Assert.Equal("BRupload", only.FileHoster.Name);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void AddPackageFiles_IncludedFile_StillRejectedByPerFileSizeCap()
    {
        // The allow-list only RESTRICTS — the per-file size cap still applies on top. A file that's
        // in the hoster's allow-list but over its cap must NOT be queued.
        string temp = Path.Combine(Path.GetTempPath(), $"task1-inc-cap-{Guid.NewGuid():N}.bin");
        File.WriteAllText(temp, "hello"); // 5 bytes
        try
        {
            FileHosterClient tiny = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [temp],
                FileHosters = new() { { tiny, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
                IncludedFilesPerHoster = new(StringComparer.Ordinal) { ["Rapidgator"] = [temp] }, // allow-listed…
            };
            Package package = new(options);

            // …but a cap of 0 means every byte exceeds it, so the size filter still drops the pair.
            StubRegistry registry = new(new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase) { ["Rapidgator"] = 0L });

            package.AddPackageFiles(registry, Mock.Of<IAppLogger>());

            Assert.Empty(package);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void AddPackageFiles_NoArg_WhenSelectedFilesEmpty_AddsNothing()
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        PackageOptions options = new()
        {
            Title = "test",
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = [],
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
        };
        Package package = new(options);

        package.AddPackageFiles();

        Assert.Empty(package);
    }

    [Fact]
    public void AddPackageFiles_NoArg_WhenSelectedFilesNull_AddsNothing()
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        PackageOptions options = new()
        {
            Title = "test",
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = null,
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
        };
        Package package = new(options);

        package.AddPackageFiles();

        Assert.Empty(package);
    }

    [Fact]
    public void AddPackageFiles_RegistryWithMaxFileSize_SkipsOversizePairs()
    {
        // Two hosters in the cross-product:
        //   * "Tiny"  — pipeline says MaxFileSize=0 (every byte exceeds it),
        //   * "Big"   — pipeline says null MaxFileSize (no cap).
        // The file is 5 bytes on disk. The user's complaint was that 38 (file, ExtMatrix)
        // pairs were getting queued just to fail at the pipeline's pre-check; this test
        // proves the filter now drops the (file, Tiny) pair at queue time while keeping
        // (file, Big) intact.
        string temp = Path.Combine(Path.GetTempPath(), $"task1-filtertest-{Guid.NewGuid():N}.bin");
        File.WriteAllText(temp, "hello");
        try
        {
            FileHosterClient tiny = new("Rapidgator", Protocol.Http);  // any registered name; pipeline below maps "Tiny"
            FileHosterClient big = new("BRupload", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [temp],
                FileHosters = new()
                {
                    { tiny, new FileHosterLoginDto { FileHosterName = "Rapidgator" } },
                    { big,  new FileHosterLoginDto { FileHosterName = "BRupload"   } },
                },
            };
            Package package = new(options);

            StubRegistry registry = new(new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Rapidgator"] = 0,    // any non-empty file is over the cap → filtered out
                ["BRupload"] = null, // no cap declared → kept
            });
            Mock<IAppLogger> logger = new();

            package.AddPackageFiles(registry, logger.Object);

            // Only one PackageFile survives — the (file, BRupload) pair.
            PackageFile only = Assert.Single(package);
            Assert.Equal("BRupload", only.FileHoster.Name);

            // And the skip was logged at Status level so it's traceable in the Logs tab.
            logger.Verify(
                l => l.Log(
                    package,
                    LogType.Status,
                    It.Is<string>(s => s.Contains("Skipping queueing", StringComparison.Ordinal)
                                       && s.Contains("Rapidgator", StringComparison.Ordinal)),
                    It.IsAny<HttpTransaction?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>()),
                Times.Once);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void AddPackageFiles_OversizeSkipMessage_QuotesTheCapInItsRoundUnit()
    {
        // The skip message quotes the hoster's DECLARED cap, which must read the way the host
        // states it (a 2,000-byte cap is "2 kB", not "1.95 KiB") — the same roundness rule as the
        // wizard's "Max file size" column. The MEASURED file size stays binary: it is nobody's
        // advertised figure.
        string temp = Path.Combine(Path.GetTempPath(), $"task1-capunit-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(temp, new byte[3000]);
        try
        {
            FileHosterClient capped = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [temp],
                FileHosters = new()
                {
                    { capped, new FileHosterLoginDto { FileHosterName = "Rapidgator" } },
                },
            };
            Package package = new(options);

            StubRegistry registry = new(new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Rapidgator"] = 2_000,
            });
            Mock<IAppLogger> logger = new();

            package.AddPackageFiles(registry, logger.Object);

            Assert.Empty(package);
            logger.Verify(
                l => l.Log(
                    package,
                    LogType.Status,
                    It.Is<string>(s => s.Contains("2 kB per-file limit", StringComparison.Ordinal)
                                       && s.Contains("2.93 KiB exceeds", StringComparison.Ordinal)),
                    It.IsAny<HttpTransaction?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>()),
                Times.Once);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void AddPackageFiles_AccountAtQuota_SkipsFile()
    {
        // The (file, hoster) pair is skipped when the account's persisted storage usage
        // (StorageUsedBytes + StorageQuotaBytes on the DTO) would be exceeded by the new
        // file. Mirrors the per-file-size filter just above this test — the wizard now
        // doesn't queue work that would obviously fail at the hoster.
        string temp = Path.Combine(Path.GetTempPath(), $"task1-quota-{Guid.NewGuid():N}.bin");
        File.WriteAllText(temp, "hello world!"); // 12 bytes
        try
        {
            FileHosterClient full = new("Rapidgator", Protocol.Http); // 11 of 20 bytes used → 12-byte file overflows
            FileHosterClient room = new("BRupload", Protocol.Http);   // 0 of 100 bytes used → fits
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [temp],
                FileHosters = new()
                {
                    {
                        full,
                        new FileHosterLoginDto { FileHosterName = "Rapidgator", StorageUsedBytes = 11L, StorageQuotaBytes = 20L }
                    },
                    {
                        room,
                        new FileHosterLoginDto { FileHosterName = "BRupload", StorageUsedBytes = 0L, StorageQuotaBytes = 100L }
                    },
                },
            };
            Package package = new(options);

            // No MaxFileSize cap on either hoster — only the quota filter should fire.
            StubRegistry registry = new(new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Rapidgator"] = null,
                ["BRupload"] = null,
            });
            Mock<IAppLogger> logger = new();

            package.AddPackageFiles(registry, logger.Object);

            PackageFile only = Assert.Single(package);
            Assert.Equal("BRupload", only.FileHoster.Name);

            // And the skip is logged with a "quota" hint so a user reading the Logs
            // tab can tell quota-skip apart from size-skip.
            logger.Verify(
                l => l.Log(
                    package,
                    LogType.Status,
                    It.Is<string>(s => s.Contains("Skipping queueing", StringComparison.Ordinal)
                                       && s.Contains("Rapidgator", StringComparison.Ordinal)
                                       && s.Contains("quota", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<HttpTransaction?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>()),
                Times.Once);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void AddPackageFiles_NoRegistryArg_BehavesLikeBefore()
    {
        // Parameterless call must still create the full cross-product — guarantees existing
        // tests / callers that don't know about the filter aren't quietly broken.
        string temp = Path.Combine(Path.GetTempPath(), $"task1-noarg-{Guid.NewGuid():N}.bin");
        File.WriteAllText(temp, "x");
        try
        {
            FileHosterClient a = new("Rapidgator", Protocol.Http);
            FileHosterClient b = new("BRupload", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [temp],
                FileHosters = new()
                {
                    { a, new FileHosterLoginDto { FileHosterName = "Rapidgator" } },
                    { b, new FileHosterLoginDto { FileHosterName = "BRupload"   } },
                },
            };
            Package package = new(options);

            package.AddPackageFiles();  // legacy parameterless

            Assert.Equal(2, package.Count());
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Tiny IFileHosterRegistry stub backed by a name-to-cap map; the only
    /// pipeline surface the filter consults is <c>MaxFileSize</c>.</summary>
    private sealed class StubRegistry(Dictionary<string, long?> capsByName) : CSUploader.Upload.Pipeline.IFileHosterRegistry
    {
        public CSUploader.Upload.Pipeline.IFileHosterPipeline? Find(string hosterName)
            => capsByName.TryGetValue(hosterName, out long? cap)
                ? new StubPipeline(hosterName, cap)
                : null;

        private sealed class StubPipeline(string name, long? maxFileSize) : CSUploader.Upload.Pipeline.IFileHosterPipeline
        {
            public string Name => name;
            public bool RequiresHashingBeforeUpload => false;
            public bool RequiresHashingAfterUpload => false;
            public long? MaxFileSize => maxFileSize;
            public int? MaxFilesPerPackage => null;
            public IAsyncEnumerable<CSUploader.Upload.Pipeline.UploadEvent> RunAsync(CSUploader.Upload.Pipeline.AttemptContext ctx, CancellationToken ct) => throw new NotImplementedException();
            public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct) => throw new NotImplementedException();
        }
    }

    [Fact]
    public void Path_NoFiles_ReturnsNull()
    {
        Package package = new(new PackageOptions { Title = "t", FileHosters = [] });

        Assert.Null(package.Path);
    }

    [Fact]
    public void Path_AllFilesShareDirectory_ReturnsThatDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.bin");
        string b = Path.Combine(dir, "b.bin");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        try
        {
            PackageOptions options = new()
            {
                Title = "t",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [a, b],
                FileHosters = new() { { new FileHosterClient("Rapidgator", Protocol.Http), new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = new(options);
            package.AddPackageFiles();

            Assert.Equal(dir, package.Path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Path_FilesShareAncestor_ReturnsLongestCommonParent()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        string topFile = Path.Combine(root, "top.bin");
        string subFile = Path.Combine(sub, "child.bin");
        File.WriteAllText(topFile, "x");
        File.WriteAllText(subFile, "y");
        try
        {
            PackageOptions options = new()
            {
                Title = "t",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [topFile, subFile],
                FileHosters = new() { { new FileHosterClient("Rapidgator", Protocol.Http), new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = new(options);
            package.AddPackageFiles();

            Assert.Equal(root, package.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Status_OneFailedWhileOthersUploading_ReturnsUploadingNotFailed()
    {
        // Regression: the rollup used to short-circuit to Failed on any failed file
        // even when siblings were still uploading. Package should reflect the
        // in-progress work until every file has reached a terminal state.
        Package package = MakePackageWithFiles(
            FileState.Uploading, FileState.Failed, FileState.Hashing, FileState.Completed);

        Assert.Equal(FileState.Uploading, package.Status);
    }

    [Fact]
    public void Status_AllTerminalMixCompletedAndFailed_ReturnsCompletedWithErrors()
    {
        Package package = MakePackageWithFiles(FileState.Completed, FileState.Failed, FileState.Completed);

        Assert.Equal(FileState.CompletedWithErrors, package.Status);
    }

    [Fact]
    public void Status_AllFailed_ReturnsFailed()
    {
        Package package = MakePackageWithFiles(FileState.Failed, FileState.Failed);

        Assert.Equal(FileState.Failed, package.Status);
    }

    [Fact]
    public void Status_AllCompleted_ReturnsCompleted()
    {
        Package package = MakePackageWithFiles(FileState.Completed, FileState.Completed);

        Assert.Equal(FileState.Completed, package.Status);
    }

    [Fact]
    public void Progress_IsByteWeightedAcrossAllFiles_IncludingQueued()
    {
        // Regression: package Progress was an UNWEIGHTED average of per-file percentages, and LINQ's
        // Average over double? silently skips queued files (their Progress is null) — so a package with
        // one done file (100%) and one half-done file (50%) plus a big queued file read 75%, hiding the
        // queued work. Byte-weighting (bytes uploaded / total size) reports the true 45%.
        Package package = MakePackageWithFiles(FileState.Completed, FileState.Uploading, FileState.Idle);
        PackageFile[] files = [.. package];
        Configure(files[0], size: 800 * Mib, loaded: 800 * Mib, remaining: null, progress: 100, speed: null);
        Configure(files[1], size: 200 * Mib, loaded: 100 * Mib, remaining: 100 * Mib, progress: 50, speed: 10 * Mib);
        Configure(files[2], size: 1000 * Mib, loaded: null, remaining: 1000 * Mib, progress: null, speed: null);

        Assert.NotNull(package.Progress);
        Assert.Equal(45.0, package.Progress!.Value, 3); // 900 MiB / 2000 MiB — NOT avg(100, 50) = 75
    }

    [Fact]
    public void TimeRemaining_CoversQueuedFiles_NotJustActiveUploads()
    {
        // Regression: ETA summed the remaining bytes of only the currently-Uploading files, so it timed
        // the active batch and ignored everything still queued. It now mirrors the overview bar:
        // total remaining bytes / aggregate speed.
        Package package = MakePackageWithFiles(FileState.Completed, FileState.Uploading, FileState.Idle);
        PackageFile[] files = [.. package];
        Configure(files[0], size: 800 * Mib, loaded: 800 * Mib, remaining: null, progress: 100, speed: null);
        Configure(files[1], size: 200 * Mib, loaded: 100 * Mib, remaining: 100 * Mib, progress: 50, speed: 10 * Mib);
        Configure(files[2], size: 1000 * Mib, loaded: null, remaining: 1000 * Mib, progress: null, speed: null);

        // 1100 MiB remaining (active 100 + queued 1000) at 10 MiB/s → 110 s, not the ~10 s the old formula
        // gave by counting only the active file's 100 MiB.
        Assert.NotNull(package.TimeRemaining);
        Assert.Equal(110.0, package.TimeRemaining!.Value.TotalSeconds, 0);
    }

    private const long Mib = 1024 * 1024;

    private static void Configure(PackageFile pf, long size, long? loaded, long? remaining, double? progress, long? speed)
    {
        pf.Size = size;
        pf.BytesLoaded = loaded;
        pf.BytesRemaining = remaining;
        pf.Progress = progress;
        pf.Speed = speed;
    }

    [Fact]
    public void AccountDisplay_RegisteredAccount_ShowsUsernameOnPackageFileAndCopyColumn()
    {
        Package package = MakePackageWithLogin(
            new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "bob@example.com", IsAnonymous = false });

        Assert.Equal("bob@example.com", package.AccountDisplay);
        PackageFile file = Assert.Single(package);
        Assert.Equal("bob@example.com", file.AccountDisplay);
        // Copy-column maps "Account" -> AccountDisplay for the Uploads tab.
        Assert.Equal("bob@example.com", ColumnValueExtractor.Extract(file, "Account", isUploadsTab: true));
    }

    [Fact]
    public void AccountDisplay_Anonymous_ShowsLocalizedLabel_EvenWhenUsernameNull()
    {
        // Reloaded anonymous packages carry Username=null; AccountDisplay must still show the
        // localized "(anonymous)" label rather than blank (the IsAnonymous-branch gotcha).
        Package package = MakePackageWithLogin(
            new FileHosterLoginDto { FileHosterName = "GigaPeta", Username = null, IsAnonymous = true });

        string expected = Localizer.Instance["Wizard_Step2_AccountAnonymous"];
        Assert.Equal(expected, package.AccountDisplay);
        Assert.Equal(expected, Assert.Single(package).AccountDisplay);
    }

    [Fact]
    public void AccountDisplay_AccountWithNoUsernameNoKey_ShowsEmptyNotAnonymous()
    {
        // A real account with neither a captured username nor an API key shows blank — it is NOT
        // anonymous, so it must not show "(anonymous)".
        Package package = MakePackageWithLogin(
            new FileHosterLoginDto { FileHosterName = "HitFile", Username = null, IsAnonymous = false });

        Assert.Equal(string.Empty, Assert.Single(package).AccountDisplay);
        Assert.Equal(string.Empty, package.AccountDisplay);
    }

    [Fact]
    public void AccountDisplay_KeyOnlyAccount_ShowsMaskedApiKey()
    {
        // API-key hosters (Ufile/NitroFlare) capture only a key, no username — the Uploads grid's
        // Account column shows a partly-masked key so several key-only accounts stay distinguishable
        // instead of rendering as identical blank cells.
        Package package = MakePackageWithLogin(new FileHosterLoginDto
        {
            FileHosterName = "Ufile",
            Username = null,
            IsAnonymous = false,
            ApiKey = "12GHte7890abcdef",
        });

        Assert.Equal("12GHte**", Assert.Single(package).AccountDisplay);
        Assert.Equal("12GHte**", package.AccountDisplay);
    }

    private static Package MakePackageWithLogin(FileHosterLoginDto login)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"csu-acct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, "f.bin");
        File.WriteAllText(p, "x");

        PackageOptions options = new()
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = [p],
            FileHosters = new() { { new FileHosterClient(login.FileHosterName ?? "Rapidgator", Protocol.Http), login } },
        };
        Package package = new(options);
        package.AddPackageFiles();
        return package;
    }

    [Fact]
    public void StartedDate_ReturnsEarliestStartedFile_OrNullWhenNoneStarted()
    {
        // The Uploads grid's "Started" column binds StartedDate on both package and file rows; the
        // package aggregate must resolve (earliest started file) so it doesn't log a missing-accessor
        // binding error, and be null before any file has started.
        Package package = MakePackageWithFiles(FileState.Uploading, FileState.Idle, FileState.Completed);
        PackageFile[] files = [.. package];
        foreach (PackageFile f in files)
        {
            f.StartedDate = null;
        }

        Assert.Null(package.StartedDate);

        DateTime early = new(2026, 7, 18, 10, 0, 0, DateTimeKind.Local);
        files[0].StartedDate = early.AddMinutes(5);
        files[2].StartedDate = early; // earliest of the started files
        // files[1] never started (null) — ignored.

        Assert.Equal(early, package.StartedDate);
    }

    private static Package MakePackageWithFiles(params FileState[] fileStates)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"csu-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        List<string> paths = [];
        for (int i = 0; i < fileStates.Length; i++)
        {
            string p = Path.Combine(dir, $"f{i}.bin");
            File.WriteAllText(p, "x");
            paths.Add(p);
        }

        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        PackageOptions options = new()
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = paths,
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
        };
        Package package = new(options);
        package.AddPackageFiles();

        int j = 0;
        foreach (PackageFile pf in package)
        {
            pf.State = fileStates[j++];
        }

        return package;
    }

    [Theory]
    [InlineData(new string?[] { @"C:\X\Y", @"C:\X\Z" }, @"C:\X")]
    [InlineData(new string?[] { @"C:\X\Y\Inner", @"C:\X\Y\Other" }, @"C:\X\Y")]
    [InlineData(new string?[] { @"C:\X" }, @"C:\X")]
    [InlineData(new string?[] { @"C:\X\Y", @"D:\Z" }, null)]
    [InlineData(new string?[] { @"C:\X\Y", null }, @"C:\X\Y")]
    [InlineData(new string?[] { null, null }, null)]
    [InlineData(new string?[] { }, null)]
    public void LongestCommonDirectory_Cases(string?[] inputs, string? expected) => Assert.Equal(expected, Package.LongestCommonDirectory(inputs));
}
