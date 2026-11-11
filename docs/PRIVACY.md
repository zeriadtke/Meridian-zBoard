# Datenschutzerklärung für zBoard

*Stand: November 2026 · English version below*

zBoard ist eine Tastatur-App für Android von World of Bytes, Patrick Zeriadtke (world-of-bytes.de).

## Kurz gesagt

zBoard erhebt, speichert und überträgt **keine** personenbezogenen Daten an World of Bytes oder an Dritte.
Die App hat keine Internet-Berechtigung und kann deshalb selbst nichts ins Internet senden.

## Was auf dem Gerät gespeichert wird

- **Gelernte Wörter und Wortfolgen**, damit Vorschläge zu deinem Schreibstil passen. Die Daten bleiben in dem
  geschützten App-Speicher auf deinem Gerät. In Passwort-, E-Mail-, Adress- und Zahlenfeldern sowie in Feldern, in
  denen die App das Lernen untersagt (Inkognito-Modus), lernt zBoard nichts. Du kannst das Lernen ausschalten und
  alles Gelernte in den Einstellungen unter „Wörterbuch“ löschen.
- **E-Mail-Adressen**, die du in E-Mail-Feldern eingibst, damit zBoard sie dort wieder vorschlagen kann (höchstens 50,
  im geschützten App-Speicher). Nicht in Inkognito-Fenstern und Feldern, in denen die App das Lernen untersagt.
  Einzelne Adressen entfernst du per langem Druck in der Vorschlagsleiste, alle unter „Wörterbuch“; dort lässt sich
  das Merken auch ausschalten.
- **Zwischenablage-Verlauf**: Kopierte Texte werden eine Stunde lang nur im Arbeitsspeicher gehalten. Gespeichert
  werden nur Einträge, die du selbst anheftest. Als vertraulich markierte Inhalte (z. B. aus Passwort-Managern)
  übernimmt zBoard nie. Der Verlauf lässt sich in den Einstellungen ausschalten.
- **Zuletzt verwendete Emoji** und deine **Einstellungen**.

Beim Deinstallieren der App löscht Android alle diese Daten.

## Sichern und wiederherstellen

Unter „Sichern & wiederherstellen“ kannst du selbst eine Sicherungsdatei erstellen (Einstellungen, gelernte Wörter,
E-Mail-Adressen, angeheftete Zwischenablage – jeweils abwählbar). Den Speicherort wählst du in der Dateiauswahl des
Systems; landet die Datei in einem Cloud-Speicher, gelten dessen Bedingungen. zBoard überträgt dabei nichts an World
of Bytes oder Dritte. Die Datei ist nicht verschlüsselt – bewahre sie entsprechend sorgfältig auf.

## Synchronisation (Plugin „zBoard Sync“)

Installierst du die separate App „zBoard Sync“ und schaltest die Synchronisation ein, übergibt zBoard deine
Einstellungen, gelernten Wörter und E-Mail-Adressen an diese App. Sie speichert sie als Datei in dem Cloud-Speicher,
den du gewählt hast (Google Drive, OneDrive, Dropbox oder ein WebDAV-Server wie HiDrive); es gilt dessen
Datenschutzerklärung. World of Bytes erhält nichts. Die Anmeldedaten bleiben verschlüsselt auf deinem Gerät; Abmelden
in zBoard Sync löscht sie. Um die Daten aus der Cloud zu entfernen, lösche dort die Datei `zboard-sync.zip` (bei
Google Drive: Drive-Einstellungen → Apps verwalten → zBoard Sync → Ausgeblendete App-Daten löschen).

## Spracheingabe

Wenn du auf das Mikrofon tippst, nutzt zBoard die Spracherkennung, die auf deinem Gerät installiert ist
(z. B. von Google oder dem Gerätehersteller). zBoard selbst zeichnet nichts auf und speichert keine Audiodaten.
Standardmäßig fordert zBoard eine Erkennung **auf dem Gerät** an. Schaltest du „Nur Offline-Erkennung“ aus, kann der
Erkennungsdienst die Sprache zur Verarbeitung an seinen Anbieter senden. Dafür gilt dessen Datenschutzerklärung.

Die Berechtigung für das Mikrofon fragt zBoard erst beim ersten Tippen auf das Mikrofon ab.

## Plugins

zBoard kann um Plugins erweitert werden – eigene Apps wie „zBoard DeepL“, die z. B. Internet zum Übersetzen nutzen.
zBoard selbst bekommt dadurch keinen Internetzugriff. Ein Plugin erhält nur Text, wenn du es in den Einstellungen
eingeschaltet hast und in der Leiste eine seiner Aktionen antippst (markierter Text oder Inhalt des Felds, nie aus
Passwortfeldern). Was das Plugin damit macht, regelt dessen eigene Datenschutzerklärung.

## Berechtigungen

| Berechtigung | Zweck |
| --- | --- |
| Mikrofon (`RECORD_AUDIO`) | Spracheingabe, nur auf deinen Wunsch |
| Vibration (`VIBRATE`) | Haptisches Feedback beim Tippen |

## Kontakt

World of Bytes, Patrick Zeriadtke · world-of-bytes.de

---

# Privacy policy for zBoard

*Last updated: November 2026*

zBoard is a keyboard app for Android by World of Bytes, Patrick Zeriadtke (world-of-bytes.de).

## In short

zBoard does **not** collect, store or transmit any personal data to World of Bytes or third parties.
The app has no internet permission, so it cannot send anything to the internet itself.

## What is stored on your device

- **Learned words and phrases**, so suggestions match the way you write. The data stays in the app's private storage
  on your device. zBoard learns nothing in password, email, address and number fields, or in fields where the app
  disallows learning (incognito mode). You can turn learning off and delete everything learned under
  "Dictionary" in the settings.
- **Email addresses** you enter in email fields, so zBoard can suggest them there again (at most 50, in the app's
  private storage). Not in incognito windows or fields where the app disallows learning. Remove a single address by
  long-pressing it in the suggestion bar, or all of them under "Dictionary", where you can also turn this off.
- **Clipboard history**: copied text is kept in memory only, for one hour. Only items you pin are saved.
  zBoard never takes content marked as sensitive (e.g. from password managers). You can turn the history off.
- **Recently used emoji** and your **settings**.

Android deletes all of this data when you uninstall the app.

## Backup and restore

Under "Backup & restore" you can create a backup file yourself (settings, learned words, email addresses, pinned
clipboard items – each optional). You choose where it is saved in the system file picker; if it ends up in a cloud
storage service, that service's terms apply. zBoard sends nothing to World of Bytes or third parties. The file is not
encrypted, so keep it somewhere safe.

## Sync (plugin "zBoard Sync")

If you install the separate app "zBoard Sync" and turn on sync, zBoard hands your settings, learned words and email
addresses to that app, which stores them as a file in the cloud storage you chose (Google Drive, OneDrive, Dropbox or
a WebDAV server such as HiDrive). That provider's privacy policy applies. World of Bytes receives nothing. The sign-in
data stays encrypted on your device; signing out in zBoard Sync deletes it. To remove the data from the cloud, delete
the file `zboard-sync.zip` there (for Google Drive: Drive settings → Manage apps → zBoard Sync → Delete hidden data).

## Voice typing

When you tap the microphone, zBoard uses the speech recognition installed on your device (e.g. from Google or your
device manufacturer). zBoard itself does not record or store any audio. By default zBoard requests **on-device**
recognition. If you turn off "Offline recognition only", the recognition service may send your speech to its
provider for processing; that provider's privacy policy applies.

zBoard asks for the microphone permission only when you first tap the microphone.

## Plugins

zBoard can be extended with plugins – separate apps such as “zBoard DeepL” that use e.g. the internet to translate.
zBoard itself does not gain internet access. A plugin only receives text when you have turned it on in the settings
and tap one of its actions in the bar (the selected text or the field's content, never from password fields). What
the plugin does with it is governed by its own privacy policy.

## Permissions

| Permission | Purpose |
| --- | --- |
| Microphone (`RECORD_AUDIO`) | Voice typing, only when you ask for it |
| Vibration (`VIBRATE`) | Haptic feedback while typing |

## Contact

World of Bytes, Patrick Zeriadtke · world-of-bytes.de
