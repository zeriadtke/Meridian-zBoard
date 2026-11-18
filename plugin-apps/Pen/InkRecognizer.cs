using Android.Gms.Tasks;
using Xamarin.Google.MLKit.Common.Model;
using Xamarin.Google.MLKit.Vision.DigitalInk;
using Task = System.Threading.Tasks.Task;

namespace ZBoard.Pen;

/// <summary>Zustand des Schriftmodells einer Sprache.</summary>
public enum ModelState
{
    Unsupported,
    Missing,
    Downloading,
    Ready,
}

/// <summary>
/// Handschrifterkennung mit Google ML Kit (Digital Ink Recognition). Erkannt wird auf dem Gerät; das Schriftmodell
/// einer Sprache lädt ML Kit einmalig aus dem Internet. Erkenner bleiben je Sprache im Speicher, solange der Prozess
/// lebt (zBoard bindet den Dienst nur für jede Anfrage).
/// </summary>
public static class InkRecognizer
{
    static readonly Dictionary<string, IDigitalInkRecognizer> Recognizers = [];
    static readonly HashSet<string> Downloading = [];
    static readonly Lock Gate = new();

    /// <summary>Das ML-Kit-Modell zu einer zBoard-Sprache, oder null, wenn ML Kit sie nicht kennt.</summary>
    public static DigitalInkRecognitionModel? Model(string language)
    {
        foreach (var tag in PenLanguages.TagsFor(language))
        {
            try
            {
                var identifier = DigitalInkRecognitionModelIdentifier.FromLanguageTag(tag);
                if (identifier != null) return DigitalInkRecognitionModel.InvokeBuilder(identifier).Build();
            }
            catch (Java.Lang.Exception)
            {
                // ungültige Kennung: nächste probieren
            }
        }

        return null;
    }

    public static async Task<ModelState> StateAsync(string language)
    {
        var model = Model(language);
        if (model == null) return ModelState.Unsupported;
        lock (Gate)
        {
            if (Downloading.Contains(language)) return ModelState.Downloading;
        }

        var downloaded = await Await(RemoteModelManager.Instance.IsModelDownloaded(model));
        return downloaded is Java.Lang.Boolean { } b && b.BooleanValue() ? ModelState.Ready : ModelState.Missing;
    }

    /// <summary>Lädt das Modell (falls nötig); kehrt zurück, wenn es fertig ist.</summary>
    public static async Task DownloadAsync(string language)
    {
        var model = Model(language) ?? throw new InvalidOperationException("Sprache wird nicht unterstützt");
        lock (Gate)
        {
            if (!Downloading.Add(language)) return;
        }

        try
        {
            var conditions = new DownloadConditions.Builder().Build();
            await Await(RemoteModelManager.Instance.Download(model, conditions));
        }
        finally
        {
            lock (Gate) Downloading.Remove(language);
        }
    }

    /// <summary>Lädt das Modell im Hintergrund, ohne zu warten (Fehler werden beim nächsten Versuch neu gemeldet).</summary>
    public static void StartDownload(string language) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(language);
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("zBoardPen", $"Modell {language} nicht geladen: {e.Message}");
            }
        });

    public static async Task DeleteAsync(string language)
    {
        var model = Model(language);
        if (model == null) return;
        lock (Gate)
        {
            if (Recognizers.Remove(language, out var recognizer)) recognizer.Close();
        }

        await Await(RemoteModelManager.Instance.DeleteDownloadedModel(model));
    }

    /// <summary>Erkennt Text aus den Strichen; das Modell muss geladen sein.</summary>
    /// <param name="strokes">Je Strich die Punkte (x, y, Zeit in ms).</param>
    public static async Task<IReadOnlyList<string>> RecognizeAsync(
        string language, IReadOnlyList<IReadOnlyList<(float X, float Y, long T)>> strokes, float width, float height, string preContext)
    {
        IDigitalInkRecognizer recognizer;
        lock (Gate)
        {
            if (!Recognizers.TryGetValue(language, out recognizer!))
            {
                var model = Model(language) ?? throw new InvalidOperationException("Sprache wird nicht unterstützt");
                recognizer = DigitalInkRecognition.GetClient(DigitalInkRecognizerOptions.InvokeBuilder(model).Build());
                Recognizers[language] = recognizer;
            }
        }

        var ink = Ink.InvokeBuilder();
        foreach (var points in strokes)
        {
            if (points.Count == 0) continue;
            var stroke = Ink.Stroke.InvokeBuilder();
            foreach (var (x, y, t) in points) stroke.AddPoint(Ink.Point.Create(x, y, t));
            ink.AddStroke(stroke.Build());
        }

        var context = RecognitionContext.InvokeBuilder()
            .SetPreContext(preContext)
            .SetWritingArea(new WritingArea(width, height))
            .Build();

        var result = await Await(recognizer.Recognize(ink.Build(), context));
        if (result is not RecognitionResult recognition) return [];
        return [.. recognition.Candidates.Select(c => c.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(8)];
    }

    /// <summary>Wartet auf eine Google-Play-Aufgabe (Erfolg: Ergebnis, Fehler: Ausnahme).</summary>
    static Task<Java.Lang.Object?> Await(Android.Gms.Tasks.Task task)
    {
        var done = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.AddOnSuccessListener(new SuccessListener(done));
        task.AddOnFailureListener(new FailureListener(done));
        return done.Task;
    }

    sealed class SuccessListener(TaskCompletionSource<Java.Lang.Object?> done) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result) => done.TrySetResult(result);
    }

    sealed class FailureListener(TaskCompletionSource<Java.Lang.Object?> done) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception e) => done.TrySetException(e);
    }
}
