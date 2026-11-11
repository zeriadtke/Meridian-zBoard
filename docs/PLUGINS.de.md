# zBoard-Plugins

🇬🇧 [English](PLUGINS.md) | 🇩🇪 **Deutsch**

zBoard hat bewusst **keine Internet-Berechtigung**. Funktionen, die Internet brauchen (Übersetzen, KI-Umformulieren,
Online-Wörterbücher …), kommen als **eigene Plugin-Apps**, z. B. aus dem Play Store. Nur die Installation des Plugins
fragt nach Internet – wer kein Plugin installiert, hat eine Tastatur ohne Internet.

Plugins: **zBoard DeepL** (`plugin-apps/DeepL`) – übersetzt den markierten Text oder den Inhalt des Felds;
**zBoard Sync** (`plugin-apps/Sync`) – gleicht Einstellungen und Gelerntes über Google Drive, OneDrive, Dropbox oder
WebDAV (HiDrive, Nextcloud …) zwischen Geräten ab.

## Für Nutzer

1. Plugin installieren (z. B. „zBoard DeepL“) und dort einrichten (API-Schlüssel, Zielsprachen).
2. zBoard-Einstellungen → **Plugins** → Plugin einschalten. Vorher bekommt es nie Text zu sehen.
3. In der Leiste über der Tastatur das **Puzzle-Symbol** antippen und eine Aktion wählen. Das Plugin bekommt den
   markierten Text, ohne Markierung den ganzen Feldinhalt (höchstens 5 000 Zeichen), und ersetzt ihn.
   **↶ Rückgängig** stellt das Original wieder her.

In Passwortfeldern bietet zBoard keine Plugins an. Text geht nur auf einen ausdrücklichen Fingertipp an ein Plugin.

## Für Entwickler: Schnittstelle (Version 1)

Ein Plugin ist eine Android-App mit einem **exportierten Dienst**:

```xml
<service android:name=".PluginService" android:exported="true" android:label="Mein Plugin">
  <intent-filter>
    <action android:name="de.World_of_Bytes.zBoard.PLUGIN" />
  </intent-filter>
  <meta-data android:name="de.World_of_Bytes.zBoard.plugin.API_VERSION" android:value="1" />
</service>
```

zBoard bindet den Dienst nur für die Dauer einer Anfrage und spricht über einen `Messenger` mit ihm. Jede Anfrage ist
eine `Message` mit `what`, einem `Bundle` als `data` und `replyTo`; das Plugin antwortet mit demselben `what` an
`replyTo` und gibt `requestId` unverändert zurück. Konstanten: `Plugins/PluginContract.cs` (ohne Android-Abhängigkeit,
lässt sich direkt in Plugin-Projekte einbinden).

| `what` | Anfrage (`data`) | Antwort |
| --- | --- | --- |
| `1` Aktionen auflisten | `requestId`, `apiVersion` | `actionIds` (string[]), `actionLabels` (string[], in Gerätesprache) |
| `2` Aktion ausführen | `requestId`, `apiVersion`, `action`, `text`, `language` (z. B. `de`) | `text` (neuer Text) **oder** `error` (Meldung für den Nutzer) |

Zeitlimits in zBoard: 5 s für die Verbindung und die Aktionsliste, 20 s für eine Aktion. Höchstens 8 Aktionen je Plugin.

### Synchronisations-Plugins

Ein Plugin mit zusätzlicher Meta-Angabe `de.World_of_Bytes.zBoard.plugin.SYNC` = `true` ist nur Transport zu einem
Cloud-Speicher. Es erscheint nicht in der Leiste; der Nutzer wählt es unter Einstellungen → Sichern & wiederherstellen.
zBoard stellt die Datei (ZIP, Format der Sicherung) über seinen nicht exportierten Content-Provider
`content://de.World_of_Bytes.zBoard.sync/…` bereit und gibt sie dem Plugin nur für die Dauer einer Anfrage frei
(`grantUriPermission`). Das Zusammenführen macht zBoard.

| `what` | Anfrage (`data`) | Antwort |
| --- | --- | --- |
| `10` Zustand | `requestId`, `apiVersion` | `syncReady` (bool), `syncTarget` (Anzeige, z. B. „Dropbox“) |
| `11` Herunterladen | `uri` (Schreibrecht) | `syncFound` (bool: es gab schon eine Datei) **oder** `error` |
| `12` Hochladen | `uri` (Leserecht) | leer **oder** `error` |

Zeitlimit 90 s je Übertragung, höchstens 64 MB. Beispiel: `plugin-apps/Sync` (README dort mit der Einrichtung der
Anbieter).

### Sicherheit

- **Nutzer entscheidet:** zBoard findet Plugins über die Intent-Aktion, nutzt aber nur die in den Einstellungen
  eingeschalteten. Beim Einschalten zeigt zBoard, ob das Plugin Internet nutzt und ob es vom selben Entwickler
  (gleiche Signatur) stammt.
- **Plugin prüft den Aufrufer:** Ein Plugin sollte nur der gerade aktiven Systemtastatur antworten
  (`Message.sendingUid` → Paket mit `Settings.Secure.DEFAULT_INPUT_METHOD` vergleichen), damit andere Apps es nicht
  benutzen können. zBoard DeepL macht das so.
- **Kein Hintergrundzugriff:** zBoard schickt Text nur auf Fingertipp und nie aus Passwortfeldern; vor dem Ersetzen
  prüft es, dass der Text im Feld unverändert ist.

### Beispiel

`plugin-apps/DeepL` – .NET-10-Android-App in C#: `PluginService.cs` (Dienst), `DeepLClient.cs` (DeepL-API),
`MainActivity.cs` (Einrichtung).

```bash
dotnet build plugin-apps/DeepL/ZBoard.DeepL.csproj -t:Run -f net10.0-android
```
