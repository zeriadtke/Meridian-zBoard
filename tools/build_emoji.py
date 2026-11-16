#!/usr/bin/env python3
"""
Erzeugt die Emoji-Daten für zBoard (Assets/emoji/).

Quellen (per git holen, siehe README "Emoji-Daten neu erzeugen"):
  --emoji-json   unicode-emoji-json, data-by-group.json (MIT)        https://github.com/muan/unicode-emoji-json
  --cldr         Unicode CLDR, Wurzelverzeichnis (Unicode License)   https://github.com/unicode-org/cldr
                 genutzt: common/annotations/<sprache>.xml und common/annotationsDerived/<sprache>.xml

Ausgabe:
  emoji.txt               gruppe<TAB>emoji<TAB>hauttöne(0/1), in Unicode-Reihenfolge
  keywords.<sprache>.txt  emoji<TAB>name<TAB>stichwort|stichwort|…  (für die Emoji-Suche)
"""
import argparse
import json
import os
import xml.etree.ElementTree as ET

GROUPS = ["smileys_emotion", "people_body", "animals_nature", "food_drink", "travel_places",
          "activities", "objects", "symbols", "flags"]


def key(emoji):
    """CLDR schreibt Emoji ohne Variantenselektor U+FE0F."""
    return emoji.replace("️", "")


def load_annotations(cldr, lang):
    names, keywords = {}, {}
    for folder in ("annotations", "annotationsDerived"):
        path = os.path.join(cldr, "common", folder, f"{lang}.xml")
        if not os.path.exists(path):
            continue
        for node in ET.parse(path).getroot().iter("annotation"):
            cp = key(node.get("cp", ""))
            text = (node.text or "").strip()
            if not cp or not text or text == "↑↑↑":
                continue
            if node.get("type") == "tts":
                names.setdefault(cp, text)
            else:
                keywords.setdefault(cp, [w.strip() for w in text.split("|") if w.strip()])
    return names, keywords


PINYIN_TONES = str.maketrans("āáǎàēéěèīíǐìōóǒòūúǔùüǖǘǚǜńňǹ", "aaaaeeeeiiiioooouuuuvvvvvnnn")


def load_pinyin(pinyin_data):
    """Schriftzeichen -> häufigste Lesung ohne Tonzeichen (ü als v, wie auf QWERTY getippt)."""
    chars = {}
    for name in ("pinyin.txt", "kMandarin_8105.txt"):  # die zweite Datei nennt die gebräuchlichste Lesung
        with open(os.path.join(pinyin_data, name), encoding="utf-8") as src:
            for line in src:
                line = line.split("#")[0].strip()
                if line.startswith("U+"):
                    code, readings = line.split(":", 1)
                    plain = readings.split(",")[0].strip().translate(PINYIN_TONES)
                    if plain.isascii() and plain.isalpha():
                        chars[chr(int(code[2:], 16))] = plain
    return chars


def pinyin_of(text, chars):
    """Pinyin eines Stichworts aus Schriftzeichen ("笑脸" -> "xiaolian"); None, wenn anderes darin steht."""
    return "".join(chars[c] for c in text) if text and all(c in chars for c in text) else None


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--emoji-json", required=True)
    p.add_argument("--cldr", required=True)
    p.add_argument("--languages", default="de,en,fr,es,it,nl,pl,cs,hu,ro,tr,vi,el,ru,uk,bg,ar,zh")
    p.add_argument("--pinyin-data", help="für zh: https://github.com/mozillazg/pinyin-data (MIT); ergänzt die Stichwörter "
                   "um ihre Pinyin-Schreibweise, damit die Suche mit der Pinyin-Tastatur trifft")
    p.add_argument("--out-dir", default="Assets/emoji")
    args = p.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    groups = json.load(open(args.emoji_json, encoding="utf-8"))
    emojis = []
    with open(os.path.join(args.out_dir, "emoji.txt"), "w", encoding="utf-8") as out:
        out.write("# Quelle: unicode-emoji-json (MIT), Reihenfolge nach Unicode CLDR\n")
        for group in groups:
            index = GROUPS.index(group["slug"])
            for e in group["emojis"]:
                out.write(f"{index}\t{e['emoji']}\t{1 if e.get('skin_tone_support') else 0}\n")
                emojis.append(e)
    print(f"emoji.txt: {len(emojis)} Emoji")

    chars = load_pinyin(args.pinyin_data) if args.pinyin_data else {}
    for lang in args.languages.split(","):
        names, keywords = load_annotations(args.cldr, lang)
        count = 0
        with open(os.path.join(args.out_dir, f"keywords.{lang}.txt"), "w", encoding="utf-8") as out:
            out.write("# Quelle: Unicode CLDR annotations (Unicode License v3)\n")
            for e in emojis:
                k = key(e["emoji"])
                name = names.get(k) or e["name"]
                words = keywords.get(k, [])
                if lang == "zh" and args.pinyin_data:
                    readings = [pinyin_of(w, chars) for w in [name] + words]
                    words = words + list(dict.fromkeys(r for r in readings if r and r not in words))
                out.write(f"{e['emoji']}\t{name}\t{'|'.join(words)}\n")
                count += k in names
        print(f"keywords.{lang}.txt: {count} von {len(emojis)} mit Namen in der Sprache")


if __name__ == "__main__":
    main()
