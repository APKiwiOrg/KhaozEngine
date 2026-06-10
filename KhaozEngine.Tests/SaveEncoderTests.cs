using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class SaveEncoderTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-key-v1");
    private const string Prefix = "TSV1";

    private static SaveEncoder NewEncoder(out FakeLogger log)
    {
        log = new FakeLogger();
        return new SaveEncoder(Key, Prefix, log);
    }

    [Fact]
    public void RoundTrip_ReturnsOriginalJson_AndLogsInfo()
    {
        var encoder = NewEncoder(out FakeLogger log);
        string json = "{\"score\":42}";

        string encoded = encoder.Encode(json);
        string? decoded = encoder.Decode(encoded);

        Assert.Equal(json, decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Info, log.Entries[0].Level);
        Assert.Contains("HMAC ok", log.Entries[0].Message);
    }

    [Fact]
    public void IsEncoded_TrueForEncoded_FalseForPlain()
    {
        var encoder = NewEncoder(out _);

        Assert.True(encoder.IsEncoded(encoder.Encode("{}")));
        Assert.False(encoder.IsEncoded("just some plain text"));
    }

    [Fact]
    public void Decode_NotOurFormat_ReturnsNull_NoLog()
    {
        var encoder = NewEncoder(out FakeLogger log);

        Assert.Null(encoder.Decode("plain text, not encoded"));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Decode_Tampered_StillReturnsJson_AndLogsWarn()
    {
        var encoder = NewEncoder(out FakeLogger log);
        string json = "{\"hp\":7}";
        string encoded = encoder.Encode(json);

        // Flip the last character of the base64 payload (after the 2nd separator).
        int lastSep = encoded.LastIndexOf(':');
        char flipped = encoded[^1] == 'A' ? 'B' : 'A';
        string tampered = encoded[..^1] + flipped;

        string? decoded = encoder.Decode(tampered);

        Assert.NotNull(decoded);                 // lenient: data recovered
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warn, log.Entries[0].Level);
        Assert.Contains("HMAC mismatch", log.Entries[0].Message);
    }

    [Fact]
    public void Decode_MalformedMissingSeparator_ReturnsNull_AndLogsError()
    {
        var encoder = NewEncoder(out FakeLogger log);

        // Has the prefix + one separator, but no second separator.
        string? decoded = encoder.Decode(Prefix + ":deadbeef");

        Assert.Null(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, log.Entries[0].Level);
    }

    [Fact]
    public void Decode_CorruptBase64_ReturnsNull_AndLogsError()
    {
        var encoder = NewEncoder(out FakeLogger log);

        // Valid prefix + a (wrong) hmac + an invalid base64 body.
        string? decoded = encoder.Decode(Prefix + ":00:!!!not-base64!!!");

        Assert.Null(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, log.Entries[0].Level);
    }

    [Fact]
    public void Encode_DiffersByPrefixAndKey()
    {
        var a = new SaveEncoder(Key, "AAA1", new FakeLogger());
        var b = new SaveEncoder(Key, "BBB1", new FakeLogger());
        var c = new SaveEncoder(Encoding.UTF8.GetBytes("other-key"), "AAA1", new FakeLogger());

        string json = "{}";
        Assert.StartsWith("AAA1:", a.Encode(json));
        Assert.StartsWith("BBB1:", b.Encode(json));
        Assert.NotEqual(a.Encode(json), c.Encode(json)); // same prefix, different key -> different hmac
    }

    [Fact]
    public void Decode_WithWrongKey_LogsMismatch()
    {
        string encoded = new SaveEncoder(Key, Prefix, new FakeLogger()).Encode("{\"x\":1}");

        var log = new FakeLogger();
        var wrong = new SaveEncoder(Encoding.UTF8.GetBytes("WRONG-key"), Prefix, log);
        string? decoded = wrong.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warn, log.Entries[0].Level);
    }

    [Fact]
    public void Ctor_NullKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SaveEncoder(null!, Prefix, new FakeLogger()));
    }

    [Fact]
    public void Ctor_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SaveEncoder(Array.Empty<byte>(), Prefix, new FakeLogger()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidPrefix_Throws(string? badPrefix)
    {
        Assert.Throws<ArgumentException>(() => new SaveEncoder(Key, badPrefix!, new FakeLogger()));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SaveEncoder(Key, Prefix, null!));
    }
}

/// <summary>Captures log calls for assertions.</summary>
internal sealed class FakeLogger : ILogger
{
    public readonly record struct Entry(LogLevel Level, string Message);
    private readonly List<Entry> entries = new();
    public IReadOnlyList<Entry> Entries => entries;

    public string Category => "test";
    public bool IsEnabled(LogLevel level) => true;
    public void Log(LogLevel level, string message, Exception? exception = null) => entries.Add(new Entry(level, message));
    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);
    public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
