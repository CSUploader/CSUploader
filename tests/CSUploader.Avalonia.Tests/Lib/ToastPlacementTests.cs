// <copyright file="ToastPlacementTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia; // PixelPoint, PixelRect
using CSUploader.Lib.UI;
using CSUploader.Services; // DipRect

namespace CSUploader.Tests.Avalonia.Lib;

/// <summary>
/// The pure DIP&lt;-&gt;physical geometry the Avalonia toast head needs (Phase 7 Task 1). The WPF head
/// needed neither conversion (WPF Top/Left and SystemParameters.WorkArea are already DIPs); the Avalonia
/// head positions via Window.Position (physical px) and reads Screen.WorkingArea (physical px), so
/// <see cref="ToastPlacement"/> bridges them through Screen.Scaling.
/// </summary>
public class ToastPlacementTests
{
    [Fact]
    public void DipToPhysical_ScalesAndRounds()
    {
        // 100.4*1.5 = 150.6 -> 151 ; 200.6*1.5 = 300.9 -> 301
        Assert.Equal(new PixelPoint(151, 301), ToastPlacement.DipToPhysical(100.4, 200.6, 1.5));
    }

    [Fact]
    public void WorkAreaToDip_DividesByScaling()
    {
        DipRect d = ToastPlacement.WorkAreaToDip(new PixelRect(0, 0, 2880, 1620), 1.5);
        Assert.Equal(0, d.X);
        Assert.Equal(0, d.Y);
        Assert.Equal(1920, d.Width);
        Assert.Equal(1080, d.Height);
        Assert.Equal(1920, d.Right);   // X + Width
        Assert.Equal(1080, d.Bottom);  // Y + Height
    }

    [Fact]
    public void ZeroOrNegativeScaling_TreatedAsUnity()
    {
        Assert.Equal(new PixelPoint(10, 20), ToastPlacement.DipToPhysical(10, 20, 0));
        DipRect d = ToastPlacement.WorkAreaToDip(new PixelRect(5, 6, 100, 200), -1);
        Assert.Equal(100, d.Width);
    }
}
