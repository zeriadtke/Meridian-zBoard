// zHeal Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
using Android.OS;
using Android.Views.InputMethods;
using ZBoard.Keyboard;
using ZBoard.Meridian;
using ZBoard.Meridian.Typing;

namespace ZBoard.Ime;

/// <summary>
/// Messung der Tipp-Dynamik für zHeal Meridian. Alles Meridian-Spezifische des Dienstes liegt in dieser Datei;
/// in ZBoardImeService.cs stehen nur einzelne, mit "Meridian:" markierte Aufrufe. So bleiben Merges von zBoard
/// (upstream) konfliktarm.
/// <para>
/// Datenschutz: Aus jedem Tastendruck wird nur die Tastenart abgeleitet (<see cref="KeyCategory"/>); Zeichen, Wörter,
/// Vorschläge und App-Namen werden weder gespeichert noch übertragen. In Passwortfeldern, Feldern, die "nicht
/// personalisiert lernen" verlangen, und Nicht-Textfeldern wird gar nicht gemessen (<see cref="TypingPrivacy"/>).
/// </para>
/// </summary>
public sealed partial class ZBoardImeService
{
    readonly TypingTracker _typing = new();
    MeridianBridge? _meridian;

    MeridianBridge MeridianLink => _meridian ??= new MeridianBridge(this);

    static long WallMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void MeridianStartInput(EditorInfo? info)
    {
        bool mayRecord = info != null && TypingPrivacy.MayRecord((int)info.InputType, (int)info.ImeOptions);
        Submit(_typing.Start(mayRecord, WallMs));
    }

    void MeridianFinishInput() => Submit(_typing.Finish(WallMs));

    void MeridianKeyDown(long uptimeMs) => _typing.KeyDown(uptimeMs);

    void MeridianKeyUp(KeyDef key, string? output, long uptimeMs, bool gesture) =>
        _typing.KeyUp(key, output, uptimeMs, gesture);

    /// <summary>Nur das Ereignis "automatisch korrigiert" zählt – die Wörter selbst werden nicht gespeichert.</summary>
    void MeridianAutoCorrected() => _typing.AutoCorrected();

    void MeridianSuggestionPicked() => _typing.SuggestionPicked();

    public override void OnFinishInput()
    {
        MeridianFinishInput();
        base.OnFinishInput();
    }

    void Submit(TypingSessionSummary? summary)
    {
        if (summary != null) MeridianLink.Submit(summary);
    }
}
