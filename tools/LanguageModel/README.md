# Neuronales Sprachmodell (KI-Vorhersage)

Ein kleines LSTM je Sprache sagt das nächste Wort aus dem **ganzen Satz** bis zur Schreibstelle vorher (bis zu 24
Wörter), nicht nur aus den beiden Wörtern davor wie das N-Gramm-Modell (`Assets/dict/<Sprache>.ngrams.bin`). Beide
werden gemischt (`SuggestionEngine.NeuralWeight`): bei der Nächstes-Wort-Vorhersage, bei der Reihenfolge der
Vorschläge, bei der Wahl der Autokorrektur und beim Wischen. Ob ein Wort aus dem Wörterbuch überhaupt korrigiert wird,
entscheidet weiterhin die Bewertung ohne Satzkontext – das Netz kann also keine richtig getippten Wörter „verschlimmbessern“.

Läuft vollständig auf dem Gerät mit [ONNX Runtime](https://onnxruntime.ai) (MIT). Fehlt für eine Sprache das Modell
in `Assets/lm/`, arbeitet zBoard wie bisher nur mit N-Grammen. Abschaltbar unter Einstellungen → Vorschläge →
„KI-Vorhersage“; dann wird das Netz gar nicht erst geladen.

| | Vorgabe |
| --- | --- |
| Vokabular | 16 000 häufigste Schreibweisen der Wortliste (`--vocab`) |
| Netz | Einbettung 128, LSTM 256, Ein- und Ausgabe teilen sich eine Matrix (`--embed`, `--hidden`, `--no-tie`) |
| Größe | 2,5 Mio. Gewichte, als int8 etwa **2,5 MB pro Sprache** (18 Sprachen ≈ 45 MB) |
| Rechenzeit | unter 1 ms pro Vorhersage; die letzten 8 Satzstellen werden zwischengespeichert |
| Arbeitsspeicher | etwa 10–15 MB pro aktiver Sprache |

## Dateien

| Datei | Inhalt |
| --- | --- |
| `Assets/lm/<Sprache>.lm.onnx` | Netz: Eingang `tokens` (int64, [1, Länge]), Ausgang `probs` (float, [1, Vokabular]) |
| `Assets/lm/<Sprache>.vocab.txt` | Vokabular, Zeile i = Eintrag i: `wort<TAB>anteil` (Anteil am Trainingstext); die ersten vier sind `<pad>`, `<unk>`, `<s>`, `,` |
| `Suggestions/NeuralModel.cs` | Vokabular, Verteilung, Laden im Hintergrund, Zwischenspeicher (plattformunabhängig, mit Unit-Tests) |
| `Suggestions/OnnxLanguageModel.cs` | Ausführen mit ONNX Runtime |

## Trainieren

Voraussetzungen: Python 3.10+, `pip install torch onnx onnxruntime numpy` (für Chinesisch zusätzlich `jieba`).
Mit einer NVIDIA-Grafikkarte nimmt PyTorch sie von selbst (`--device cuda`); dann dauert eine Sprache wenige Minuten,
auf der CPU etwa 1–3 Stunden.

```bash
git clone --depth 1 --filter=blob:none --sparse https://github.com/common-voice/common-voice
git -C common-voice sparse-checkout set --no-cone '/server/data/de/*'

python3 tools/LanguageModel/train_lm.py --lang de --common-voice common-voice/server/data/de
```

Alle 18 Sprachen nacheinander (lädt die Satzsammlung selbst, falls nötig):

```bash
tools/LanguageModel/train_all.sh                 # alle
tools/LanguageModel/train_all.sh de en fr        # nur diese
```

Unter Windows (PowerShell) mit zusätzlichem Text aus der Leipzig Corpora Collection – das Skript sucht je Sprache das
neueste Web-Korpus des Landes (sonst Nachrichten), lädt es nach `..\lm-daten` und trainiert:

```powershell
powershell -ExecutionPolicy Bypass -File tools\LanguageModel\train_all.ps1                  # alle ohne Modell
powershell -ExecutionPolicy Bypass -File tools\LanguageModel\train_all.ps1 -Languages en,fr
```

Das Skript zeigt alle 5 000 Schritte die Perplexität und wie oft das nächste Wort im Prüftext auf Platz 1 bzw. unter
den ersten 3 steht, prüft das fertige int8-Modell gegen PyTorch und schreibt beide Dateien nach `Assets/lm/`.

Wichtige Optionen: `--steps` (Vorgabe 30 000), `--batch`, `--vocab`, `--source Datei.txt:Gewicht` (eigene Sätze, ein
Satz pro Zeile, mehrfach möglich), `--max-lines` (zum schnellen Ausprobieren), `--seed`.

## Quellen

Wie beim N-Gramm-Modell die Satzsammlung von [Mozilla Common Voice](https://github.com/common-voice/common-voice)
(CC0), gleich gewichtet: eingesandte Alltagssätze 10, Europarl 1, Wikipedia 0,15.

Für einige Sprachen ist sie klein (Griechisch, Vietnamesisch, Rumänisch, Chinesisch, Russisch, Arabisch, Bulgarisch:
unter 5 MB). Das Skript warnt bei weniger als 1 Mio. Wörtern. Dann lohnt sich mehr Text mit `--source`, z. B. die
Satzlisten von [Tatoeba](https://tatoeba.org/de/downloads) (CC BY 2.0 FR) oder die
[Leipzig Corpora](https://wortschatz.uni-leipzig.de/de/download) (CC BY 4.0, Nachrichten/Web, je Sprache 1 Mio. Sätze).
Lizenz der Quelle dann in `THIRD_PARTY_NOTICES.md` nachtragen. Ohne genug Text lieber kein Modell für diese Sprache
ausliefern: Ein schwaches Netz bringt gegenüber den N-Grammen nichts.

## Wörter lesen wie beim Tippen

Training und Tastatur müssen dasselbe unter „Kontext“ verstehen. `train_lm.py` liest Text nach denselben Regeln wie
`WordContext.FromTextWithSentence` und `tools/NgramBuilder`: Wörter aus Buchstaben, Ziffern, Apostroph und
Bindestrich, Komma als eigenes Zeichen, nach `. ! ? …` beginnt ein Satz (`<s>`), jedes andere Satzzeichen unterbricht
den Kontext. Wörter stehen in der Schreibweise der Wortliste (am Satzanfang bei mehrdeutigen Wörtern die häufigere).
Nach Änderungen an der Wortliste daher neu trainieren.

Chinesisch trennt die Wörter mit jieba; beim Tippen bekommt das Netz bisher nur das zuletzt eingesetzte Wort
(`ChineseContext`).

## Messwerte

Deutsch, mitgelieferte Testsätze (`tools/SuggestionEval`, 5 Durchläufe, Stand mit Siezen, Tippmuster und ähnlichen
Wörtern). Das Modell in `Assets/lm/de.lm.onnx`: 30 000 Schritte auf einer RTX 5070, Common Voice plus
`deu-de_web_2021_1M` der Leipzig Corpora Collection mit Gewicht 2:

```bash
python3 tools/LanguageModel/train_lm.py --lang de --common-voice common-voice/server/data/de \
  --source leipzig-de-web.txt:2
```

`leipzig-de-web.txt` ist die zweite Spalte (der Satz) aus `deu-de_web_2021_1M-sentences.txt`.

| | nur N-Gramme | nur Common Voice | + Leipzig-Web |
| --- | --- | --- | --- |
| Nächstes Wort vorhergesagt, Platz 1 | 14,5 % | 15,9 % | 16,0 % |
| Nächstes Wort vorhergesagt, unter den ersten 3 | 31,0 % | 34,0 % | **36,4 %** |
| Wort nach der Hälfte der Buchstaben in der Leiste | 75,0 % | 80,2 % | 81,1 % |
| Gesparte Tastendrücke | 50,4 % | 53,0 % | **54,1 %** |
| Tippfehler richtig korrigiert | 84,9 % | 85,4 % | 85,8 % |
| Richtig getippte Wörter, die verändert wurden | 0,0 % | 0,0 % | 0,0 % |
| Falsche Wörter im Text nach der Korrektur | 3,6 % | 3,5 % | 3,4 % |

Viermal längeres Training (7 000 → 30 000 Schritte) brachte allein kaum etwas (34,2 % → 34,0 %, innerhalb der
Messstreuung von gut einem Prozentpunkt); der Gewinn kam erst mit mehr Text. Für die anderen Sprachen daher zuerst
Text dazunehmen (Web-Korpus der Leipzig Corpora Collection für das Land der Sprache, 1M).

`NeuralWeight` 0,35 und 0,7 waren beide schlechter als 0,5 (unter den ersten 3: 33,1 % bzw. 33,3 %).

### Alle Sprachen, fremder Text

Je 300 Sätze aus einem kleinen Korpus (10K) der Leipzig Corpora Collection, das keins der beiden Modelle kennt (andere
Quelle oder anderes Jahr als im Training, nicht Common Voice); `tools/LanguageModel/compare_all.ps1`, ohne → mit Netz.
Nachrichtentext ist schwerer vorherzusagen als die Testsätze oben, die absoluten Werte liegen daher niedriger.

| Sprache | Nächstes Wort unter den ersten 3 | Gesparte Tastendrücke | Falsche Wörter im Text |
| --- | --- | --- | --- |
| Deutsch | 13,4 % → **20,3 %** | 28,8 % → 32,6 % | 17,1 % → 16,5 % |
| Englisch | 18,2 % → **24,2 %** | 30,6 % → 35,2 % | 15,7 % → 15,3 % |
| Arabisch | 3,1 % → **12,5 %** | 16,2 % → 22,1 % | 19,9 % → 19,1 % |
| Bulgarisch | 9,6 % → **21,6 %** | 22,2 % → 29,3 % | 15,0 % → 14,3 % |
| Tschechisch | 10,6 % → **17,8 %** | 21,9 % → 26,8 % | 19,5 % → 18,8 % |
| Griechisch | 5,9 % → **21,6 %** | 25,6 % → 34,3 % | 15,7 % → 15,1 % |
| Spanisch | 21,2 % → **27,9 %** | 33,4 % → 37,2 % | 14,5 % → 14,2 % |
| Französisch | 16,6 % → **27,0 %** | 30,8 % → 36,4 % | 15,8 % → 14,9 % |
| Ungarisch | 13,6 % → **20,7 %** | 21,2 % → 24,5 % | 23,7 % → 23,3 % |
| Italienisch | 13,2 % → **21,8 %** | 29,4 % → 34,7 % | 15,0 % → 14,3 % |
| Niederländisch | 14,4 % → **24,9 %** | 32,4 % → 38,0 % | 12,7 % → 12,1 % |
| Polnisch | 11,0 % → **20,2 %** | 21,2 % → 26,4 % | 20,5 % → 20,0 % |
| Rumänisch | 10,3 % → **23,0 %** | 24,4 % → 31,6 % | 17,7 % → 17,2 % |
| Russisch | 9,1 % → **17,9 %** | 20,0 % → 25,4 % | 20,0 % → 19,8 % |
| Türkisch | 6,9 % → **12,7 %** | 18,2 % → 22,0 % | 26,9 % → 26,2 % |
| Ukrainisch | 6,8 % → **13,0 %** | 17,5 % → 22,0 % | 23,0 % → 22,5 % |
| Vietnamesisch | 9,6 % → **26,7 %** | 24,1 % → 36,5 % | 18,0 % → 16,2 % |

In allen Sprachen ist das Netz bei allen drei Werten besser. Mit Sätzen aus Common Voice als Test (die das
N-Gramm-Modell wörtlich kennt) sah es dagegen oft nach Gleichstand aus – für solche Vergleiche nie Trainingstext nehmen.

## Messen und abstimmen

`tools/SuggestionEval` nimmt das Modell aus `Assets/lm/` automatisch mit; Vergleich ohne Netz:

```bash
dotnet run -c Release --project tools/SuggestionEval -- --lang de               # mit Netz
dotnet run -c Release --project tools/SuggestionEval -- --lang de --no-neural   # nur N-Gramme
```

`NeuralWeight` (Anteil des Netzes) und `MinNeuralFactor` (wie stark das Netz ein unpassendes Wort abwerten darf) in
`SuggestionEngine` damit abstimmen.
