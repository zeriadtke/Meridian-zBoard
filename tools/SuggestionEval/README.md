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

Danach (Autokorrektur, Stand November 2026; Deutsch mit dem neuronalen Probemodell aus `Assets/lm/`):

| | Deutsch vorher | Deutsch nachher | Englisch vorher | Englisch nachher |
| --- | --- | --- | --- | --- |
| Tippfehler richtig korrigiert | 82,7 % | **85,2 %** | 78,9 % | **82,7 %** |
| Richtig getippte Wörter, die verändert wurden | 0,0 % | 0,0 % | 0,0 % | 0,0 % |
| Richtig getippte Wörter mit falscher Groß-/Kleinschreibung | 1,2 % | 0,9 % | 1,0 % | 1,0 % |
| Falsche Wörter im Text nach der Korrektur | 4,6 % | **3,9 %** | 4,3 % | **3,7 %** |
| Fehlendes Leerzeichen: in die beiden Wörter getrennt | 69,2 % | 71,8 % | 63,5 % | **74,3 %** |

1. Seltene Einträge der Wortliste, die fast immer Vertipper sind („dic“, „mor“, „ie“), werden korrigiert, wenn der Satz
   klar für das andere Wort spricht (`RareWordContextRatio`). Ist das andere Wort ohne Satz nicht mindestens 300-mal
   wahrscheinlicher, bleibt das Getippte stehen („ganz fettig“).
2. Zwei Buchstaben werden um einen Buchstaben korrigiert („ic“ → „ich“, „su“ → „du“); die Leiste zeigt dabei weiter
   Vervollständigungen. Abkürzungen in Großbuchstaben schützen ein klein getipptes Wort nicht mehr („ic“ ist kein „IC“).
3. Fehlendes Leerzeichen schlägt eine Korrektur, die nur eines der beiden Wörter übrig lässt („fora“ → „for a“, nicht
   „for“).
4. Die Korrektur bekommt die Großschreibung, die in den Satz passt („das trwffen“ → „Treffen“).
5. Wortliste Deutsch: „Gute“, „Voraus“ und „Treffen“ zusätzlich großgeschrieben („Alles Gute“, „im Voraus“), „morgens“
   klein (`tools/build_dictionaries.py`); Sprachmodell neu erzeugt.

Gegenprobe mit absichtlich getippten seltenen Wörtern („Früher mochte ich …“, „Ich drucke …“, „viel zu fettig“):
unverändert 0,1 % (Deutsch) und 0,0 % (Englisch) veränderte Wörter. Eine Variante, bei der der Satz auch über Umlaute
entscheidet („ich mochte“ → „möchte“), brachte 0,4 Prozentpunkte, veränderte dort aber 1,1 % der Wörter und ist
deshalb nicht drin. Auch auf Sätzen aus Common Voice für Französisch, Spanisch, Italienisch und Polnisch steigen die
richtig korrigierten Tippfehler um 1,5 bis 2,8 Prozentpunkte.

Danach (Siezen, Tippmuster, ähnliche Wörter; 20 Durchläufe, Deutsch mit neuronalem Probemodell):

| | Deutsch vorher | Deutsch nachher | Englisch vorher | Englisch nachher |
| --- | --- | --- | --- | --- |
| Echtes Wort vertippt, gemeintes Wort nicht in der Leiste | 0,8 % | **0,4 %** | 2,4 % | **1,3 %** |
| Richtig getippte Wörter mit falscher Groß-/Kleinschreibung | 0,9 % | **0,5 %** | 1,0 % | 1,0 % |
| Falsche Wörter im Text nach der Korrektur | 3,7 % | **3,3 %** | 3,5 % | 3,5 % |
| Richtig getippte Wörter, die verändert wurden | 0,0 % | 0,0 % | 0,0 % | 0,0 % |
| Gesparte Tastendrücke | 53,6 % | 52,9 % | 49,7 % | 49,1 % |

1. **Siezen** (`WordContext.Formal`, `SuggestionEngine.ApplyFormal`): Der Brief in `samples/de.txt` steht dafür jetzt in
   einer Zeile wie in einer E-Mail (die Messung liest eine Zeile als ein Eingabefeld). In Common Voice sind nach einer
   förmlichen Anrede im selben Satz 80 % der weiteren „sie“ und rund 90 % der „ihre“, „Ihnen“ usw. förmlich. Ein
   Gegentest mit förmlichen Briefen, die über Dritte sprechen („meine Tochter hat ihre Prüfung …“), zeigt die Kehrseite:
   dort steigt die falsche Großschreibung von 4,1 auf 14 %.
2. **Ähnliche echte Wörter** (`RealWordBarRatio`): kosten etwa 0,6 Punkte gesparte Tastendrücke, weil das Nachbarwort
   manchmal eine Vervollständigung verdrängt.
3. **Tippmuster** (`TouchModel`, `--adapt`): Ohne systematische Abweichung ändert sich nichts (86,2 % → 86,1 %
   korrigiert). Mit simulierter Abweichung (`--bias 0.05,0.3 --thumbs 0.2`: 0,3 Tastenbreiten zu tief, Daumen ziehen
   zur Mitte) sinken die vertippten Wörter von 35,8 % auf 28,4 % und die falschen Wörter im Text von 5,0 % auf 3,7 %
   (Englisch 5,8 % → 4,2 %). Wie stark echte Finger abweichen, zeigt erst das Gerät.

Außerdem lädt die Messung die Wortliste jetzt mit Sprachangabe wie die App (vorher griffen sprachabhängige Regeln nicht).

## Grenzen

- Die Testsätze in `samples/` sind wenige (rund 1 000 Wörter je Sprache) und eigens geschrieben. Unterschiede unter
  etwa einem Prozentpunkt sind Zufall; mit `--runs 20` oder einem größeren Text wird es genauer.
- Die Tippfehler sind simuliert. Echte Finger treffen systematisch daneben (z. B. zu tief), das bildet die
  Normalverteilung nicht ab. Das gilt besonders für den Gewinn durch die Fingerpositionen: Die Korrektur nimmt
  dieselbe Art von Streuung an, mit der die Messung tippt. Auf dem Gerät kann der Gewinn deshalb kleiner ausfallen.
- Wischeingabe, mehrsprachige Eingabe und Gelerntes werden nicht gemessen.
