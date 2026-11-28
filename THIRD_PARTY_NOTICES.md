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

## Assets/lm/ (neuronale Sprachmodelle)

**Lizenz:** [Creative Commons Attribution-ShareAlike 4.0 (CC BY-SA 4.0)](https://creativecommons.org/licenses/by-sa/4.0/),
wie die Wortlisten, deren Wörter und Schreibweisen das Vokabular (`<Sprache>.vocab.txt`) übernimmt.

Kleine LSTM-Netze, trainiert mit `tools/LanguageModel/train_lm.py` auf derselben Satzsammlung von
[Mozilla Common Voice](https://github.com/common-voice/common-voice) wie die N-Gramm-Modelle (CC0 1.0). Die Netze
enthalten keine Sätze, nur gelernte Gewichte. Kommen weitere Texte dazu (`--source`), steht deren Lizenz hier.

Alle Sprachen zusätzlich mit je einem Korpus (1 Mio. Sätze) der
[Leipzig Corpora Collection](https://wortschatz.uni-leipzig.de/de/download) (Universität Leipzig),
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Quelle: D. Goldhahn, T. Eckart, U. Quasthoff: *Building
Large Monolingual Dictionaries at the Leipzig Corpora Collection: From 100 to 200 Languages*, LREC 2012.

| Sprache | Korpus |
| --- | --- |
| Arabisch (`ar.lm.onnx`) | `ara-ae_web_2017_1M` |
| Bulgarisch (`bg.lm.onnx`) | `bul_news_2022_1M` |
| Tschechisch (`cs.lm.onnx`) | `ces_news_2024_1M` |
| Deutsch (`de.lm.onnx`) | `deu-de_web_2021_1M` |
| Griechisch (`el.lm.onnx`) | `ell-gr_web_2015_1M` |
| Englisch (`en.lm.onnx`) | `eng_web_2014_1M` |
| Spanisch (`es.lm.onnx`) | `spa_web_2016_1M` |
| Französisch (`fr.lm.onnx`) | `fra_news_2024_1M` |
| Ungarisch (`hu.lm.onnx`) | `hun-hu_web_2019_1M` |
| Italienisch (`it.lm.onnx`) | `ita_news_2024_1M` |
| Niederländisch (`nl.lm.onnx`) | `nld_news_2024_1M` |
| Polnisch (`pl.lm.onnx`) | `pol_news_2024_1M` |
| Rumänisch (`ro.lm.onnx`) | `ron-ro_web_2015_1M` |
| Russisch (`ru.lm.onnx`) | `rus_news_2024_1M` |
| Türkisch (`tr.lm.onnx`) | `tur-tr_web_2019_1M` |
| Ukrainisch (`uk.lm.onnx`) | `ukr-ua_web_2019_1M` |
| Vietnamesisch (`vi.lm.onnx`) | `vie-vn_web_2015_1M` |
| Chinesisch (`zh.lm.onnx`) | `zho-cn_web_2015_1M` |

## ONNX Runtime (Microsoft.ML.OnnxRuntime)

© Microsoft Corporation, [MIT License](https://github.com/microsoft/onnxruntime/blob/main/LICENSE). Führt die
neuronalen Sprachmodelle auf dem Gerät aus. Als NuGet-Paket eingebunden, unverändert.

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
