// <copyright file="UploadNotificationListenerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Services;

public class UploadNotificationListenerTests : IDisposable
{
    private readonly UploadScheduler _scheduler;
    private readonly Mock<IToastNotificationService> _toasts = new();
    private readonly UploadNotificationListener _listener;

    public UploadNotificationListenerTests()
    {
        AppSettings settings = new();
        _scheduler = new UploadScheduler(
            settings,
            BuildAttemptRunner(),
            Mock.Of<IAppLogger>(),
            new CSUploader.Lib.Crypto.HashingService(),
            new DefaultFileHosterRegistry([]));

        _listener = new UploadNotificationListener(_scheduler, _toasts.Object);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Completed_FiresShowFileCompleted()
    {
        Package pkg = BuildPackage("p", fileCount: 1);
        PackageFile file = pkg.First();
        file.State = FileState.Completed;

        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(file, FileState.Uploading, FileState.Completed));

        _toasts.Verify(t => t.ShowFileCompleted(file), Times.Once);
    }

    [Fact]
    public void NonTerminalState_FiresNothing()
    {
        Package pkg = BuildPackage("p", fileCount: 1);
        PackageFile file = pkg.First();

        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(file, FileState.Idle, FileState.UploadQueued));
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(file, FileState.UploadQueued, FileState.Uploading));

        _toasts.Verify(t => t.ShowFileCompleted(It.IsAny<PackageFile>()), Times.Never);
        _toasts.Verify(t => t.ShowPackageCompleted(It.IsAny<Package>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void AllFilesCompleted_FiresPackageSummaryOnce()
    {
        Package pkg = BuildPackage("p", fileCount: 4);
        PackageFile[] files = pkg.ToArray();

        foreach (PackageFile f in files)
        {
            f.State = FileState.Completed;
            _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(f, FileState.Uploading, FileState.Completed));
        }

        _toasts.Verify(t => t.ShowPackageCompleted(pkg, 4, 4), Times.Once);
    }

    [Fact]
    public void MixedSuccessAndFailure_FiresPackageSummaryWithCorrectCounts()
    {
        Package pkg = BuildPackage("p", fileCount: 4);
        PackageFile[] files = pkg.ToArray();

        files[0].State = FileState.Completed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[0], FileState.Uploading, FileState.Completed));
        files[1].State = FileState.Completed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[1], FileState.Uploading, FileState.Completed));
        files[2].State = FileState.Completed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[2], FileState.Uploading, FileState.Completed));
        files[3].State = FileState.Failed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[3], FileState.Uploading, FileState.Failed));

        _toasts.Verify(t => t.ShowPackageCompleted(pkg, 3, 4), Times.Once);
    }

    [Fact]
    public void AllFilesFailed_DoesNotFirePackageSummary()
    {
        Package pkg = BuildPackage("p", fileCount: 2);
        PackageFile[] files = pkg.ToArray();

        files[0].State = FileState.Failed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[0], FileState.Uploading, FileState.Failed));
        files[1].State = FileState.Failed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[1], FileState.Uploading, FileState.Failed));

        _toasts.Verify(t => t.ShowPackageCompleted(It.IsAny<Package>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void RetryAfterSummary_DoesNotFireSecondSummary()
    {
        Package pkg = BuildPackage("p", fileCount: 2);
        PackageFile[] files = pkg.ToArray();

        // First run — both files terminate, summary fires.
        files[0].State = FileState.Completed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[0], FileState.Uploading, FileState.Completed));
        files[1].State = FileState.Failed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[1], FileState.Uploading, FileState.Failed));

        // User retries the failed file; it eventually completes.
        files[1].State = FileState.Uploading;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[1], FileState.Failed, FileState.Uploading));
        files[1].State = FileState.Completed;
        _listener.HandleFileStateChanged(_scheduler, new FileStateChangedEventArgs(files[1], FileState.Uploading, FileState.Completed));

        _toasts.Verify(t => t.ShowPackageCompleted(pkg, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    private static Package BuildPackage(string name, int fileCount)
    {
        Package pkg = new(new PackageOptions { Title = name });
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        List<PackageFile> files = [];
        for (int i = 0; i < fileCount; i++)
        {
            string path = Path.Combine(Path.GetTempPath(), $"{name}_{i}.zip");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "x");
            }

            files.Add(new PackageFile(pkg, path, hoster, login));
        }
        pkg.AddPackageFiles(files.ToArray());
        return pkg;
    }

    private static AttemptRunner BuildAttemptRunner()
    {
        // Mirrors tests/Upload/PackageManagerSoftRemoveTests.BuildAttemptRunner.
        DefaultFileHosterRegistry registry = new([]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }
}
