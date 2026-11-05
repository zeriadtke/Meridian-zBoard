// zHeal Meridian – Tipp-Dynamik. Nicht Teil von zBoard (upstream), siehe Meridian/README.md.
namespace ZBoard.Meridian.Typing;

/// <summary>
/// Wann gar nicht gemessen wird – auch nicht die Zeiten. Arbeitet auf den rohen Werten von
/// <c>EditorInfo.inputType</c> und <c>EditorInfo.imeOptions</c> (Konstanten aus der Android-Doku), damit es ohne
/// Android testbar ist.
/// </summary>
public static class TypingPrivacy
{
    const int TypeMaskClass = 0x0000000f;
    const int TypeClassText = 0x00000001;
    const int TypeMaskVariation = 0x00000ff0;
    const int TextVariationPassword = 0x00000080;
    const int TextVariationVisiblePassword = 0x00000090;
    const int TextVariationWebPassword = 0x000000e0;
    const int ImeFlagNoPersonalizedLearning = 0x01000000;

    /// <summary>
    /// Gemessen wird nur in Textfeldern. Nie in Passwortfeldern (selbst der Tipp-Rhythmus wäre schützenswert), nie
    /// in Feldern mit <c>IME_FLAG_NO_PERSONALIZED_LEARNING</c> (private Tabs, Passwortmanager) und nie in Zahlen-,
    /// PIN-, Telefon- und Datumsfeldern (kein Fließtext, nicht aussagekräftig).
    /// </summary>
    public static bool MayRecord(int inputType, int imeOptions)
    {
        if ((imeOptions & ImeFlagNoPersonalizedLearning) != 0) return false;
        int cls = inputType & TypeMaskClass;
        if (cls != TypeClassText) return false;
        int variation = inputType & TypeMaskVariation;
        return variation is not (TextVariationPassword or TextVariationVisiblePassword or TextVariationWebPassword);
    }
}
