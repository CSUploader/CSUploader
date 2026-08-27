// <copyright file="Avalonia12EmptyLineHang.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.UI;

/// <summary>
/// Works around an Avalonia 12.1.1 layout hang: a wrapping <c>TextBlock</c> whose text contains an
/// EMPTY line, measured under <c>SizeToContent</c>, spins forever inside
/// <c>TextFormatterImpl.PerformTextWrapping</c> / <c>CreateEmptyTextLine</c>. Every message box in
/// this app is a SizeToContent window, and several localized bodies (and every release-notes
/// rendering) contain <c>\n\n</c> — so without this, Help → Check for Updates hangs the app.
/// </summary>
/// <remarks>
/// The mitigation is a single real space on each otherwise-empty line. Probed, not guessed: a
/// zero-width space still shapes to a zero-width line and still hangs; a plain space renders no
/// glyph, keeps the same line height, and converges. Isolated by bisection in the headless suite —
/// three non-empty lines are fine, one empty line is not, and a fixed-size window is immune, which
/// is what pins the bug to empty-line + wrap + content-sized measure.
/// <para>
/// REMOVE when the app moves to an Avalonia with the upstream fix; the padder's unit tests and the
/// prompt/message-box tests that hung without it are the regression net that makes removal safe to
/// try. Closest known upstream report at the time of writing: AvaloniaUI/Avalonia#21685 (degenerate
/// paragraph width in the same formatter); this exact empty-line shape was reproduced locally on
/// 12.1.1.
/// </para>
/// </remarks>
public static class Avalonia12EmptyLineHang
{
    /// <summary>
    /// The text with every empty line carrying one space, so the 12.1.1 formatter never meets the
    /// line shape it cannot finish measuring. Null passes through — absence stays absence.
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
