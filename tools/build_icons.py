#!/usr/bin/env python3
"""
Wandelt Material Symbols (Apache 2.0, https://github.com/google/material-design-icons) in Android-VectorDrawables um.

    git clone --depth 1 --filter=blob:none --sparse https://github.com/google/material-design-icons
    python3 tools/build_icons.py material-design-icons

Ausgabe: Resources/drawable/ms_<name>.xml (schwarz; zBoard färbt sie beim Zeichnen passend zum Design ein).
"""
import os
import re
import sys

ICONS = [
    # Tastatur
    "shift", "shift_fill1", "shift_lock_fill1", "backspace", "keyboard_return", "search", "send", "arrow_forward",
    "check", "keyboard_tab", "keyboard_tab_rtl", "sentiment_satisfied", "language",
    # Leiste
    "mic", "content_paste", "settings", "back_hand",
    # Einstellungen
    "tune", "palette", "spellcheck", "gesture", "menu_book", "keyboard", "info", "arrow_back",
    "settings_backup_restore", "save", "restore_page",
]

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def convert(svg):
    paths = re.findall(r'<path d="([^"]+)"', svg)
    body = "\n".join(f'    <path android:fillColor="#FF000000" android:pathData="{d}"/>' for d in paths)
    return f'''<?xml version="1.0" encoding="utf-8"?>
<!-- Material Symbols (Apache License 2.0), erzeugt von tools/build_icons.py -->
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp" android:height="24dp"
    android:viewportWidth="960" android:viewportHeight="960">
  <group android:translateY="960">
{body}
  </group>
</vector>
'''


def main():
    source = sys.argv[1]
    for name in ICONS:
        base = name.removesuffix("_fill1")
        path = os.path.join(source, "symbols", "web", base, "materialsymbolsoutlined", f"{name}_24px.svg")
        with open(path, encoding="utf-8") as f:
            xml = convert(f.read())
        out = os.path.join(ROOT, "Resources", "drawable", f"ms_{name}.xml")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            f.write(xml)
        print(os.path.relpath(out, ROOT))


if __name__ == "__main__":
    main()
