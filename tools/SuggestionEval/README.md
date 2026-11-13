# Vorschlagsmessung

Misst, wie gut Wortvorschläge und Autokorrektur sind, indem es einen Testtext mit simulierten Tippfehlern „tippt“.
Benutzt dieselben Quelltexte wie die App (`Suggestions/`, `Keyboard/Layouts.cs`) und die Wortlisten aus `Assets/dict/`.

```bash
dotnet run -c Release --project tools/SuggestionEval -- --lang de
dotnet run -c Release --project tools/SuggestionEval -- --lang en --verbose        # mit Fehlerbeispielen
dotnet run -c Release --project tools/SuggestionEval -- --lang de --text mein.txt  # eigener Text, ein Satz pro Zeile
```

Weitere Optionen: `--runs`, `--sigma`, `--lazy`, `--seed`, `--model`, `--no-touch`, `--no-context` (siehe `--help`). Gleiche Optionen ergeben immer
dieselben Zahlen.

## Was gemessen wird

**Vorschläge (fehlerfrei getippt):** Für jedes Wort wird geprüft, ob es vor dem ersten Buchstaben vorhergesagt wird
und ab welchem Buchstaben es in der Leiste steht. Den Satzkontext liest die Messung wie der Tastaturdienst
(`WordContext.FromText`). „Gesparte Tastendrücke“ rechnet mit einem Nutzer, der einen
Vorschlag antippt, sobald er sichtbar ist.

**Autokorrektur (ungenau getippt):** Jeder Tastendruck landet normalverteilt um die Tastenmitte (`--sigma`, in
Tastenbreiten); die nächstgelegene Taste zählt. Dazu kommen ausgelassene, doppelte und vertauschte Buchstaben und
Umlaute, die als Grundbuchstabe getippt werden („fur“). Nach jedem Wort folgt die Leertaste; verglichen wird, was
dann im Text stünde.

**Fehlendes Leerzeichen:** Je zwei aufeinanderfolgende Wörter werden ohne Leerzeichen dazwischen und sonst
fehlerfrei getippt; gezählt wird, wie oft danach beide Wörter richtig dastehen.

**Leertaste mitten im Wort:** Jedes Wort ab fünf Buchstaben wird einmal mit einer Leertaste statt eines Buchstabens
der untersten Reihe und einmal mit einer zusätzlichen Leertaste getippt. Als Fehlalarm zählt, wenn in den Durchläufen
mit Tippfehlern zwei verschiedene Wörter verschmelzen.

Wie am Gerät tippt der simulierte Nutzer mitten im Satz alles klein und überlässt die Großschreibung der Tastatur.
Gelerntes gibt es nicht (frisch installierte Tastatur).

## Werte (mitgelieferte Testsätze)

| | Deutsch 0.11.1 | Deutsch 0.12.0 | Englisch 0.11.1 | Englisch 0.12.0 |
| --- | --- | --- | --- | --- |
| Nächstes Wort vorhergesagt, unter den ersten 3 | – | 31,0 % | – | 35,0 % |
| … nur nach einem Wort (ohne Satzanfang und Komma) | 15,6 % | 28,2 % | 18,2 % | 33,6 % |
| Wort nach der Hälfte der Buchstaben in der Leiste | 61,1 % | 76,6 % | 71,0 % | 83,7 % |
| Gesparte Tastendrücke | 42,1 % | 51,0 % | 39,3 % | 49,9 % |
| Tippfehler richtig korrigiert | 66,3 % | 81,9 % | 60,1 % | 78,9 % |
| Richtig getippte Wörter, die verändert wurden | 0,0 % | 0,0 % | 1,4 % | 0,0 % |
| Richtig getippte Wörter mit falscher Groß-/Kleinschreibung | 2,0 % | 1,2 % | 1,0 % | 1,0 % |
| Falsche Wörter im Text nach der Korrektur | 8,8 % | 4,8 % | 8,5 % | 4,3 % |

In 0.12.0 dazugekommen:

1. Englische Kurzformen in der Wortliste („don't“, „I'm“) und die Korrektur seltener Wörterbucheinträge („fur“ →
   „für“, „dont“ → „don't“, „yiu“ → „you“; `LazySpellingRatio` und `RareWordRatio` in `SuggestionEngine`).
2. Das Sprachmodell (`NgramModel`, erzeugt mit `tools/NgramBuilder`) statt der 5 000 Wortpaare: Vorhersage aus zwei
   Wörtern Kontext, auch am Satzanfang und nach Komma; Vorschläge und Korrekturen nach dem Satz geordnet
   (`MaxContextFactor`); Großschreibung mehrdeutiger Wörter aus dem Satz („eine frage“ → „Frage“, `NounContextRatio`).
3. Fingerpositionen (`TouchTrace`): Die Korrektur rechnet mit dem Abstand des Fingers zu den Tasten statt mit
   „Nachbartaste ja oder nein“ (`TouchTrace.Sigma`), und ein echtes Wort wird korrigiert, wenn der Finger fast auf
   der Grenze zur Nachbartaste lag und das Nachbarwort viel besser in den Satz passt („heute drei“ → „frei“;
   `NearMissDistance`, `NearMissRatio`). Vergleich ohne Fingerpositionen: `--no-touch`.
4. Zusammengesetzte Substantive aus bekannten Substantiven gelten als richtig geschrieben („Haustürschlüssel“), und
   ein fehlendes Leerzeichen wird eingesetzt, wenn die beiden Wörter im Sprachmodell aufeinander folgen („ichhabe“ →
   „ich habe“). Von den Wortpaaren der Testsätze, ohne Leerzeichen getippt, werden 68,6 % (Deutsch) und 63,5 %
   (Englisch) richtig getrennt.
5. Leertaste mitten im Wort (`JoinAcrossSpace`): Trifft die Leertaste statt eines Buchstabens der untersten Reihe
   („da ke“) oder zusätzlich („ha llo“), setzt zBoard beim nächsten Leerzeichen das gemeinte Wort ein. Wiederhergestellt
   werden 57,4 % bzw. 66,0 % (Deutsch) und 68,3 % bzw. 67,2 % (Englisch); zwei richtige Wörter bleiben immer getrennt.
   Dazu werden zusammengesetzte Substantive vervollständigt („Haustürschl“ → „Haustürschlüssel“).

## Grenzen

- Die Testsätze in `samples/` sind wenige (rund 1 000 Wörter je Sprache) und eigens geschrieben. Unterschiede unter
  etwa einem Prozentpunkt sind Zufall; mit `--runs 20` oder einem größeren Text wird es genauer.
- Die Tippfehler sind simuliert. Echte Finger treffen systematisch daneben (z. B. zu tief), das bildet die
  Normalverteilung nicht ab. Das gilt besonders für den Gewinn durch die Fingerpositionen: Die Korrektur nimmt
  dieselbe Art von Streuung an, mit der die Messung tippt. Auf dem Gerät kann der Gewinn deshalb kleiner ausfallen.
- Wischeingabe, mehrsprachige Eingabe und Gelerntes werden nicht gemessen.
