using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
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

        // Flip the last character of the base64 payload so the HMAC no longer matches.
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
        string encodedA = a.Encode(json);
        Assert.StartsWith("AAA1:", encodedA);
        Assert.StartsWith("BBB1:", b.Encode(json));
        Assert.NotEqual(encodedA, c.Encode(json)); // same prefix, different key -> different hmac
    }

    [Fact]
    public void Decode_EmptyPayload_ReturnsNull_AndLogsError()
    {
        var encoder = NewEncoder(out FakeLogger log);

        // Valid prefix + hmac + 2nd separator, but no payload after it.
        string? decoded = encoder.Decode(Prefix + ":deadbeef:");

        Assert.Null(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, log.Entries[0].Level);
    }

    [Fact]
    public void RoundTrip_PrefixContainingSeparator_StillRoundTrips()
    {
        // A prefix that itself contains ':' must not break parsing (firstSep is the prefix length).
        var encoder = new SaveEncoder(Key, "FO:O1", new FakeLogger());
        string json = "{\"v\":1}";

        Assert.Equal(json, encoder.Decode(encoder.Encode(json)));
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
    public void Ctor_NullLogger_FallsBackToAmbient_AndStillDecodes()
    {
        // A null logger is no longer an error: the encoder falls back to the ambient Log facade
        // (a no-op when unconfigured). Decoding must still work without throwing.
        var encoder = new SaveEncoder(Key, Prefix, null);
        Assert.Equal("{\"x\":1}", encoder.Decode(encoder.Encode("{\"x\":1}")));
    }

    // ---- v2 envelope (versioned, tamper-protected metadata) ----

    [Fact]
    public void EncodeV2_RoundTrips_JsonAndMetadata()
    {
        var encoder = NewEncoder(out _);
        var meta = new SaveMetadata { SavedAtUtc = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc), GameVersion = "1.2.3", Summary = "lvl 4" };

        SaveDecodeResult r = encoder.TryDecode(encoder.Encode("{\"score\":42}", meta));

        Assert.Equal(SaveDecodeVerdict.Ok, r.Verdict);
        Assert.Equal("{\"score\":42}", r.Json);
        Assert.Equal("1.2.3", r.Metadata!.GameVersion);
        Assert.Equal("lvl 4", r.Metadata.Summary);
    }

    [Fact]
    public void EncodeV2_TamperedPayload_ReportsMismatch_StillYieldsJson()
    {
        var encoder = NewEncoder(out _);
        string encoded = encoder.Encode("{\"gold\":10}");
        string tampered = encoded[..^4] + SwapLastBase64Char(encoded);

        SaveDecodeResult r = encoder.TryDecode(tampered);

        Assert.Equal(SaveDecodeVerdict.TamperMismatch, r.Verdict);
        Assert.NotNull(r.Json);
    }

    [Fact]
    public void EncodeV2_TamperedMetadataSegment_ReportsMismatch()
    {
        var encoder = NewEncoder(out _);
        var meta = new SaveMetadata { SavedAtUtc = DateTime.UnixEpoch, GameVersion = "3.1.4", Summary = "boss room" };
        // parts = [prefix, "v2", hmac, meta-base64, payload-base64]. Flip one char inside the meta segment.
        string[] parts = encoder.Encode("{\"hp\":99}", meta).Split(':');
        char[] metaChars = parts[3].ToCharArray();
        metaChars[0] = metaChars[0] == 'A' ? 'B' : 'A';
        parts[3] = new string(metaChars);

        SaveDecodeResult r = encoder.TryDecode(string.Join(':', parts));

        Assert.Equal(SaveDecodeVerdict.TamperMismatch, r.Verdict);
    }

    [Fact]
    public void TryDecode_V1Content_DecodesWithNullMetadata()
    {
        var encoder = NewEncoder(out _);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":1}"));
        using var h = new System.Security.Cryptography.HMACSHA256(Key);
        string hmac = Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes(b64)));

        SaveDecodeResult r = encoder.TryDecode($"{Prefix}:{hmac}:{b64}");

        Assert.Equal(SaveDecodeVerdict.Ok, r.Verdict);
        Assert.Equal("{\"v\":1}", r.Json);
        Assert.Null(r.Metadata);
    }

    [Fact]
    public void TryDecode_PlainText_NotEncoded()
    {
        var encoder = NewEncoder(out _);

        SaveDecodeResult r = encoder.TryDecode("just text");

        Assert.Equal(SaveDecodeVerdict.NotEncoded, r.Verdict);
        Assert.Null(r.Json);
    }

    [Fact]
    public void TryDecode_V2EmptyPayload_Malformed()
    {
        var encoder = NewEncoder(out _);
        var meta = new SaveMetadata { SavedAtUtc = DateTime.UnixEpoch, GameVersion = "0.0.1" };
        string metaB64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(meta));
        using var h = new System.Security.Cryptography.HMACSHA256(Key);
        string hmac = Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes($"{metaB64}:")));

        SaveDecodeResult r = encoder.TryDecode($"{Prefix}:v2:{hmac}:{metaB64}:");

        Assert.Equal(SaveDecodeVerdict.Malformed, r.Verdict);
        Assert.Contains("empty payload", r.Detail);
    }

    [Fact]
    public void TryReadMetadata_VerifiesHmac_WithoutPayloadDecode()
    {
        var encoder = NewEncoder(out _);
        var meta = new SaveMetadata { SavedAtUtc = DateTime.UnixEpoch, GameVersion = "9.9.9" };
        SaveMetadataProbe probe = encoder.TryReadMetadata(encoder.Encode("{}", meta));

        Assert.Equal(SaveDecodeVerdict.Ok, probe.Verdict);
        Assert.Equal("9.9.9", probe.Metadata!.GameVersion);
    }

    [Fact]
    public void TryReadMetadata_V1_OkWithNullMetadata()
    {
        var encoder = NewEncoder(out _);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":1}"));
        using var h = new System.Security.Cryptography.HMACSHA256(Key);
        string hmac = Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes(b64)));

        SaveMetadataProbe probe = encoder.TryReadMetadata($"{Prefix}:{hmac}:{b64}");

        Assert.Equal(SaveDecodeVerdict.Ok, probe.Verdict);
        Assert.Null(probe.Metadata);
    }

    [Fact]
    public void Decode_V2Tampered_LenientReturnsJson_AndWarns()
    {
        var encoder = NewEncoder(out FakeLogger log);
        string encoded = encoder.Encode("{\"gold\":5}");
        string tampered = encoded[..^4] + SwapLastBase64Char(encoded);

        string? decoded = encoder.Decode(tampered);

        Assert.NotNull(decoded);
        Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warn, log.Entries[0].Level);
        Assert.Contains("possible tampering", log.Entries[0].Message);
    }

    // Returns the last 4 chars of the encoded string with one non-padding base64 char swapped for a
    // different valid one, so the payload still Base64-decodes but its HMAC no longer matches.
    private static string SwapLastBase64Char(string encoded)
    {
        char[] tail = encoded[^4..].ToCharArray();
        for (int i = tail.Length - 1; i >= 0; i--)
        {
            if (tail[i] != '=')
            {
                tail[i] = tail[i] == 'A' ? 'B' : 'A';
                break;
            }
        }
        return new string(tail);
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
