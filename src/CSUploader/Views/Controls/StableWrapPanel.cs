// <copyright file="StableWrapPanel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;

namespace CSUploader.Views.Controls;

/// <summary>
/// Lays its children out left to right and wraps them where a horizontal <see cref="WrapPanel"/> (default
/// settings) would — except that while <see cref="HoldWidths"/> is on, a child that gets narrower keeps the
/// widest width it has had. This is the Upload Overview's stats bar. Its values change width on every refresh
/// tick (a byte count that loses a trailing zero is a digit shorter), and in a <see cref="WrapPanel"/> a bar
/// that only just overflowed its line kept pulling its last stat up and pushing it back down with them,
/// changing the bar's height each time. Held, a shorter value leaves a gap after itself instead. A child that
/// gets wider still widens at once, so the bar may wrap further as values grow but never flips back; turning
/// the hold off lays every child out at its current width again.
/// </summary>
public sealed class StableWrapPanel : Panel
{
    /// <summary>Defines the <see cref="HoldWidths"/> property.</summary>
    public static readonly StyledProperty<bool> HoldWidthsProperty =
        AvaloniaProperty.Register<StableWrapPanel, bool>(nameof(HoldWidths));

    // The widest width each visible child has measured since the hold began. Emptied the moment the hold is
    // turned off — not on the next layout pass, which a hidden panel never gets — so nothing held during one
    // upload run carries over into the next. A child removed mid-hold keeps its entry until then; the stats
    // bar's children are fixed.
    private readonly Dictionary<Control, double> _heldWidths = [];

    static StableWrapPanel()
    {
        AffectsMeasure<StableWrapPanel>(HoldWidthsProperty);
    }

    /// <summary>
    /// Whether children keep the widest width they have measured. Turning it off drops the held widths and lays
    /// every child out at its current width.
    /// </summary>
    public bool HoldWidths
    {
        get => GetValue(HoldWidthsProperty);
        set => SetValue(HoldWidthsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HoldWidthsProperty && !change.GetNewValue<bool>())
        {
            _heldWidths.Clear();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        bool hold = HoldWidths;
        double lineWidth = 0;
        double lineHeight = 0;
        double panelWidth = 0;
        double panelHeight = 0;

        foreach (Control child in Children)
        {
            child.Measure(availableSize);
            double width = hold ? HoldWidth(child) : child.DesiredSize.Width;
            double height = child.DesiredSize.Height;
            if (Overflows(lineWidth + width, availableSize.Width))
            {
                panelWidth = Math.Max(lineWidth, panelWidth);
                panelHeight += lineHeight;
                lineWidth = width;
                lineHeight = height;
            }
            else
            {
                lineWidth += width;
                lineHeight = Math.Max(height, lineHeight);
            }
        }

        return new Size(Math.Max(lineWidth, panelWidth), panelHeight + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int lineStart = 0;
        double lineWidth = 0;
        double lineHeight = 0;
        double lineTop = 0;

        for (int i = 0; i < Children.Count; i++)
        {
            double width = SlotWidth(Children[i]);
            double height = Children[i].DesiredSize.Height;
            if (Overflows(lineWidth + width, finalSize.Width))
            {
                ArrangeLine(lineStart, i, lineTop, lineHeight);
                lineTop += lineHeight;
                lineStart = i;
                lineWidth = width;
                lineHeight = height;
            }
            else
            {
                lineWidth += width;
                lineHeight = Math.Max(height, lineHeight);
            }
        }

        ArrangeLine(lineStart, Children.Count, lineTop, lineHeight);
        return finalSize;
    }

    /// <summary>
    /// Avalonia's <c>MathUtilities.GreaterThan</c>, which <see cref="WrapPanel"/> breaks its lines with — internal
    /// to Avalonia, so restated here. The tolerance keeps a line that overflows by a floating-point hair (widths
    /// in 0.8 px steps at 125% scaling sum to 300.00000000000006 instead of 300), so a line breaks exactly
    /// where a <see cref="WrapPanel"/> would break it.
    /// </summary>
    private static bool Overflows(double lineWidth, double available)
        => lineWidth > available
            && lineWidth - available >= (Math.Abs(lineWidth) + Math.Abs(available) + 10.0) * 2.220446049250313E-16;

    /// <summary>
    /// Records a just-measured child's width under the hold and returns the width to lay it out at: the widest it
    /// has measured. A hidden child gives its held width up, so it comes back at whatever width it has then.
    /// </summary>
    private double HoldWidth(Control child)
    {
        double width = child.DesiredSize.Width;
        if (!child.IsVisible)
        {
            _heldWidths.Remove(child);
            return width;
        }

        if (_heldWidths.TryGetValue(child, out double held) && held > width)
        {
            width = held;
        }

        _heldWidths[child] = width;
        return width;
    }

    /// <summary>The width the last measure pass laid <paramref name="child"/> out at.</summary>
    private double SlotWidth(Control child)
        => _heldWidths.TryGetValue(child, out double held) ? held : child.DesiredSize.Width;

    private void ArrangeLine(int start, int end, double top, double height)
    {
        double left = 0;
        for (int i = start; i < end; i++)
        {
            Control child = Children[i];
            double width = SlotWidth(child);
            child.Arrange(new Rect(left, top, width, height));
            left += width;
        }
    }
}
