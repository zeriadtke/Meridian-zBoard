# Meridian Tastatur

> **Umbenennung zHeal → zHealth (November 2026):** App-Kennung, Berechtigung und ContentProvider heißen jetzt
> `de.World_of_Bytes.zHealth.Meridian.*`. Für Android ist das eine neue App – alte Installationen deinstallieren.
> Nur zusammen mit der Meridian-App ab derselben Umbenennung ausliefern, sonst kommen keine Tipp-Daten an.
> Standard-Keystore ist jetzt `C:\keys\zhealth-meridian.jks` mit Alias `zhealth`.


Fork von [zBoard](https://github.com/World-of-Bytes/zBoard) (BSD 2-Clause) mit einer Erweiterung, die die
**Tipp-Dynamik** für zHealth Meridian misst – nach dem Vorbild der BiAffect-Forschung. Gemessen werden **nur Zeiten
und Tastenarten**, nie Zeichen. Ersetzt die frühere Meridian-Tastatur auf Basis von AnySoftKeyboard
(Repo `Meridian-Keyboard`, archiviert); Paketname und Datenschnittstelle sind gleich geblieben, die Meridian-App
braucht deshalb keine Änderung.

## Was gemessen wird – und was nicht

Je Eingabesitzung (ein Textfeld von Öffnen bis Schließen) entsteht eine Zusammenfassung:

| Wert | Bedeutung |
| --- | --- |
| `total_keys`, `alnum_keys`, `backspace_keys`, `space_keys`, `enter_keys`, `punct_keys` | Anzahl je Tastenart |
| `iki_median_ms`, `iki_mean_ms`, `iki_sd_ms`, `iki_p90_ms` | Zeit zwischen zwei Buchstaben/Ziffern (Tippgeschwindigkeit, Gleichmäßigkeit) |
| `hold_median_ms` | Haltedauer einer Taste |
| `pauses` | Pausen ≥ 2 s |
| `active_ms` | reine Schreibzeit (Lücken ≥ 5 s zählen nicht) |
| `autocorrections`, `suggestions_picked` | Anzahl automatischer Korrekturen bzw. gewählter Vorschläge |
| `started_ms`, `ended_ms` | Zeitpunkt der Sitzung |

Rechenregeln wie `TypingSessionRecorder` in Meridian.Core (und wie die frühere Tastatur), damit die Werte über
Geräte und Tastatur-Versionen hinweg dasselbe bedeuten.

**Nie** gespeichert oder übertragen: Zeichen, Wörter, Vorschläge, Inhalte, App-Namen, Feldnamen.
**Gar nicht** gemessen wird in Passwort- und PIN-Feldern, in Feldern mit `IME_FLAG_NO_PERSONALIZED_LEARNING`
(private Tabs, Passwortmanager) und in Nicht-Text-Feldern (Zahlen, Telefon, Datum). Sitzungen mit weniger als
5 Tasten werden verworfen.

**Besonderheiten von zBoard:**
- Gezählt wird beim Loslassen, weil erst dann feststeht, was der Druck war. Für Abstände und Pausen zählt der
  Zeitpunkt des Drückens.
- Zeichen aus dem Popup (langer Druck, z. B. „ä“ auf „a“) zählen als Taste, aber ohne Haltedauer.
- **Wischeingabe** (Glide Typing) liefert keine Einzeltasten und fließt nicht ein; ein danach angetippter
  Alternativvorschlag zählt unter `suggestions_picked`.
- **Wort-Löschen per Wischen** von ⌫ zählt als eine Löschtaste (ohne Haltedauer); Halten von ⌫ (Wiederholung)
  zählt ebenfalls als eine.
- Cursor-Bewegung per Wischen über die Leertaste, Popup ohne Auswahl, Spracheingabe, Emoji aus dem Emoji-Feld und
  Einfügen aus der Zwischenablage zählen nicht.

Die Tastatur speichert selbst nichts dauerhaft. Sie übergibt die Zusammenfassung an die Meridian-App
(ContentProvider `de.World_of_Bytes.zHealth.Meridian.typing`, `/status` und `/sessions`). Meridian nimmt sie nur an,
wenn die Quelle „Tipp-Dynamik“ freigegeben ist; sonst fragt die Tastatur nur den Status ab.

## Aufbau – warum Upstream-Merges einfach bleiben

| Datei | Zweck |
| --- | --- |
| `Meridian/Typing/*.cs` | Messung ohne Android-Abhängigkeiten (unit-getestet in `tests/Meridian.Keyboard.Tests`) |
| `Meridian/MeridianBridge.cs` | Übergabe an die Meridian-App |
| `Meridian/ZBoardImeService.Meridian.cs` | Teil der partiellen Klasse `ZBoardImeService` mit allen Meridian-Methoden |
| `Directory.Build.targets` | App-Kennung `de.World_of_Bytes.zHealth.Meridian.Keyboard`, Versionsnummer, Signierung – überschreibt `ZBoard.csproj`, ohne es zu ändern |
| `.github/workflows/meridian.yml` | eigener CI-Lauf auf dem selbst gehosteten Windows-Runner |

Geänderte zBoard-Dateien (jede Stelle mit `Meridian:` markiert):

| Datei | Änderung |
| --- | --- |
| `Ime/ZBoardImeService.cs` | Name des Dienstes, sechs einzeilige Aufrufe (Sitzung, Taste gedrückt/losgelassen, Autokorrektur, Vorschlag) |
| `AndroidManifest.xml` | Berechtigung `…permission.TYPING_DATA`, Paketsichtbarkeit des Providers, App-Name |
| `Settings/SettingsActivity.cs`, `Resources/values/strings.xml` | App-Name „Meridian Tastatur“ |

Die Einstellungen der Tastatur selbst heißen weiter „zBoard“.

**Versionsnummer:** `versionCode` = 1 000 000 + versionCode von zBoard. Damit liegt jede Version über der früheren
AnySoftKeyboard-Tastatur mit gleicher Kennung. Sicherer ist trotzdem, die alte Tastatur vor der ersten Installation
zu deinstallieren.

## Upstream übernehmen

```bash
git remote add upstream https://github.com/World-of-Bytes/zBoard.git
git remote set-url --push upstream DISABLE-push-to-upstream
git fetch upstream
git merge upstream/main          # Konflikte höchstens an den markierten Zeilen
dotnet test tests/Meridian.Keyboard.Tests
git push origin main
```

## Bauen

Voraussetzungen wie zBoard: .NET 10 SDK mit Android-Workload, Android SDK, JDK 21.

```bash
dotnet test tests/Meridian.Keyboard.Tests
dotnet build ZBoard.csproj -c Release -f net10.0-android -p:AndroidPackageFormats=apk
# APK: bin/Release/net10.0-android/de.World_of_Bytes.zHealth.Meridian.Keyboard-Signed.apk
```

**Signierung:** Die Tastatur muss mit **demselben Schlüssel wie die Meridian-App** signiert sein. Meridian schützt
seine Datenschnittstelle mit einer Berechtigung der Stufe `signature`; mit anderem Schlüssel lehnt Android den
Zugriff ab, und die Tastatur verwirft die Daten stillschweigend.

```
MERIDIAN_KEYSTORE=C:\keys\zhealth-meridian.jks
MERIDIAN_KEYSTORE_PASSWORD=…
MERIDIAN_KEY_ALIAS=zhealth          # optional, Standard "zhealth"
MERIDIAN_KEY_PASSWORD=…           # optional, Standard = Keystore-Passwort
```

Dieselben Variablen liest `Meridian.App.csproj`. In der CI kommen sie aus dem Repository-Secret
`MERIDIAN_KEYSTORE_PASSWORD` (Pfad und Alias optional als Repository-Variablen); die signierte APK wird nach
`%USERPROFILE%\zhealth-apks\zhealth-meridian-keyboard.apk` kopiert und als Artefakt `Meridian-Keyboard-apk`
hochgeladen. Ohne diese Variablen wird mit dem zBoard-Testschlüssel signiert (Build-Warnung) – dann kommen bei
Meridian keine Daten an.

Ein eigenes deutsches Sprachpaket ist nicht mehr nötig: zBoard bringt Deutsch, Englisch, Französisch, Spanisch
und Italienisch mit.

## Lizenz

zBoard steht unter der BSD 2-Clause License (`LICENSE`), die Wortlisten unter CC BY-SA 4.0 / CC BY 3.0
(`THIRD_PARTY_NOTICES.md`). Die Hinweise bleiben erhalten.
