using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ZBoard.DeepL;

/// <summary>Fehler, deren Meldung der Nutzer zu sehen bekommt.</summary>
public sealed class DeepLException(string message) : Exception(message);

/// <summary>
/// Übersetzt über die DeepL-API (POST /v2/translate). Schlüssel der kostenlosen API enden auf ":fx" und gehen an
/// api-free.deepl.com, alle anderen an api.deepl.com.
/// </summary>
public static class DeepLClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static bool German => Java.Util.Locale.Default.Language == "de";

    static string T(string german, string english) => German ? german : english;

    public static async Task<string> TranslateAsync(string apiKey, string text, string targetLang, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new DeepLException(T("Kein DeepL-API-Schlüssel – bitte in der App „zBoard DeepL“ eintragen.",
                "No DeepL API key – please enter it in the “zBoard DeepL” app."));
        }

        string host = apiKey.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate");
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        request.Headers.UserAgent.ParseAdd("zBoard-DeepL/1.0");
        request.Content = new StringContent(RequestBody(text, targetLang), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (HttpRequestException)
        {
            throw new DeepLException(T("DeepL ist nicht erreichbar – besteht eine Internetverbindung?",
                "Cannot reach DeepL – are you online?"));
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new DeepLException(T("DeepL hat zu lange gebraucht.", "DeepL took too long."));
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode) throw new DeepLException(ErrorMessage(response.StatusCode));
            return ParseTranslation(body)
                   ?? throw new DeepLException(T("Unerwartete Antwort von DeepL.", "Unexpected response from DeepL."));
        }
    }

    /// <summary>{"text":["…"],"target_lang":"DE"} – ohne Reflection, damit es auch getrimmt funktioniert.</summary>
    public static string RequestBody(string text, string targetLang)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartArray("text");
            json.WriteStringValue(text);
            json.WriteEndArray();
            json.WriteString("target_lang", targetLang);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Text der ersten Übersetzung aus {"translations":[{"detected_source_language":"EN","text":"…"}]}.</summary>
    public static string? ParseTranslation(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
            {
                return null;
            }

            return translations[0].TryGetProperty("text", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string ErrorMessage(HttpStatusCode status) => (int)status switch
    {
        403 or 401 => T("DeepL lehnt den API-Schlüssel ab.", "DeepL rejected the API key."),
        456 => T("Das DeepL-Kontingent für diesen Monat ist aufgebraucht.", "Your DeepL quota for this month is used up."),
        429 => T("Zu viele Anfragen an DeepL – bitte kurz warten.", "Too many requests to DeepL – please wait a moment."),
        413 => T("Der Text ist für DeepL zu lang.", "The text is too long for DeepL."),
        >= 500 => T("DeepL hat gerade Probleme – bitte später erneut versuchen.", "DeepL is having problems – please try again later."),
        _ => T($"DeepL meldet einen Fehler ({(int)status}).", $"DeepL reported an error ({(int)status})."),
    };
}
