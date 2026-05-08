#!/usr/bin/env python3
"""Convert an i18n inventory Markdown file (Key = Value lines inside ``` code fences)
into a .resx file. Used to regenerate Resources/Strings*.resx from the canonical
docs/i18n-inventory*.md sources whenever the inventory changes.

Usage:
    python scripts/md-to-resx.py docs/i18n-inventory.md src/Resources/Strings.resx
    python scripts/md-to-resx.py docs/i18n-inventory.zh-Hans.md src/Resources/Strings.zh-Hans.resx
    ...

Conventions parsed:
- Only lines INSIDE triple-backtick code fences are considered key=value entries.
- A line of the form `<key> = <value>` (with arbitrary whitespace around the `=`).
- A trailing `# comment` (whitespace-prefixed `#`) is stripped from the value.
- Literal `\\n` in the value becomes a real newline in the resx output.
- Leading/trailing whitespace on the value is preserved relative to the equals sign.
"""

import argparse
import re
import sys
from pathlib import Path


LINE_RE = re.compile(r"^(?P<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?P<value>.*)$")
INLINE_COMMENT_RE = re.compile(r"\s+#\s.*$")


def parse_md(md_path: Path) -> dict[str, str]:
    entries: dict[str, str] = {}
    in_code_fence = False
    for raw in md_path.read_text(encoding="utf-8").splitlines():
        if raw.strip().startswith("```"):
            in_code_fence = not in_code_fence
            continue
        if not in_code_fence:
            continue

        match = LINE_RE.match(raw)
        if not match:
            continue

        key = match.group("key")
        value = match.group("value").rstrip()
        # Strip trailing inline comment ("  # ...").
        value = INLINE_COMMENT_RE.sub("", value).rstrip()
        # Decode literal \\n → newline.
        value = value.replace("\\n", "\n")
        if key in entries:
            print(f"WARNING: duplicate key '{key}' — keeping first occurrence", file=sys.stderr)
            continue
        entries[key] = value
    return entries


def xml_escape(s: str) -> str:
    return (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )


RESX_HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    Auto-generated from docs/i18n-inventory*.md by scripts/md-to-resx.py.
    Do not edit by hand — edit the inventory and regenerate.
  -->
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""

RESX_FOOTER = "</root>\n"


def write_resx(entries: dict[str, str], out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("w", encoding="utf-8", newline="\n") as f:
        f.write(RESX_HEADER)
        for key, value in entries.items():
            escaped = xml_escape(value)
            # xml:space="preserve" so leading/trailing whitespace and embedded newlines survive round-trips.
            f.write(f'  <data name="{key}" xml:space="preserve">\n')
            f.write(f"    <value>{escaped}</value>\n")
            f.write("  </data>\n")
        f.write(RESX_FOOTER)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="path to the inventory .md file")
    parser.add_argument("output", type=Path, help="path to the .resx to write")
    args = parser.parse_args()

    if not args.input.is_file():
        print(f"input not found: {args.input}", file=sys.stderr)
        return 1

    entries = parse_md(args.input)
    write_resx(entries, args.output)
    print(f"wrote {len(entries)} entries to {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
