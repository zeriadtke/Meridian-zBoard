# Sprachmodelle erzeugen

Zählt Zweier- und Dreierfolgen von Wörtern in Satzsammlungen und schreibt daraus das Sprachmodell einer Sprache
(`Assets/dict/<Sprache>.ngrams.bin`, gelesen von `Suggestions/NgramModel.cs`).

```bash
git clone --depth 1 --filter=blob:none --sparse https://github.com/common-voice/common-voice
git -C common-voice sparse-checkout set --no-cone '/server/data/de/*' '/server/data/en/*' \
  '/server/data/fr/*' '/server/data/es/*' '/server/data/it/*'

dotnet run -c Release --project tools/NgramBuilder -- \
  --words Assets/dict/de.txt --out Assets/dict/de.ngrams.bin --common-voice common-voice/server/data/de
```

Gleiche Eingaben ergeben dieselbe Datei. Für Deutsch braucht ein Lauf knapp eine Minute und etwa 1,5 GB Arbeitsspeicher.

## Quelle

Die Satzsammlung von [Mozilla Common Voice](https://github.com/common-voice/common-voice) ist gemeinfrei (CC0). Sie
enthält eingesandte Sätze, Sätze aus Wikipedia und für Deutsch Protokolle des Europäischen Parlaments. `--common-voice`
gewichtet sie so, dass das Modell nach Nachrichten klingt und nicht nach Lexikon:

| Dateien | Gewicht |
| --- | --- |
| `wiki*.txt` | 0,15 |
| `europarl*.txt` | 1 |
| alle anderen (eingesandte Sätze) | 10 |

Eigene Texte kommen mit `--source Datei.txt:Gewicht` dazu (ein Satz pro Zeile). Je näher ein Text an Alltagssprache
ist, desto mehr bringt er: Mit den eingesandten Sätzen allein trifft die Vorhersage schon fast so gut wie mit allem
zusammen.

## Was ins Modell kommt

- Nur Wörter aus der Wortliste (`--words`), in deren Schreibweise. Deshalb nach Änderungen an der Wortliste neu erzeugen.
- Der Kontext wird gelesen wie beim Tippen (`WordContext.FromText`): Satzanfang und Komma sind eigene Zeichen, andere
  Satzzeichen und unbekannte Wörter unterbrechen die Folge.
- Behalten werden die 200 000 häufigsten Zweier- und 150 000 häufigsten Dreierfolgen (`--bigrams`, `--trigrams`). Mehr
  bringt kaum etwas: Mit je 1 000 000 steigt die Trefferquote der Vorhersage um etwa einen Prozentpunkt.

Wie gut ein Modell ist, misst `tools/SuggestionEval` (`--model <Datei>` für einen Vergleich).
