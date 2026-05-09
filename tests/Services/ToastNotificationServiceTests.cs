// <copyright file="ToastNotificationServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Windows;
using CSUploader.Dal;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Moq;

namespace CSUploader.Tests.Services;

public class ToastNotificationServiceTests
{
    private const double ToastHeight = 80;

    private readonly AppSettings _settings = new();
    private readonly FakeToastWindowFactory _factory = new();
    private readonly Rect _workArea = new(0, 0, 1920, 1040);

    private ToastNotificationService CreateService()
        => new(_settings, _factory, () => _workArea, activate: () => { }, dispatchToUi: action => action());

    [Fact]
    public void ShowFileCompleted_WhenSettingDisabled_DoesNotCreateToast()
    {
        _settings.ShowCompletionToasts = false;
        ToastNotificationService service = CreateService();

        service.ShowFileCompleted(BuildFile("foo.zip"));

        Assert.Empty(_factory.Created);
    }

    [Fact]
    public void ShowFileCompleted_WhenSettingEnabled_CreatesAToast()
    {
        _settings.ShowCompletionToasts = true;
        ToastNotificationService service = CreateService();

        service.ShowFileCompleted(BuildFile("foo.zip"));

        Assert.Single(_factory.Created);
        FakeToastHost host = _factory.Created[0];
        Assert.Equal(_workArea.Right - ToastNotificationService.ToastWidth - ToastNotificationService.Margin, host.Left);
        Assert.Equal(_workArea.Bottom - ToastNotificationService.Margin - ToastHeight, host.Top);
    }

    [Fact]
    public void ShowFileCompleted_ThreeInARow_StacksUpward()
    {
        _settings.ShowCompletionToasts = true;
        ToastNotificationService service = CreateService();

        service.ShowFileCompleted(BuildFile("a.zip"));
        service.ShowFileCompleted(BuildFile("b.zip"));
        service.ShowFileCompleted(BuildFile("c.zip"));

        Assert.Equal(3, _factory.Created.Count);
        double bottomTop = _workArea.Bottom - ToastNotificationService.Margin - ToastHeight;
        Assert.Equal(bottomTop, _factory.Created[0].Top);
        Assert.Equal(bottomTop - ToastHeight, _factory.Created[1].Top);
        Assert.Equal(bottomTop - 2 * ToastHeight, _factory.Created[2].Top);
    }

    [Fact]
    public void ClosingMiddleToast_ReflowsRemainingToastsDown()
    {
        _settings.ShowCompletionToasts = true;
        ToastNotificationService service = CreateService();

        service.ShowFileCompleted(BuildFile("a.zip"));
        service.ShowFileCompleted(BuildFile("b.zip"));
        service.ShowFileCompleted(BuildFile("c.zip"));

        // Close the middle toast (b.zip).
        _factory.Created[1].RaiseClosed();

        // The remaining toasts (a, c) should occupy the two bottom slots.
        double bottomTop = _workArea.Bottom - ToastNotificationService.Margin - ToastHeight;
        Assert.Equal(bottomTop, _factory.Created[0].Top);
        Assert.Equal(bottomTop - ToastHeight, _factory.Created[2].Top);
    }

    [Fact]
    public void ShowPackageCompleted_PassesCountsAndPackageNameToBody()
    {
        _settings.ShowCompletionToasts = true;
        ToastNotificationService service = CreateService();
        Package pkg = BuildPackage("MyPack");

        service.ShowPackageCompleted(pkg, succeeded: 3, total: 4);

        Assert.Single(_factory.Created);
        ToastViewModel vm = _factory.Created[0].ViewModel;
        Assert.Contains("3", vm.Message, StringComparison.Ordinal);
        Assert.Contains("4", vm.Message, StringComparison.Ordinal);
        Assert.Contains("MyPack", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToastActivateCommand_InvokesActivateCallback()
    {
        _settings.ShowCompletionToasts = true;
        bool activated = false;
        ToastNotificationService service = new(_settings, _factory, () => _workArea, activate: () => activated = true, dispatchToUi: action => action());

        service.ShowFileCompleted(BuildFile("foo.zip"));
        _factory.Created[0].ViewModel.ActivateCommand.Execute(null);

        Assert.True(activated);
    }

    private static PackageFile BuildFile(string name)
    {
        // Real PackageFile needs a Package + login + hoster client. Construct the
        // smallest valid graph; the toast service only reads the file's Name.
        Package pkg = BuildPackage("test-pkg");
        FileHosterClient hoster = new("Rapidgator", CSUploader.Lib.Net.Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", Username = "u", Password = "p" };
        // Use a temp file so FileInfo doesn't throw — toast service never reads its size.
        string tempPath = Path.Combine(Path.GetTempPath(), name);
        if (!File.Exists(tempPath)) File.WriteAllText(tempPath, "x");
        PackageFile file = new(pkg, tempPath, hoster, login);
        return file;
    }

    private static Package BuildPackage(string name) => new(new PackageOptions { Title = name });

    private sealed class FakeToastWindowFactory : IToastWindowFactory
    {
        public List<FakeToastHost> Created { get; } = new();

        public IToastHost Create(ToastViewModel viewModel)
        {
            FakeToastHost host = new(viewModel);
            Created.Add(host);
            return host;
        }
    }

    private sealed class FakeToastHost : IToastHost
    {
        public FakeToastHost(ToastViewModel viewModel) => ViewModel = viewModel;

        public ToastViewModel ViewModel { get; }
        public double Height => ToastHeight;
        public double Top { get; set; }
        public double Left { get; set; }
        public event EventHandler? Closed;

        public void Show() { /* no-op for tests */ }

        public void Close() => RaiseClosed();

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }
}
