using Android.Content;
using Android.Views.InputMethods;
using ZBoard.Keyboard;
using ZBoard.Suggestions;

namespace ZBoard.Ime;

/// <summary>
/// E-Mail-Adressen wie bei Gboard: In E-Mail-Feldern schlägt die Leiste gemerkte Adressen vor – schon im leeren Feld
/// und passend zum Angefangenen, nach "@" auch bekannte Anbieter. Gemerkt wird, was beim Verlassen des Felds
/// (oder bei Enter) als gültige Adresse darin steht. Alles bleibt im app-privaten Speicher.
/// </summary>
public sealed partial class ZBoardImeService
{
    static readonly Lock EmailLock = new();
    static EmailMemory? s_emails;

    string? _emailInitialText;   // Feldinhalt beim Öffnen: Unverändertes wird nicht erneut gezählt
    string? _emailLearnedText;   // schon gemerkt (Enter und anschließendes Schließen zählen einmal)
    string? _pendingEmailRemoval; // langer Druck: wartet auf Bestätigung in der Leiste

    static string EmailFile(Context context) =>
        Path.Combine(context.FilesDir!.AbsolutePath, "learning", "emails.tsv");

    static EmailMemory Emails(Context context)
    {
        lock (EmailLock)
        {
            if (s_emails != null) return s_emails;
            try
            {
                var path = EmailFile(context);
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    s_emails = EmailMemory.Load(stream);
                }
            }
            catch (IOException)
            {
                // Datei unlesbar: neu anfangen
            }

            return s_emails ??= new EmailMemory();
        }
    }

    /// <summary>Löscht alle gemerkten E-Mail-Adressen. Aufgerufen aus den Einstellungen.</summary>
    public static void ClearEmails(Context context)
    {
        lock (EmailLock)
        {
            s_emails?.Clear();
            var path = EmailFile(context);
            if (File.Exists(path)) File.Delete(path);
            s_emails = new EmailMemory();
        }
    }

    /// <summary>Anzahl gemerkter Adressen (für die Einstellungen).</summary>
    public static int EmailCount(Context context) => Emails(context).Count;

    bool EmailLearningAllowed =>
        _settings.RememberEmails && _profile.IsEmail && !_profile.IsPassword && !_profile.NoPersonalizedLearning;

    void StartEmailInput()
    {
        _pendingEmailRemoval = null;
        _emailLearnedText = null;
        _emailInitialText = _profile.IsEmail ? FieldText() : null;
    }

    /// <summary>Gültige Adressen aus dem Feld merken, wenn sich der Inhalt seit dem Öffnen geändert hat.</summary>
    void LearnEmailsFromField()
    {
        if (!EmailLearningAllowed) return;
        string? text = FieldText();
        if (string.IsNullOrWhiteSpace(text) || text == _emailInitialText || text == _emailLearnedText) return;

        var emails = Emails(this);
        if (emails.LearnFrom(text, NowMs()) > 0) _emailLearnedText = text;
    }

    void SaveEmails()
    {
        var emails = s_emails;
        if (emails is not { IsDirty: true }) return;
        var path = EmailFile(this);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        emails.Save(stream);
    }

    /// <summary>Ganzer Feldinhalt (bei sehr langen Feldern nur die Umgebung des Cursors).</summary>
    string? FieldText()
    {
        var ic = Ic;
        if (ic == null) return null;
        var extracted = ic.GetExtractedText(new ExtractedTextRequest { HintMaxChars = 2000 }, 0);
        if (extracted?.Text is { } full) return full.ToString();
        return TextBeforeCursor(1000) + (ic.GetTextAfterCursorFormatted(1000, 0)?.ToString() ?? string.Empty);
    }

    static bool IsEmailChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '_' or '%' or '+' or '-' or '@';

    /// <summary>
    /// Das angefangene Adress-Stück vor dem Cursor (inklusive des Worts in Bearbeitung) und wie viel davon
    /// schon fest im Feld steht. Manche Apps (Browser) melden den Text in Bearbeitung verzögert; das wird ausgeglichen.
    /// </summary>
    (string Token, int CommittedLength) EmailToken()
    {
        string before = TextBeforeCursor(128);
        string composing = _composing.ToString();
        for (int k = composing.Length; k >= 0; k--)
        {
            if (before.EndsWith(composing[..k], StringComparison.Ordinal))
            {
                before = before[..^k];
                break;
            }
        }

        int start = before.Length;
        while (start > 0 && IsEmailChar(before[start - 1])) start--;
        string committed = before[start..];
        return (committed + composing, committed.Length);
    }

    /// <summary>Zeigt Adress-Vorschläge, wenn es passende gibt. false: normale Vorschläge anzeigen.</summary>
    bool TryShowEmailSuggestions()
    {
        _pendingEmailRemoval = null; // jede neue Eingabe bricht die Rückfrage ab
        if (_suggestionBar == null || _profile.IsPassword || !_profile.AllowSuggestions) return false;

        bool emailField = _profile.IsEmail;
        if (!emailField && (!_settings.RememberEmails || Emails(this).Count == 0)) return false;
        var (token, _) = EmailToken();
        // In normalen Textfeldern nur, wenn gerade eine Adresse entsteht und eine gemerkte dazu passt
        if (!emailField && !token.Contains('@')) return false;

        IReadOnlyList<EmailSuggestion> suggestions = Emails(this).Suggest(token);
        if (!_settings.RememberEmails) suggestions = [.. suggestions.Where(s => s.Display != s.Address)];
        if (!emailField) suggestions = [.. suggestions.Where(s => s.Display == s.Address)];
        if (suggestions.Count == 0) return false;

        _currentSuggestions = SuggestionResult.Empty;
        SuggestionSlot? Slot(int i) => i < suggestions.Count
            ? new SuggestionSlot(suggestions[i].Display, suggestions[i].Address, i == 0, false, IsEmail: true)
            : null;
        _suggestionBar.SetSlots(Slot(0), Slot(1), Slot(2));
        return true;
    }

    /// <summary>Ersetzt das angefangene Stück durch die gewählte Adresse (ohne Leerzeichen dahinter).</summary>
    void PickEmail(SuggestionSlot slot)
    {
        var ic = Ic;
        if (ic == null) return;
        MeridianSuggestionPicked(); // Meridian: nur das Ereignis

        var (_, committedLength) = EmailToken();
        ic.BeginBatchEdit();
        if (_composing.Length > 0) ic.SetComposingText(J(string.Empty), 1);
        if (committedLength > 0) ic.DeleteSurroundingText(committedLength, 0);
        ic.CommitText(J(slot.Word), 1);
        ic.EndBatchEdit();

        _composing.Clear();
        _glideActive = false;
        _lastCorrection = null;
        _autoSpaceInserted = false;
        if (_shift == ShiftState.OneShot) SetShift(ShiftState.Off, manual: false);
        ShowToolbar();
    }

    // ---------- Entfernen per langem Druck (wie Gboard) ----------

    void OnSuggestionLongPressed(SuggestionSlot slot)
    {
        if (!slot.IsEmail || _suggestionBar == null) return;
        _pendingEmailRemoval = slot.Word;
        _suggestionBar.SetRemoveConfirm(slot.Word);
    }

    void RemovePendingEmail()
    {
        var address = _pendingEmailRemoval;
        _pendingEmailRemoval = null;
        if (address != null && Emails(this).Remove(address))
        {
            try
            {
                SaveEmails(); // sofort, damit die Adresse auch nach einem Absturz weg bleibt
            }
            catch (IOException)
            {
                // wird beim nächsten Schließen der Tastatur erneut versucht
            }
        }

        UpdateSuggestions();
    }
}
