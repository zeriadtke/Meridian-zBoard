using System.Text;
using ZBoard.Suggestions;
using Xunit;

namespace ZBoard.Core.Tests;

/// <summary>Laden der Wortlisten: aus der Textdatei und aus der vorbereiteten Fassung (Zwischenspeicher der App).</summary>
public class DictionaryLoadingTests
{
    static WordDictionary FromText(string text, long? normalizeTo = null, string? language = null) =>
        WordDictionary.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)), normalizeTo, language);

    static WordDictionary RoundTrip(WordDictionary dictionary)
    {
        var stream = new MemoryStream();
        dictionary.Save(stream);
        stream.Position = 0;
        return WordDictionary.LoadCompiled(stream, dictionary.Language);
    }

    [Fact]
    public void Load_ReadsWordsWithAnyLineEndingAndSkipsComments()
    {
        var dictionary = FromText("﻿# Kopfzeile\nhaus\t50\r\nHaus\t200\r\n\r\n  baum \t 30 \nohne\nzwei wörter 7\n#ende");

        Assert.Equal(4, dictionary.Count);
        Assert.Equal(("Haus", 200), dictionary.Lookup("haus"));
        Assert.Equal(["Haus", "haus"], dictionary.Forms("HAUS"));
        Assert.Equal(("baum", 30), dictionary.Lookup("baum"));
        Assert.Equal(("ohne", 1), dictionary.Lookup("ohne")); // ohne Häufigkeit zählt 1
        Assert.Equal(("zwei", 1), dictionary.Lookup("zwei")); // "wörter 7" ist keine Zahl
        Assert.Equal(282, dictionary.TotalFrequency);
    }

    [Fact]
    public void Load_KeepsTheHigherFrequencyOfARepeatedSpelling()
    {
        var dictionary = FromText("frage\t10\nFrage\t40\nfrage\t60\nFrage\t5\nfragen\t20");

        Assert.Equal(["frage", "Frage"], dictionary.Forms("frage"));
        Assert.Equal(("frage", 60), dictionary.Lookup("Frage"));
        Assert.Equal(120, dictionary.TotalFrequency);
        Assert.Equal([("frage", 60), ("fragen", 20)], dictionary.Complete("fr"));
    }

    [Fact]
    public void Load_SameFrequencyKeepsTheOrderOfTheList()
    {
        var dictionary = FromText("Essen\t10\nessen\t10\nESSEN\t10");

        Assert.Equal(["Essen", "essen", "ESSEN"], dictionary.Forms("essen"));
    }

    [Fact]
    public void Compiled_RoundTripKeepsEverything()
    {
        var dictionary = FromText("haus\t50\nHaus\t200\nbaum\t30\nBäume\t20\nb\t5\nbar\t70\nİstanbul\t9\nı\t3", normalizeTo: 1000, language: "tr");
        var loaded = RoundTrip(dictionary);

        Assert.Equal("tr", loaded.Language);
        Assert.Equal(dictionary.Count, loaded.Count);
        Assert.Equal(dictionary.TotalFrequency, loaded.TotalFrequency);
        Assert.Equal(dictionary.WithPrefix(string.Empty), loaded.WithPrefix(string.Empty));
        foreach (var (key, _, _) in dictionary.WithPrefix(string.Empty))
        {
            Assert.Equal(dictionary.Forms(key), loaded.Forms(key));
            Assert.Equal(dictionary.Lookup(key), loaded.Lookup(key));
            for (int length = 1; length <= key.Length; length++)
            {
                Assert.Equal(dictionary.Complete(key[..length]), loaded.Complete(key[..length]));
            }
        }

        Assert.Equal(dictionary.FindSimilar("haud", 1f, (_, _) => 1f), loaded.FindSimilar("haud", 1f, (_, _) => 1f));
        Assert.Throws<InvalidOperationException>(() => loaded.Add("neu", 1)); // fertig aufgebaut
    }

    [Fact]
    public void Compiled_EmptyDictionary()
    {
        var loaded = RoundTrip(FromText(string.Empty));

        Assert.Equal(0, loaded.Count);
        Assert.Empty(loaded.Complete("a"));
        Assert.False(loaded.Contains("a"));
    }

    /// <summary>Die mitgelieferten Wortlisten: Die vorbereitete Fassung muss genau dasselbe Wörterbuch ergeben.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("tr")]
    [InlineData("ru")]
    [InlineData("zh")]
    public void Compiled_MatchesTheShippedWordList(string language)
    {
        WordDictionary dictionary;
        using (var stream = File.OpenRead(Path.Combine(RepositoryRoot(), "Assets", "dict", $"{language}.txt")))
        {
            dictionary = WordDictionary.Load(stream, WordDictionary.ReferenceTotal, language);
        }

        var loaded = RoundTrip(dictionary);

        Assert.Equal(dictionary.TotalFrequency, loaded.TotalFrequency);
        Assert.True(dictionary.WithPrefix(string.Empty).SequenceEqual(loaded.WithPrefix(string.Empty)));
        var prefixes = dictionary.WithPrefix(string.Empty)
            .SelectMany(e => Enumerable.Range(1, Math.Min(3, e.Key.Length)).Select(length => e.Key[..length]))
            .Distinct(StringComparer.Ordinal);
        foreach (string prefix in prefixes)
        {
            Assert.True(dictionary.Complete(prefix).SequenceEqual(loaded.Complete(prefix)), prefix);
        }

        foreach (var (key, _, _) in dictionary.WithPrefix(string.Empty))
        {
            Assert.True(dictionary.Forms(key).SequenceEqual(loaded.Forms(key)), key);
        }
    }

    [Fact]
    public void Compiled_RejectsOtherAndDamagedData()
    {
        Assert.Throws<InvalidDataException>(() => WordDictionary.LoadCompiled(new MemoryStream("haus\t50\n"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => WordDictionary.LoadCompiled(new MemoryStream()));

        var stream = new MemoryStream();
        FromText("haus\t50\nHaus\t200\nbaum\t30").Save(stream);
        byte[] data = stream.ToArray();

        // abgeschnitten (Schreiben unterbrochen) und mit angehängtem Rest
        Assert.Throws<InvalidDataException>(() => WordDictionary.LoadCompiled(new MemoryStream(data[..^3])));
        Assert.Throws<InvalidDataException>(() => WordDictionary.LoadCompiled(new MemoryStream([.. data, 0])));

        // andere Formatversion
        byte[] otherVersion = [.. data];
        otherVersion[4]++;
        Assert.Throws<InvalidDataException>(() => WordDictionary.LoadCompiled(new MemoryStream(otherVersion)));

        // Längenangaben passen nicht zum Text
        byte[] wrongLength = [.. data];
        wrongLength[32 + 2 * 4]++; // Länge des ersten Schlüssels (nach den Häufigkeiten der beiden Schlüssel)
        Assert.Throws<InvalidDataException>(() => WordDictionary.LoadCompiled(new MemoryStream(wrongLength)));
    }

    [Fact]
    public void Save_NeedsAFinishedDictionary()
    {
        var dictionary = new WordDictionary();
        dictionary.Add("haus", 1);

        Assert.Throws<InvalidOperationException>(() => dictionary.Save(new MemoryStream()));
    }

    [Fact]
    public void NgramModel_LoadRejectsATruncatedFile()
    {
        var stream = new MemoryStream();
        new NgramModel.Builder().Add("ich", "habe", 100).Add("ich", "bin", 80).Build().Save(stream);
        byte[] data = stream.ToArray();

        Assert.Throws<EndOfStreamException>(() => NgramModel.Load(new MemoryStream(data[..^2])));
        Assert.Equal(["habe", "bin"], NgramModel.Load(new MemoryStream(data)).Predict(new WordContext("ich")).Select(p => p.Word));
    }

    static string RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ZBoard.csproj"))) return dir.FullName;
            }
        }

        throw new InvalidOperationException("zBoard-Ordner nicht gefunden.");
    }
}
