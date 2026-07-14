// <copyright file="ToastPlacement.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia; // PixelPoint, PixelRect
using CSUploader.Services; // DipRect

namespace CSUploader.Lib.UI;

/// <summary>
/// Pure conversions between the toast service's DIP geometry (design: ALL toast geometry is in DIPs)
/// and Avalonia's physical-pixel Window.Position / Screen.WorkingArea. The WPF head needed neither
/// (WPF Top/Left and SystemParameters.WorkArea are already DIPs).
/// </summary>
public static class ToastPlacement
{
    /// <summary>Converts a screen's physical work area to DIPs (design DipRect / ToastNotificationService).</summary>
    public static DipRect WorkAreaToDip(PixelRect physicalWorkArea, double scaling)
    {
        if (scaling <= 0)
        {
            scaling = 1.0;
        }

        return new DipRect(
            physicalWorkArea.X / scaling,
            physicalWorkArea.Y / scaling,
            physicalWorkArea.Width / scaling,
            physicalWorkArea.Height / scaling);
    }

    /// <summary>Converts a DIP top-left to a physical PixelPoint for Window.Position.</summary>
    public static PixelPoint DipToPhysical(double dipLeft, double dipTop, double scaling)
    {
        if (scaling <= 0)
        {
            scaling = 1.0;
        }

        return new PixelPoint(
            (int)Math.Round(dipLeft * scaling),
            (int)Math.Round(dipTop * scaling));
    }
}
