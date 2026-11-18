using System.Diagnostics;

namespace ZBoard.Testing;

/// <summary>
/// Zeitmessung für den Emulator-Test: schreibt „PERF name millisekunden“ ins Log (Tag "zBoardTest"). Nur in
/// Debug-Versionen und mit Testhaken; in der Store-Version entfallen die Aufrufe samt ihrer Argumente.
/// </summary>
static class Perf
{
    public static long Now => Stopwatch.GetTimestamp();

    public static double Since(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    /// <summary>Millisekunden seit dem Start des Prozesses (Android zählt ab dem Fork, also samt Start der .NET-Laufzeit).</summary>
    public static double SinceProcessStart =>
        Android.OS.SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;

    [Conditional("DEBUG"), Conditional("ZBOARD_TESTHOOKS")]
    public static void Log(string name, double milliseconds) =>
        Android.Util.Log.Info("zBoardTest", FormattableString.Invariant($"PERF {name} {milliseconds:F1}"));
}
