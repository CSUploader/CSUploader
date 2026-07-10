// <copyright file="DipRect.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>Device-independent-pixel rectangle. ALL toast geometry (work area, host
/// Top/Left/Height) is in DIPs — the WPF head passes SystemParameters.WorkArea verbatim;
/// the Avalonia head must convert Screens' physical pixels via Screen.Scaling.</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}
