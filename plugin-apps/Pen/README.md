# zBoard Pen

Plugin-App für zBoard: **Handschrift**. Man schreibt mit Finger oder Stift auf der Schreibfläche von zBoard,
zBoard Pen erkennt den Text mit **Google ML Kit** (Digital Ink Recognition, dieselbe Technik wie in Gboard) auf dem
Gerät. zBoard selbst bleibt ohne Internet-Berechtigung; nur diese App hat sie – zum Laden der Schriftmodelle.

## So funktioniert es

- zBoard zeigt die Schreibfläche (Werkzeugleiste → Stift) und schickt nach jedem Strich alle Striche des aktuellen
  Worts, die Sprache der Tastatur und bis zu 20 Zeichen vor dem Cursor an das Plugin (Schnittstelle: `MsgInkRecognize`
  in `Plugins/PluginContract.cs`, beschrieben in `docs/PLUGINS.de.md`).
- Das Plugin antwortet mit bis zu acht Vorschlägen. Fehlt das Schriftmodell der Sprache, lädt es das Modell (etwa
  20 MB) und meldet `inkLoading`; zBoard fragt dann alle zwei Sekunden erneut.
- Es antwortet nur der aktiven Systemtastatur. Die Striche bleiben auf dem Gerät.
- In der App selbst lassen sich die Modelle der 18 zBoard-Sprachen laden und löschen.

## Abhängigkeit

`Xamarin.Google.MLKit.DigitalInk.Recognition` (ML Kit 18.1, Apache License 2.0 für die Bindung; ML Kit selbst unter
den Nutzungsbedingungen von Google). ML Kit kann anonyme Nutzungsdaten an Google senden – das steht in der App, in
`docs/PRIVACY.md` und muss im Play-Store-Eintrag (Datensicherheit) von zBoard Pen angegeben werden.

## Bauen

```bash
dotnet build plugin-apps/Pen/ZBoard.Pen.csproj -t:Run -f net10.0-android
```
