# Fremde Inhalte in zBoard

Der Quellcode von zBoard steht unter der BSD 2-Clause License (siehe `LICENSE`).
Die mitgelieferten Wortlisten sind **Daten** mit eigener Lizenz:

## Assets/dict/de.txt und Assets/dict/en.txt

**Lizenz:** [Creative Commons Attribution-ShareAlike 4.0 (CC BY-SA 4.0)](https://creativecommons.org/licenses/by-sa/4.0/)

Erstellt mit `tools/build_dictionaries.py` aus:

| Quelle | Verwendung | Lizenz |
| --- | --- | --- |
| [FrequencyWords](https://github.com/hermitdave/FrequencyWords) von Hermit Dave, Daten aus [OpenSubtitles 2018](http://opus.nlpl.eu/OpenSubtitles2018.php) | Wörter und Häufigkeiten (Deutsch, Englisch) | CC BY-SA 4.0 (Inhalte) |
| [german-nouns](https://github.com/gambolputty/german-nouns) von Gregor Weichbrodt, Daten aus [Wiktionary](https://de.wiktionary.org) | Großschreibung deutscher Substantive | CC BY-SA 4.0 |

Änderungen gegenüber den Quellen: auf die häufigsten 120 000 (Deutsch) bzw. 100 000 (Englisch) Wörter gekürzt,
Wörter mit Ziffern und Sonderzeichen entfernt, deutsche Substantive großgeschrieben, bei mehrdeutigen Wörtern
beide Schreibweisen aufgenommen.

Wer diese Wortlisten verändert und weitergibt, muss sie ebenfalls unter CC BY-SA 4.0 weitergeben.
Für den Code von zBoard gilt diese Pflicht nicht.

## Assets/dict/*.ngrams.bin

**Lizenz:** [Creative Commons Attribution-ShareAlike 4.0 (CC BY-SA 4.0)](https://creativecommons.org/licenses/by-sa/4.0/),
wie die Wortlisten, deren Wörter und Schreibweisen die Modelle übernehmen.

Sprachmodelle für die Vorhersage: die 200 000 häufigsten Zweier- und 150 000 häufigsten Dreierfolgen von Wörtern je
Sprache mit ihrer Wahrscheinlichkeit, erstellt mit `tools/NgramBuilder` aus der Satzsammlung von
[Mozilla Common Voice](https://github.com/common-voice/common-voice) (`server/data/<Sprache>/`): eingesandte Sätze,
Sätze aus Wikipedia und, für Deutsch, aus den Protokollen des Europäischen Parlaments. Common Voice gibt diese Sätze
gemeinfrei unter [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) weiter. Die Modelle enthalten keine
Sätze, nur gezählte Wortfolgen.

## Assets/dict/fr.txt, es.txt, it.txt und die Wortlisten der weiteren Sprachen

Französisch, Spanisch, Italienisch, Niederländisch, Polnisch, Tschechisch, Ungarisch, Rumänisch, Türkisch,
Vietnamesisch, Griechisch, Russisch, Ukrainisch, Bulgarisch und Arabisch aus derselben Quelle wie oben: Wortlisten
aus FrequencyWords (CC BY-SA 4.0). Änderung: auf 80 000 Wörter gekürzt, Tippfehler gefiltert. Bei den Sprachen ab
Niederländisch außerdem: nur Wörter in der Schrift der Sprache; Einträge ohne Akzente entfernt, wenn dieselbe
Buchstabenfolge mit Akzenten deutlich häufiger ist; Rumänisch mit ș/ț statt ş/ţ; Arabisch ohne Vokalzeichen.

## Assets/dict/zh.txt, zh.pinyin.txt

Chinesisch: Wörter und Häufigkeiten aus FrequencyWords (`zh_cn`, CC BY-SA 4.0), beschränkt auf die 8105 Zeichen der
Liste allgemein gebräuchlicher Schriftzeichen (vereinfacht). Die Pinyin-Lesungen stammen aus
[pinyin-data](https://github.com/mozillazg/pinyin-data) und
[phrase-pinyin-data](https://github.com/mozillazg/phrase-pinyin-data) von mozillazg (beide MIT, © 2016/2017 mozillazg);
Änderung: Tonzeichen entfernt, ü als v. Beide Dateien stehen wie die übrigen Wortlisten unter CC BY-SA 4.0.

Das chinesische Sprachmodell (`zh.ngrams.bin`) entstand aus den Common-Voice-Sätzen, vorher mit
[jieba](https://github.com/fxsjy/jieba) (MIT) in Wörter zerlegt; jieba selbst ist nicht enthalten.

Lizenztext von pinyin-data (Copyright (c) 2016 mozillazg) und phrase-pinyin-data (Copyright (c) 2017 mozillazg):

```
The MIT License (MIT)

Copyright (c) 2016 mozillazg

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Assets/emoji/

| Datei | Quelle | Lizenz |
| --- | --- | --- |
| `emoji.txt` | [unicode-emoji-json](https://github.com/muan/unicode-emoji-json) von Mu-An Chiou (Emoji, Kategorien, Hauttöne) | MIT |
| `keywords.*.txt` | [Unicode CLDR](https://github.com/unicode-org/cldr) annotations (Namen und Suchbegriffe) | [Unicode License v3](https://www.unicode.org/license.txt), © Unicode, Inc. |

## Resources/drawable/ms_*.xml

Symbole aus [Material Symbols](https://github.com/google/material-design-icons) von Google, © Google LLC,
[Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0). Änderung: von SVG in Android-VectorDrawables umgewandelt
(`tools/build_icons.py`).

## Nur beim Erzeugen benutzt, nicht enthalten

| Quelle | Verwendung | Lizenz |
| --- | --- | --- |
| [igerman98](https://www.j3e.de/ispell/igerman98/) (de_DE Hunspell, über [wooorm/dictionaries](https://github.com/wooorm/dictionaries)) | Prüfliste, welche Wörter auch kleingeschrieben vorkommen | GPLv2 / GPLv3 |

igerman98 wird nicht mit zBoard ausgeliefert; es dient beim Erzeugen nur als Ja/Nein-Prüfung für die Großschreibung.
