// <copyright file="SpeedGraph.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CSUploader.Views.Controls;

/// <summary>
/// A tiny filled sparkline of recent speed samples (a JDownloader-style speedometer). <see cref="Samples"/> are
/// drawn oldest→newest left→right, scaled so the peak sample touches near the top; an all-zero window draws
/// nothing. Purely presentational — the Uploads toolbar binds <see cref="Samples"/> to the view-model's rolling
/// history, which hands it a fresh array each refresh tick.
/// </summary>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<System.Collections.Generic.IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<SpeedGraph, System.Collections.Generic.IReadOnlyList<double>?>(nameof(Samples));

    /// <summary>Stroke of the top line (also the source of the auto-derived translucent area fill).</summary>
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(LineBrush), Brushes.SteelBlue);

    /// <summary>Area fill under the line. When null it is derived from <see cref="LineBrush"/> at low opacity.</summary>
    public static readonly StyledProperty<IBrush?> AreaBrushProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(AreaBrush));

    static SpeedGraph()
    {
        AffectsRender<SpeedGraph>(SamplesProperty, LineBrushProperty, AreaBrushProperty);
    }

    public System.Collections.Generic.IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? AreaBrush
    {
        get => GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        System.Collections.Generic.IReadOnlyList<double>? samples = Samples;
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (samples is null || samples.Count < 2 || w <= 0 || h <= 0)
        {
            return;
        }

        double max = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i] > max)
            {
                max = samples[i];
            }
        }

        if (max <= 0)
        {
            return; // all-zero window → draw nothing (idle)
        }

        double last = samples.Count - 1;
        double headroom = 0.12 * h; // keep the peak just clear of the top edge

        Point At(int i)
        {
            double x = w * i / last;
            double y = h - (samples[i] / max * (h - headroom));
            return new Point(x, y);
        }

        // Filled area under the line, closed down to the baseline.
        IBrush? area = AreaBrush;
        if (area is null && LineBrush is ISolidColorBrush solid)
        {
            area = new SolidColorBrush(solid.Color, 0.20);
        }

        if (area is not null)
        {
            StreamGeometry fill = new();
            using (StreamGeometryContext g = fill.Open())
            {
                g.BeginFigure(new Point(0, h), isFilled: true);
                g.LineTo(At(0));
                for (int i = 1; i < samples.Count; i++)
                {
                    g.LineTo(At(i));
                }

                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }

            context.DrawGeometry(area, null, fill);
        }

        StreamGeometry line = new();
        using (StreamGeometryContext g = line.Open())
        {
            g.BeginFigure(At(0), isFilled: false);
            for (int i = 1; i < samples.Count; i++)
            {
                g.LineTo(At(i));
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(LineBrush ?? Brushes.SteelBlue, 1.25), line);
    }
}
