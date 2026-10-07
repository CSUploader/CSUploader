// <copyright file="StableWrapPanelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using CSUploader.Views.Controls;

namespace CSUploader.Tests.Avalonia.Views.Controls;

/// <summary>
/// Pins <see cref="StableWrapPanel"/>, the Upload Overview's stats panel. Children are fixed-size borders on a
/// 300 px line, so every expected position is plain arithmetic and nothing depends on a font. A child that
/// changes width stands in for a stat whose value text got longer or shorter on a refresh tick.
/// </summary>
public class StableWrapPanelTests
{
    private const double LineWidth = 300;
    private const double ChildHeight = 20;

    [AvaloniaFact]
    public void NotHolding_WrapsLikeAWrapPanel_ChildrenFollowTheirCurrentWidths()
    {
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: false, 100, 100, 104);
        try
        {
            // 100 + 100 + 104 = 304 > 300: the third child starts a second line.
            Assert.Equal(new Point(0, 0), children[0].Bounds.Position);
            Assert.Equal(new Point(100, 0), children[1].Bounds.Position);
            Assert.Equal(new Point(0, 20), children[2].Bounds.Position);
            Assert.Equal(40, panel.Bounds.Height);

            // 96 + 100 + 104 = 300: an exact fit stays on the line, as it does in a WrapPanel.
            children[0].Width = 96;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(96, 0), children[1].Bounds.Position);
            Assert.Equal(new Point(196, 0), children[2].Bounds.Position);
            Assert.Equal(20, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WhileHolding_AChildThatShrinks_KeepsItsWidth_SoNothingAfterItMoves()
    {
        // The Upload Overview bug: the stats overflowed one line by a few pixels, so whenever a value lost a
        // digit the last stat jumped back up — and the whole bar changed height — until the digit returned.
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 104);
        try
        {
            Assert.Equal(new Point(0, 20), children[2].Bounds.Position);

            // Unheld, this would fit on one line (96 + 100 + 104 = 300).
            children[0].Width = 96;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(100, 0), children[1].Bounds.Position);
            Assert.Equal(new Point(0, 20), children[2].Bounds.Position);
            Assert.Equal(40, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WhileHolding_AChildThatGrows_WidensAtOnce_EvenIfThatWrapsTheNextChild()
    {
        // Holding never clips or overlaps a value that got longer: the bar may wrap once, then it stays.
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 96);
        try
        {
            Assert.Equal(20, panel.Bounds.Height); // 296 fits

            children[0].Width = 108;               // 108 + 100 + 96 = 304 does not
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(108, 0), children[1].Bounds.Position);
            Assert.Equal(new Point(0, 20), children[2].Bounds.Position);
            Assert.Equal(40, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WhileHolding_ResizingRewrapsAtTheNewWidth_StillUsingTheHeldWidths()
    {
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 96);
        try
        {
            children[0].Width = 90;   // held at 100
            Dispatcher.UIThread.RunJobs();

            panel.Width = 250;        // 100 + 100 + 96 = 296 no longer fits
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Point(0, 20), children[2].Bounds.Position);

            panel.Width = 400;        // fits again, the first child still at its held 100
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Point(100, 0), children[1].Bounds.Position);
            Assert.Equal(new Point(200, 0), children[2].Bounds.Position);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ReleasingTheHold_ShrinksChildrenBackToTheirCurrentWidths()
    {
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 104);
        try
        {
            children[0].Width = 96;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40, panel.Bounds.Height); // held at 100

            panel.HoldWidths = false;              // uploads stopped
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(96, 0), children[1].Bounds.Position);
            Assert.Equal(new Point(196, 0), children[2].Bounds.Position);
            Assert.Equal(20, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HoldingAgain_StartsFromTheCurrentWidths_NotTheOnesHeldBefore()
    {
        // The next upload run starts compact: widths held during the last run are gone.
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 104);
        try
        {
            children[0].Width = 96;
            Dispatcher.UIThread.RunJobs();
            panel.HoldWidths = false;
            Dispatcher.UIThread.RunJobs();

            panel.HoldWidths = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(96, 0), children[1].Bounds.Position);
            Assert.Equal(20, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TurningTheHoldOff_DropsHeldWidths_EvenWhileThePanelIsHidden()
    {
        // The overview collapsed while one run ends and the next starts: a hidden panel gets no layout pass in
        // between, and the next run must still start from the current widths.
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 104);
        try
        {
            children[0].Width = 96;
            Dispatcher.UIThread.RunJobs(); // held at 100

            panel.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            panel.HoldWidths = false;
            Dispatcher.UIThread.RunJobs();
            panel.HoldWidths = true;
            Dispatcher.UIThread.RunJobs();
            panel.IsVisible = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(96, 0), children[1].Bounds.Position);
            Assert.Equal(20, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WhileHolding_AHiddenChildTakesNoSpace_AndReturnsAtItsCurrentWidth()
    {
        // A stat switched off from the overview's right-click menu mid-run.
        (Window window, StableWrapPanel panel, Border[] children) = Show(holdWidths: true, 100, 100, 104);
        try
        {
            children[0].IsVisible = false;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(0, 0), children[1].Bounds.Position);   // its 100 px went with it
            Assert.Equal(new Point(100, 0), children[2].Bounds.Position); // 100 + 104 fits on one line
            Assert.Equal(20, panel.Bounds.Height);

            children[0].Width = 60;
            children[0].IsVisible = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Point(60, 0), children[1].Bounds.Position);  // 60, not the 100 held before
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ALine_IsAsTallAsItsTallestChild_AsInAWrapPanel()
    {
        Border[] children =
        [
            new Border { Width = 100, Height = 30 },
            new Border { Width = 100, Height = ChildHeight },
            new Border { Width = 104, Height = ChildHeight },
        ];
        (Window window, StableWrapPanel panel, _) = Show(holdWidths: false, children);
        try
        {
            Assert.Equal(new Point(0, 30), children[2].Bounds.Position); // line 1 is 30 tall, not its last child's 20
            Assert.Equal(50, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AnOverflowOfAFloatingPointHair_StillFits_AsInAWrapPanel()
    {
        // 102.4 + 156.8 + 40.8 is exactly 300, but in doubles it sums to 300.00000000000006. A WrapPanel keeps
        // that on the line (its overflow test has a tolerance), and so must this panel: at 125% scaling layout
        // runs in 0.8 px steps, and without the tolerance a stat could wrap where the WrapPanel kept it.
        Border[] children = [.. new[] { 102.4, 156.8, 40.8 }
            .Select(w => new Border { Width = w, Height = ChildHeight, UseLayoutRounding = false })];
        (Window window, StableWrapPanel panel, _) = Show(holdWidths: false, children);
        try
        {
            Assert.Equal(0, children[2].Bounds.Y);
            Assert.Equal(20, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, StableWrapPanel Panel, Border[] Children) Show(bool holdWidths, params double[] widths)
        => Show(holdWidths, [.. widths.Select(w => new Border { Width = w, Height = ChildHeight })]);

    private static (Window Window, StableWrapPanel Panel, Border[] Children) Show(bool holdWidths, Border[] children)
    {
        StableWrapPanel panel = new()
        {
            Width = LineWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HoldWidths = holdWidths,
        };
        panel.Children.AddRange(children);

        Window window = new() { Width = 800, Height = 600, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, panel, children);
    }
}
