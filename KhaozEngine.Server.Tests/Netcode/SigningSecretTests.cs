using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The signing-key loader both games hand-wrote. The rule is the one they share (standard base64, trimmed, at
/// least 32 decoded bytes, unset is null, set but invalid throws), and the property that matters most is that a
/// refusal names the variable and never echoes the value.
/// </summary>
public class SigningSecretTests
{
    private const string Variable = "TEST_TOKEN_SECRET";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    private static byte[] Key(int length, byte fill = 0x5A) => Enumerable.Repeat(fill, length).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Decode_UnsetOrBlank_IsNull(string? raw) => Assert.Null(SigningSecret.Decode(raw, Variable));

    [Fact]
    public void Decode_ThirtyTwoBytes_WithSurroundingWhitespace_Loads()
    {
        byte[] key = Key(32);
        string raw = "  \n" + Convert.ToBase64String(key) + " \t\r\n";

        byte[]? decoded = SigningSecret.Decode(raw, Variable);

        Assert.Equal(key, decoded);
    }

    [Fact]
    public void Decode_ALongerKey_Loads()
    {
        byte[] key = Key(64, 0x11);
        Assert.Equal(key, SigningSecret.Decode(Convert.ToBase64String(key), Variable));
    }

    [Fact]
    public void Decode_ThirtyOneBytes_Throws_NamingTheVariable_AndNotTheValue()
    {
        string raw = Convert.ToBase64String(Key(31, 0x42));

        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => SigningSecret.Decode(raw, Variable));

        Assert.Contains(Variable, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("31", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(SigningSecret.MinimumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            thrown.Message, StringComparison.Ordinal);
        AssertCarriesNoPartOf(raw, thrown);
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("c2VjcmV0LXNlY3JldC1zZWNyZXQtc2VjcmV0LXNlY3JldA=")]
    [InlineData("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%")]
    public void Decode_NotBase64_Throws_NamingTheVariable_AndNotTheValue(string raw)
    {
        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => SigningSecret.Decode(raw, Variable));

        Assert.Contains(Variable, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("base64", thrown.Message, StringComparison.Ordinal);
        AssertCarriesNoPartOf(raw, thrown);
    }

    [Fact]
    public void Decode_RefusesTheUrlSafeAlphabet_BecauseTheRuleIsStandardBase64()
    {
        // Thirty-two 0xFF bytes encode to '/' runs in standard base64, which the url-safe alphabet writes as '_'.
        byte[] key = Key(32, 0xFF);
        string standard = Convert.ToBase64String(key);
        string urlSafe = standard.Replace('+', '-').Replace('/', '_');
        Assert.NotEqual(standard, urlSafe);

        Assert.Equal(key, SigningSecret.Decode(standard, Variable));
        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => SigningSecret.Decode(urlSafe, Variable));
        AssertCarriesNoPartOf(urlSafe, thrown);
    }

    /// <summary>
    /// The compatibility the games' migration depends on: every value their loaders accepted decodes to the same
    /// bytes here, so every outstanding token keeps verifying. Both loaders are
    /// <c>Convert.FromBase64String(raw.Trim())</c> behind the same length floor.
    /// </summary>
    [Fact]
    public void Decode_AcceptsExactlyWhatTheGamesLoadersAccepted_WithTheSameBytes()
    {
        byte[] key = Enumerable.Range(0, 48).Select(i => (byte)(i * 37)).ToArray();
        string b64 = Convert.ToBase64String(key);
        var inputs = new List<string>
        {
            b64,
            " " + b64 + "\n",
            // Interior whitespace, as a line-wrapped value pasted into a secret store.
            b64[..20] + "\n" + b64[20..40] + "\r\n" + b64[40..],
        };

        foreach (string raw in inputs)
        {
            byte[] expected = GamesLoader(raw);
            Assert.Equal(expected, SigningSecret.Decode(raw, Variable));
        }
    }

    /// <summary>
    /// The other half of the compatibility: what the games' loaders refused as base64 is refused here too, for the same
    /// reason. The whitespace the decoder skips (space, tab, CR, LF) is accepted above. Any other whitespace inside the
    /// value is not base64 to either loader.
    /// </summary>
    [Theory]
    [InlineData("padding only")]
    [InlineData("data after padding")]
    [InlineData("one character short of a quantum")]
    [InlineData("one character past a quantum")]
    [InlineData("no-break space inside")]
    [InlineData("form feed inside")]
    [InlineData("vertical tab inside")]
    public void Decode_RefusesWhatTheGamesLoadersRefused(string scenario)
    {
        byte[] key = Enumerable.Range(0, 48).Select(i => (byte)(i * 37)).ToArray();
        string b64 = Convert.ToBase64String(key);
        string padded = Convert.ToBase64String(key[..47]);
        Assert.EndsWith("=", padded, StringComparison.Ordinal);
        string raw = scenario switch
        {
            "padding only" => "====",
            "data after padding" => padded + "AAAA",
            "one character short of a quantum" => b64[..^1],
            "one character past a quantum" => b64 + "A",
            "no-break space inside" => b64[..20] + " " + b64[20..],
            "form feed inside" => b64[..20] + "\f" + b64[20..],
            "vertical tab inside" => b64[..20] + "\v" + b64[20..],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<FormatException>(() => GamesLoader(raw));
        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => SigningSecret.Decode(raw, Variable));
        Assert.Contains("base64", thrown.Message, StringComparison.Ordinal);
        AssertCarriesNoPartOf(raw, thrown);
    }

    [Fact]
    public void Decode_ReturnsAFreshArrayEveryCall()
    {
        string raw = Convert.ToBase64String(Key(32));

        byte[] first = SigningSecret.Decode(raw, Variable)!;
        byte[] second = SigningSecret.Decode(raw, Variable)!;
        first[0] ^= 0xFF;

        Assert.NotSame(first, second);
        Assert.Equal(Key(32), second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Decode_RequiresASourceName(string? sourceName) =>
        Assert.ThrowsAny<ArgumentException>(() => SigningSecret.Decode(Convert.ToBase64String(Key(32)), sourceName!));

    [Fact]
    public void Load_ReadsTheNamedVariable_ThroughTheDelegate()
    {
        byte[] key = Key(32, 0x33);
        var environment = new Dictionary<string, string?> { [Variable] = Convert.ToBase64String(key) };
        var asked = new List<string>();

        byte[]? loaded = SigningSecret.Load(name =>
        {
            asked.Add(name);
            return environment.TryGetValue(name, out string? value) ? value : null;
        }, Variable);

        Assert.Equal(key, loaded);
        Assert.Equal(new[] { Variable }, asked);
    }

    [Fact]
    public void Load_AnUnsetVariable_IsNull() => Assert.Null(SigningSecret.Load(_ => null, Variable));

    [Fact]
    public void Load_AnInvalidValue_Throws_NamingTheVariable()
    {
        string raw = Convert.ToBase64String(Key(16, 0x77));

        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => SigningSecret.Load(_ => raw, Variable));

        Assert.Contains(Variable, thrown.Message, StringComparison.Ordinal);
        AssertCarriesNoPartOf(raw, thrown);
    }

    [Fact]
    public void Load_RefusesANullReaderOrABlankVariable()
    {
        Assert.Throws<ArgumentNullException>(() => SigningSecret.Load(null!, Variable));
        Assert.ThrowsAny<ArgumentException>(() => SigningSecret.Load(_ => null, " "));
    }

    [Fact]
    public void CreateEphemeral_IsMinimumBytes_AndDiffersPerCall()
    {
        byte[] first = SigningSecret.CreateEphemeral();
        byte[] second = SigningSecret.CreateEphemeral();

        Assert.Equal(SigningSecret.MinimumBytes, first.Length);
        Assert.Equal(SigningSecret.MinimumBytes, second.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ALoadedKey_MintsTokensTheSameKeyVerifies_AndAnEphemeralKeyDoesNot()
    {
        byte[] loaded = SigningSecret.Decode(Convert.ToBase64String(Key(32, 0x2C)), Variable)!;
        byte[] ephemeral = SigningSecret.CreateEphemeral();
        string token = SignedToken.Mint("discord:1", Now.AddHours(1), loaded);

        Assert.True(SignedToken.TryVerify(token, loaded, Now, out string subject, out _));
        Assert.Equal("discord:1", subject);
        Assert.False(SignedToken.TryVerify(token, ephemeral, Now, out _, out string reason));
        Assert.Equal("bad signature", reason);
    }

    // Both games' loader: Convert.FromBase64String of the trimmed value, behind the same length floor.
    private static byte[] GamesLoader(string raw)
    {
        byte[] key = Convert.FromBase64String(raw.Trim());
        return key.Length >= SigningSecret.MinimumBytes
            ? key
            : throw new InvalidOperationException("The key is under the length floor.");
    }

    // The message and every inner exception are checked, since a host logs the whole chain.
    private static void AssertCarriesNoPartOf(string raw, Exception thrown)
    {
        string trimmed = raw.Trim();
        string probe = trimmed.Length > 8 ? trimmed[..8] : trimmed;
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain(trimmed, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(probe, e.Message, StringComparison.Ordinal);
        }
    }
}
