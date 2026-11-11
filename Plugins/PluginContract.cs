namespace ZBoard.Plugins;

/// <summary>
/// Schnittstelle zwischen zBoard und Plugins (eigene Apps, z. B. „zBoard DeepL“). zBoard selbst hat keine
/// Internet-Berechtigung; braucht ein Plugin Internet, fragt nur dessen Installation danach.
/// Ablauf: zBoard bindet den Plugin-Dienst (Intent-Aktion <see cref="ServiceAction"/>) und schickt Messenger-Nachrichten;
/// das Plugin antwortet an <c>replyTo</c>. Wird auch in Plugin-Apps eingebunden – ohne Android-Abhängigkeiten.
/// Ausführlich: docs/PLUGINS.md.
/// </summary>
public static class PluginContract
{
    /// <summary>Intent-Aktion des Plugin-Dienstes.</summary>
    public const string ServiceAction = "de.World_of_Bytes.zBoard.PLUGIN";

    /// <summary>Meta-Daten am Dienst: Version der Schnittstelle (aktuell <see cref="ApiVersion"/>).</summary>
    public const string MetaApiVersion = "de.World_of_Bytes.zBoard.plugin.API_VERSION";

    public const int ApiVersion = 1;

    // ---------- Nachrichten (Message.what) ----------

    /// <summary>Welche Aktionen bietet das Plugin an? Antwort: <see cref="KeyActionIds"/>, <see cref="KeyActionLabels"/>.</summary>
    public const int MsgListActions = 1;

    /// <summary>Aktion auf Text ausführen. Anfrage: <see cref="KeyAction"/>, <see cref="KeyText"/>, <see cref="KeyLanguage"/>.
    /// Antwort: <see cref="KeyText"/> oder <see cref="KeyError"/>.</summary>
    public const int MsgRun = 2;

    // ---------- Bundle-Schlüssel ----------

    /// <summary>int: von zBoard vergeben, das Plugin gibt ihn in der Antwort unverändert zurück.</summary>
    public const string KeyRequestId = "requestId";

    /// <summary>int: Schnittstellenversion von zBoard (Anfrage) bzw. des Plugins (Antwort).</summary>
    public const string KeyApiVersion = "apiVersion";

    /// <summary>string[]: Kennungen der Aktionen, z. B. "translate:EN-US".</summary>
    public const string KeyActionIds = "actionIds";

    /// <summary>string[]: Beschriftungen in der Sprache des Geräts, z. B. "DeepL → Englisch".</summary>
    public const string KeyActionLabels = "actionLabels";

    public const string KeyAction = "action";

    /// <summary>string: Text (Anfrage: markierter Text bzw. Inhalt des Felds; Antwort: neuer Text).</summary>
    public const string KeyText = "text";

    /// <summary>string: aktuelle Tastatursprache, z. B. "de".</summary>
    public const string KeyLanguage = "language";

    /// <summary>string: Fehlermeldung für den Nutzer (statt <see cref="KeyText"/>).</summary>
    public const string KeyError = "error";

    /// <summary>Längster Text, den zBoard an ein Plugin schickt.</summary>
    public const int MaxTextLength = 5000;

    // ---------- Synchronisation (Plugins mit Meta-Daten MetaSync = true) ----------
    // Das Plugin ist nur der Transport zum Cloud-Speicher: zBoard stellt die Datei bereit, führt Daten zusammen und
    // übergibt die Datei über einen eigenen Content-Provider mit befristeter Lese- bzw. Schreibberechtigung (KeyUri).
    // Synchronisations-Plugins erscheinen nicht in der Leiste.

    /// <summary>Meta-Daten am Dienst (boolean, true): das Plugin kann synchronisieren.</summary>
    public const string MetaSync = "de.World_of_Bytes.zBoard.plugin.SYNC";

    /// <summary>Ist ein Speicher eingerichtet? Antwort: <see cref="KeySyncReady"/>, <see cref="KeySyncTarget"/>.</summary>
    public const int MsgSyncStatus = 10;

    /// <summary>Datei aus der Cloud in <see cref="KeyUri"/> schreiben. Antwort: <see cref="KeySyncFound"/> oder <see cref="KeyError"/>.</summary>
    public const int MsgSyncDownload = 11;

    /// <summary>Datei aus <see cref="KeyUri"/> in die Cloud laden (ersetzt die vorige). Antwort: leer oder <see cref="KeyError"/>.</summary>
    public const int MsgSyncUpload = 12;

    /// <summary>string: content://-URI der Datei, nur für die Dauer der Anfrage freigegeben.</summary>
    public const string KeyUri = "uri";

    /// <summary>bool: Speicher eingerichtet und angemeldet.</summary>
    public const string KeySyncReady = "syncReady";

    /// <summary>string: Anzeige für den Nutzer, z. B. "Dropbox" oder "HiDrive (patrick)".</summary>
    public const string KeySyncTarget = "syncTarget";

    /// <summary>bool: in der Cloud lag schon eine Datei (sonst ist dies das erste Gerät).</summary>
    public const string KeySyncFound = "syncFound";

    /// <summary>Name der Datei im Cloud-Speicher.</summary>
    public const string SyncFileName = "zboard-sync.zip";

    /// <summary>Größte Datei, die Plugin und zBoard austauschen.</summary>
    public const long MaxSyncBytes = 64L * 1024 * 1024;
}
