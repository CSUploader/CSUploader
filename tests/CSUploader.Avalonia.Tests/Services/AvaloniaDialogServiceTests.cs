// <copyright file="AvaloniaDialogServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Headless.XUnit;
using CSUploader.Dal;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Views;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// Phase 4 Task 3: the opt-out plumbing <see cref="AvaloniaDialogService"/> inherits from
/// <see cref="DialogServiceBase"/> (suppression lookup + "don't ask again" set mutation), plus the real
/// service's headless behavior. The base owns the suppression flow; the head implements only
/// <c>ShowOptOutConfirmationCoreAsync</c>, so the plumbing is exercised through a fake base whose core
/// returns a canned outcome (no window), and the real service is checked for the load-bearing headless
/// property: a suppressed key returns <c>true</c> WITHOUT opening the ownerless message box that a
/// non-suppressed call would (in headless the owner resolver is null, so the core would take the
/// ownerless <c>Show()</c> branch).
/// </summary>
public class AvaloniaDialogServiceTests
{
    [AvaloniaFact]
    public void RealService_SuppressedKey_ShortCircuitsWithoutOpeningWindow()
    {
        var settings = new AppSettings();
        settings.SuppressedConfirmations.Add("csu-key");
        var service = new AvaloniaDialogService(settings, StubRepository(), Mock.Of<ITrayIconService>());

        Task<bool> task = service.ShowOptOutConfirmationAsync("csu-key", "Remove this account?");

        // Suppressed → the base returns before ShowOptOutConfirmationCoreAsync, which in headless (null
        // owner) would open an ownerless MessageBoxWindow and await its Closed. A completed task with no
        // await pending is the proxy for "no window opened".
        Assert.True(task.IsCompletedSuccessfully);
        Assert.True(task.Result);
    }

    [AvaloniaFact]
    public async Task Confirmed_WithDontAskAgain_AddsKeyToSuppressed()
    {
        var settings = new AppSettings();
        var service = new FakeOptOutService(settings, StubRepository(), confirmed: true, dontAskAgain: true);

        bool result = await service.ShowOptOutConfirmationAsync("csu-key", "Remove this account?");

        Assert.True(result);
        Assert.Contains("csu-key", settings.SuppressedConfirmations);
        Assert.Equal(1, service.CoreCalls);
    }

    [AvaloniaFact]
    public async Task Declined_DoesNotSuppress()
    {
        var settings = new AppSettings();
        var service = new FakeOptOutService(settings, StubRepository(), confirmed: false, dontAskAgain: true);

        bool result = await service.ShowOptOutConfirmationAsync("csu-key", "Remove this account?");

        Assert.False(result);
        Assert.DoesNotContain("csu-key", settings.SuppressedConfirmations);
    }

    [AvaloniaFact]
    public async Task Confirmed_WithoutDontAskAgain_DoesNotSuppress()
    {
        var settings = new AppSettings();
        var service = new FakeOptOutService(settings, StubRepository(), confirmed: true, dontAskAgain: false);

        bool result = await service.ShowOptOutConfirmationAsync("csu-key", "Remove this account?");

        Assert.True(result);
        Assert.DoesNotContain("csu-key", settings.SuppressedConfirmations);
    }

    [AvaloniaFact]
    public async Task SuppressedKey_SkipsCore()
    {
        var settings = new AppSettings();
        settings.SuppressedConfirmations.Add("csu-key");
        var service = new FakeOptOutService(settings, StubRepository(), confirmed: false, dontAskAgain: false);

        bool result = await service.ShowOptOutConfirmationAsync("csu-key", "Remove this account?");

        Assert.True(result);
        Assert.Equal(0, service.CoreCalls); // the base short-circuited before the core
    }

    [Fact]
    public void ToOptOutResult_MapsConfirmedThenDontAskAgain_NotTransposed()
    {
        // Pins ShowOptOutConfirmationCoreAsync's MessageBoxOutcome→tuple projection via the extracted
        // ToOptOutResult seam. The real core can't be driven end-to-end headlessly — it opens an ownerless
        // MessageBoxWindow (the headless lifetime resolves a null owner) that no headless API can reach to
        // click — and AvaloniaDialogService is sealed, so its protected core isn't reachable via a test
        // subclass either. Confirmed≠DontAskAgain so a transposition mutant — return (DontAskAgain,
        // Confirmed) — flips both fields and fails here.
        (bool Confirmed, bool DontAskAgain) result =
            AvaloniaDialogService.ToOptOutResult(new MessageBoxOutcome(Confirmed: true, DontAskAgain: false));

        Assert.True(result.Confirmed);
        Assert.False(result.DontAskAgain);
    }

    // A SettingRepository whose factory yields no context; the base's fire-and-forget persist NREs
    // internally and is swallowed (best-effort), so the in-memory SuppressedConfirmations mutation —
    // which the assertions target — still lands. No real DB needed for these plumbing tests.
    private static SettingRepository StubRepository()
        => new(Mock.Of<IDbContextFactory<CSUploaderDbContext>>());

    // Minimal DialogServiceBase whose raw opt-out core returns a canned outcome with no UI, so the
    // base's suppression lookup + set mutation are exercised deterministically (the real
    // AvaloniaDialogService's core opens a window unreachable in a headless session).
    private sealed class FakeOptOutService(AppSettings settings, SettingRepository repository, bool confirmed, bool dontAskAgain)
        : DialogServiceBase(settings, repository)
    {
        public int CoreCalls { get; private set; }

        protected override Task<(bool Confirmed, bool DontAskAgain)> ShowOptOutConfirmationCoreAsync(string message, string title)
        {
            CoreCalls++;
            return Task.FromResult((confirmed, dontAskAgain));
        }
    }
}
