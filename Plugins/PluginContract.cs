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
}
