// <copyright file="Avalonia12EmptyLineHang.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.UI;

/// <summary>
/// Works around an Avalonia 12.1.1 layout hang that is specific to the HEADLESS platform: wrapping
/// text that contains an EMPTY line spins forever inside
/// <c>TextFormatterImpl.PerformTextWrapping</c> / <c>CreateEmptyTextLine</c>. The headless test
/// suite runs every window on that platform, and several localized bodies (and every release-notes
/// rendering) contain <c>\n\n</c> — so without this, the suite hangs instead of failing.
/// </summary>
/// <remarks>
/// Headless-only, established by probing the same shapes on the real Windows renderer (12.1.1,
/// Skia): a wrapping TextBlock and a wrapping TextBox, fixed-size and SizeToContent, all converge
/// live with empty-line text — the degenerate metrics come from the headless text shaper. Under
/// headless, bisection pinned the shapes that spin: wrap + empty line + a content-sized measure —
/// a SizeToContent window's TextBlock, or a wrapping TextBox anywhere (its scroll presenter
/// measures unbounded even in a fixed window; caught mid-spin in a stack dump of the test
/// process). A fixed-size window's TextBlock is immune.
/// <para>
/// The mitigation is a single real space on each otherwise-empty line — invisible on screen, and a
/// zero-width space does NOT work (it still shapes to a zero-width line and still hangs). It is
/// applied in production code at the few sinks whose text can carry <c>\n\n</c>, because that is
/// the one place that covers the app's headless tests and the live app alike, and the live cost is
/// nil. REMOVE when the app moves to an Avalonia whose headless platform has the upstream fix; the
/// padder's unit tests and the prompt/message-box tests that hung without it are the regression
/// net that makes removal safe to try. Closest known upstream report at the time of writing:
/// AvaloniaUI/Avalonia#21685 (degenerate paragraph width in the same formatter).
/// </para>
/// </remarks>
public static class Avalonia12EmptyLineHang
{
    /// <summary>
    /// The text with every empty line carrying one space, so the 12.1.1 formatter never meets the
    /// line shape it cannot finish measuring. Null passes through — absence stays absence.
    /// Contract edge: lines are what <c>\n</c> delimits, so classic-Mac CR-only breaks
    /// (<c>"a\r\rb"</c>) are NOT padded — no text in this app is sourced that way.
    /// </summary>
    public static string? PadEmptyLines(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\n', StringComparison.Ordinal))
        {
            return text;
        }

        string[] lines = text.Split('\n');
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            // \r survives on the line under CRLF input; a line that is only a \r is still rendered
            // empty, so it gets the space too (before the \r, keeping the CRLF pair intact).
            if (lines[i].Length == 0)
            {
                lines[i] = " ";
                changed = true;
            }
            else if (lines[i] == "\r")
            {
                lines[i] = " \r";
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : text;
    }
}
