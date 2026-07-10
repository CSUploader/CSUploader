#!/usr/bin/env python3
"""Builds contact-sheet.html from the shots directory, pairing <view>-<theme>-wpf.png
with <view>-<theme>-ava.png side by side (design: screenshot review batched per phase
via a persisted HTML contact sheet). Missing counterparts render as a 'missing' cell —
expected while a view exists on only one side.

Usage: python scripts/contact-sheet.py [shots-dir]   (default D:/temp2/cbuild-mig/shots)
"""
import html
import re
import sys
from pathlib import Path


def main() -> None:
    shots = Path(sys.argv[1] if len(sys.argv) > 1 else r"D:/temp2/cbuild-mig/shots")
    pattern = re.compile(r"^(?P<view>.+)-(?P<theme>light|dark)-(?P<side>wpf|ava)\.png$")
    cells: dict[tuple[str, str, str], str] = {}
    for png in sorted(shots.glob("*.png")):
        m = pattern.match(png.name)
        if m:
            cells[(m["view"], m["theme"], m["side"])] = png.name

    pairs = sorted({(view, theme) for (view, theme, _) in cells})
    rows = []
    for view, theme in pairs:
        def img(side: str) -> str:
            name = cells.get((view, theme, side))
            if name is None:
                return '<td class="missing">missing</td>'
            return f'<td><img src="{html.escape(name)}" loading="lazy"></td>'
        rows.append(f"<tr><th>{html.escape(view)} — {theme}</th>{img('wpf')}{img('ava')}</tr>")

    out = shots / "contact-sheet.html"
    out.write_text(
        '<!doctype html><meta charset="utf-8"><title>CSUploader migration contact sheet</title>\n'
        "<style>body{font:13px sans-serif;background:#222;color:#eee}"
        "table{border-collapse:collapse}"
        "td,th{border:1px solid #555;padding:4px;vertical-align:top;text-align:left}"
        "img{max-width:900px;display:block}.missing{color:#f88}"
        "thead th{position:sticky;top:0;background:#333}</style>\n"
        "<table><thead><tr><th>view — theme</th><th>WPF (reference)</th><th>Avalonia</th></tr></thead>\n"
        + "".join(rows) + "</table>\n",
        encoding="utf-8",
    )
    print(f"wrote {out} ({len(pairs)} view/theme pairs)")


if __name__ == "__main__":
    main()
