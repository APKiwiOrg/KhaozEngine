using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App;

public sealed class EnvFileTests
{
    [Fact]
    public void PublicParserPreservesOrderedDuplicateEntries()
    {
        using var reader = new StringReader("SETTING=\nSETTING=second\nsetting=third");
        IReadOnlyList<KeyValuePair<string, string>> entries = EnvFile.Parse(reader);

        Assert.Equal(new[]
        {
            new KeyValuePair<string, string>("SETTING", ""),
            new KeyValuePair<string, string>("SETTING", "second"),
            new KeyValuePair<string, string>("setting", "third"),
        }, entries);
    }

    [Fact]
    public void ParseIgnoresBlankCommentAndMalformedLines()
    {
        using var reader = new StringReader(" \t\r\n # ignored=entry\nmissing separator\n=empty key\n \t =empty key\nGOOD=one\rSECOND=two");

        Assert.Equal(new[] { Pair("GOOD", "one"), Pair("SECOND", "two") }, EnvFile.Parse(reader));
    }

    [Theory]
    [InlineData(" KEY = value ", "KEY", "value")]
    [InlineData("KEY=\" spaced \"", "KEY", " spaced ")]
    [InlineData("KEY=' spaced '", "KEY", " spaced ")]
    [InlineData("KEY=\"\"", "KEY", "")]
    [InlineData("KEY=''", "KEY", "")]
    [InlineData("KEY= \t", "KEY", "")]
    [InlineData("KEY=a=b=c", "KEY", "a=b=c")]
    [InlineData("KEY=one # inline", "KEY", "one # inline")]
    [InlineData("KEY=\"one # inline\"", "KEY", "one # inline")]
    [InlineData("KEY=\"unterminated", "KEY", "\"unterminated")]
    [InlineData("KEY='unterminated", "KEY", "'unterminated")]
    [InlineData("KEY=\"", "KEY", "\"")]
    [InlineData("KEY='", "KEY", "'")]
    [InlineData("KEY=\"'", "KEY", "\"'")]
    [InlineData("KEY=${TOKEN}", "KEY", "${TOKEN}")]
    [InlineData("KEY=\"a\\nb\"", "KEY", "a\\nb")]
    [InlineData("export KEY=value", "export KEY", "value")]
    [InlineData("Mixed Key=value", "Mixed Key", "value")]
    public void ParsePreservesTheLiteralConsumerGrammar(string text, string key, string value)
    {
        using var reader = new StringReader(text);

        Assert.Equal(Pair(key, value), Assert.Single(EnvFile.Parse(reader)));
    }

    [Fact]
    public void ParseStartsAtTheCurrentPositionAndLeavesTheReaderOpen()
    {
        using var reader = new StringReader("SKIP=first\nKEEP=second");
        _ = reader.ReadLine();

        Assert.Equal(Pair("KEEP", "second"), Assert.Single(EnvFile.Parse(reader)));
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void ParseDoesNotTreatAnUnfinishedQuotedValueAsMultilineSyntax()
    {
        using var reader = new StringReader("KEY=\"first\nsecond\"\nNEXT=third");

        Assert.Equal(new[] { Pair("KEY", "\"first"), Pair("NEXT", "third") }, EnvFile.Parse(reader));
    }

    [Fact]
    public void ParsePropagatesAReadFailureRatherThanReturningPartialEntriesOrClosingTheReader()
    {
        var failure = new IOException("Synthetic read failure.");
        using var reader = new FailingReader(failure);

        Assert.Same(failure, Assert.Throws<IOException>(() => EnvFile.Parse(reader)));
        Assert.False(reader.Disposed);
    }

    [Fact]
    public void ParseRejectsANullReader()
        => Assert.Throws<ArgumentNullException>(() => EnvFile.Parse(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryReadReturnsOrderedEntriesWithBclBomDetectionAndClosesTheFile(int encodingKind)
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "settings.env");
        Encoding encoding = encodingKind switch
        {
            0 => new UTF8Encoding(false),
            1 => new UTF8Encoding(true),
            2 => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true),
        };
        File.WriteAllText(path, "KEY=café\nKEY=second\nEMPTY=", encoding);

        Assert.True(EnvFile.TryRead(path, out IReadOnlyList<KeyValuePair<string, string>> entries));
        Assert.Equal(new[] { Pair("KEY", "café"), Pair("KEY", "second"), Pair("EMPTY", "") }, entries);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void TryReadSucceedsForAReadableFileWithNoValidEntries()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "empty.env");
        File.WriteAllText(path, "# ignored\nno separator\n=empty key");

        Assert.True(EnvFile.TryRead(path, out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryReadMissingFileReturnsFalseAndEmptyEntries()
    {
        using var temp = new TempDirectory();

        Assert.False(EnvFile.TryRead(Path.Combine(temp.Path, "missing.env"), out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryReadDirectoryAsFileReturnsFalseAndReplacesPriorEntriesWithAnEmptyResult()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<KeyValuePair<string, string>> entries = new[] { Pair("PRIOR", "entry") };

        Assert.False(EnvFile.TryRead(temp.Path, out entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryReadInvalidPathReturnsFalseAndEmptyEntries()
    {
        Assert.False(EnvFile.TryRead("invalid\0path", out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryReadRejectsANullPath()
        => Assert.Throws<ArgumentNullException>(() => EnvFile.TryRead(null!, out _));

    [Fact]
    public void TryReadRejectsAnEmptyPath()
        => Assert.Throws<ArgumentException>(() => EnvFile.TryRead("", out _));

    static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    sealed class FailingReader(IOException failure) : TextReader
    {
        bool _first = true;
        public bool Disposed { get; private set; }

        public override string? ReadLine()
        {
            if (!_first) throw failure;
            _first = false;
            return "KEY=partial";
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "khaoz-env-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
