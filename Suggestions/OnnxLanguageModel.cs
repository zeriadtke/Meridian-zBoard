using Microsoft.ML.OnnxRuntime;

namespace ZBoard.Suggestions;

/// <summary>
/// Führt das neuronale Sprachmodell (&lt;Sprache&gt;.lm.onnx aus tools/LanguageModel/train_lm.py) mit ONNX Runtime aus.
/// Eingang „tokens“ (int64, [1, Länge]), Ausgang „probs“ (float, [1, Vokabular]): Wahrscheinlichkeit jedes Eintrags
/// als nächstes Wort. Gebraucht von der App und von tools/SuggestionEval; die Logik drumherum steht in NeuralModel.cs.
/// </summary>
public static class OnnxLanguageModel
{
    public const string InputName = "tokens";
    public const string OutputName = "probs";

    /// <summary>Lädt Netz und Vokabular und liefert die Funktion, die es ausführt.</summary>
    /// <param name="model">Inhalt der .onnx-Datei.</param>
    /// <param name="vocabulary">Die zugehörige .vocab.txt.</param>
    public static (NeuralVocabulary Vocabulary, NeuralRunner Run) Load(byte[] model, Stream vocabulary, string? language)
    {
        var vocab = NeuralVocabulary.Load(vocabulary, language);

        var options = new SessionOptions
        {
            // Ein Thread reicht für ein Netz dieser Größe (unter einer Millisekunde) und lässt der Tastatur den Rest.
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        var session = new InferenceSession(model, options);
        options.Dispose();

        var inputs = session.InputMetadata;
        var outputs = session.OutputMetadata;
        if (!inputs.ContainsKey(InputName) || !outputs.ContainsKey(OutputName))
        {
            session.Dispose();
            throw new InvalidDataException($"Sprachmodell ohne Eingang „{InputName}“ oder Ausgang „{OutputName}“");
        }

        var runOptions = new RunOptions();
        string[] inputNames = [InputName];
        string[] outputNames = [OutputName];

        float[] Run(long[] ids)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
            using var results = session.Run(runOptions, inputNames, [input], outputNames);
            var probs = results[0].GetTensorDataAsSpan<float>();
            return probs.ToArray();
        }

        // Kurzer Probelauf: findet ein falsches Vokabular sofort, nicht erst beim Tippen.
        int size = Run([NeuralVocabulary.SentenceStartId]).Length;
        if (size != vocab.Count)
        {
            session.Dispose();
            throw new InvalidDataException($"Sprachmodell hat {size} Ausgänge, das Vokabular {vocab.Count} Einträge");
        }

        return (vocab, Run);
    }
}
