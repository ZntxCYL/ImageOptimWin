#!/usr/bin/env python3
"""Ensure build-critical fixes are present in every source archive.
Run from any directory: python3 scripts/check-source-config.py
"""
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
project_path = root / "src/ImageOptim.Win/ImageOptim.Win.csproj"
theme_path = root / "src/ImageOptim.Win/Services/ThemeManager.cs"
errors = []
try:
    project = ET.parse(project_path).getroot()
    content_items = [e for e in project.iter("Content")
                     if e.attrib.get("Include", "").replace("/", "\\") == r"tools\**\*"]
    if len(content_items) != 1:
        errors.append("Expected exactly one Content item for tools\\**\\*")
    else:
        item = content_items[0]
        if item.attrib.get("ExcludeFromSingleFile", "").lower() != "true":
            errors.append("tools Content must set ExcludeFromSingleFile=true")
        if item.attrib.get("CopyToPublishDirectory") != "PreserveNewest":
            errors.append("tools Content must be copied to the publish directory")
except (ET.ParseError, OSError) as ex:
    errors.append(f"Cannot parse project: {ex}")
try:
    if "using System.IO;" not in theme_path.read_text(encoding="utf-8"):
        errors.append("ThemeManager.cs is missing using System.IO;")
except OSError as ex:
    errors.append(f"Cannot read ThemeManager.cs: {ex}")
if errors:
    for message in errors:
        print(f"[CONFIG ERROR] {message}", file=sys.stderr)
    raise SystemExit(1)
print("[CONFIG OK] tools excluded from single-file and ThemeManager imports System.IO")
