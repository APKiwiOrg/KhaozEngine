using System;
using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

/// <summary>
/// The bug report redactor, one test per rule of the D-RED table plus the text it must leave alone. The table
/// is the oracle: these pin its patterns, its order and its home rule.
/// </summary>
public sealed class ReportRedactorTests
{
    const string Mask = ReportRedactor.Mask;

    static readonly ReportRedactor NoHome = new(homeDirectory: "");

    [Fact]
    public void Home_becomes_tilde()
    {
        var redactor = new ReportRedactor("/Users/ann");

        Assert.Equal("open ~/Library/x.log", redactor.Redact("open /Users/ann/Library/x.log"));
        Assert.Equal("cwd ~", redactor.Redact("cwd /Users/ann"));
        Assert.Equal("\"~/x\"", redactor.Redact("\"/USERS/ANN/x\""));
    }

    [Fact]
    public void Sibling_user_is_untouched()
    {
        var redactor = new ReportRedactor("/Users/ann");

        Assert.Equal("/Users/anna/x", redactor.Redact("/Users/anna/x"));
        Assert.Equal("/Users/ann.old/x", redactor.Redact("/Users/ann.old/x"));
        Assert.Equal("/Users/ann_2/x", redactor.Redact("/Users/ann_2/x"));
    }

    [Fact]
    public void Windows_home_matches_both_separators()
    {
        var redactor = new ReportRedactor(@"C:\Users\Ann");

        Assert.Equal(@"~\AppData", redactor.Redact(@"C:\Users\Ann\AppData"));
        Assert.Equal("~/x", redactor.Redact("c:/users/ann/x"));
        Assert.Equal(@"C:\Users\Annie\x", redactor.Redact(@"C:\Users\Annie\x"));
    }

    [Fact]
    public void Short_home_is_ignored()
    {
        Assert.Equal("/etc/hosts", new ReportRedactor("/").Redact("/etc/hosts"));
        Assert.Equal(@"C:\x", new ReportRedactor(@"C:").Redact(@"C:\x"));
        Assert.Equal("/etc/hosts", new ReportRedactor("").Redact("/etc/hosts"));
    }

    [Fact]
    public void Default_home_is_the_user_profile()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length < 3) return;

        Assert.Equal("~", new ReportRedactor().Redact(home));
    }

    [Fact]
    public void Bearer_value_is_masked()
    {
        Assert.Equal("Authorization: Bearer [redacted]", NoHome.Redact("Authorization: Bearer abc.def-123456"));
        Assert.Equal("Authorization: basic  [redacted]", NoHome.Redact("Authorization: basic  dXNlcjpwYXNz"));
    }

    [Fact]
    public void Jwt_is_masked()
    {
        string redacted = NoHome.Redact("t=eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl");

        Assert.DoesNotContain("eyJ", redacted);
        Assert.Equal("t=" + Mask, redacted);
    }

    [Fact]
    public void Key_value_secrets_are_masked()
    {
        Assert.Equal("password=[redacted]", NoHome.Redact("password=hunter22"));
        Assert.Equal("\"apiKey\": \"[redacted]\"", NoHome.Redact("\"apiKey\": \"abcd1234\""));
        Assert.Equal("AccountKey=[redacted];", NoHome.Redact("AccountKey=abc==;"));
        Assert.Equal("client_secret : [redacted], next", NoHome.Redact("client_secret : s3cr3t, next"));
    }

    [Fact]
    public void Query_secrets_are_masked()
    {
        Assert.Equal(
            "https://x/b?sv=1&sig=[redacted]&se=2",
            NoHome.Redact("https://x/b?sv=1&sig=AbC%2B&se=2"));
        Assert.Equal(
            "https://x/cb?code=[redacted]#state",
            NoHome.Redact("https://x/cb?code=abc123#state"));
    }

    [Fact]
    public void Provider_tokens_are_masked()
    {
        Assert.Equal(Mask, NoHome.Redact("ghp_" + new string('a', 36)));
        Assert.Equal("key " + Mask + " end", NoHome.Redact("key AKIAABCDEFGHIJKLMNOP end"));
        Assert.Equal(Mask, NoHome.Redact("xoxb-1234567890-abcdef"));
        Assert.Equal(Mask, NoHome.Redact("sk-" + new string('Z', 24)));
        Assert.Equal(Mask, NoHome.Redact("github_pat_" + new string('1', 22)));
    }

    [Fact]
    public void Pem_block_is_masked()
    {
        const string pem =
            "-----BEGIN RSA PRIVATE KEY-----\n" +
            "MIIBOgIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8Qu\n" +
            "KUpRKfFLfRYC9AIKjbJTWit+CqvjWYzvQwECAwEAAQ==\n" +
            "-----END RSA PRIVATE KEY-----";

        Assert.Equal("key:\n" + Mask + "\ndone", NoHome.Redact("key:\n" + pem + "\ndone"));
        Assert.Equal(
            Mask,
            NoHome.Redact("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----"));
    }

    [Fact]
    public void Ordinary_text_is_untouched()
    {
        var redactor = new ReportRedactor("/Users/ann");
        string[] clean =
        [
            "Loaded 12 tiles in 3.2 ms",
            "tokenizer=fast",
            "0192f1c2a3b47c4d8e9f0a1b2c3d4e5f",
            "design=x",
            "/Users/anna/x",
            "https://x/b?sv=1&se=2",
            "",
        ];

        foreach (string text in clean)
        {
            string result = redactor.Redact(text);
            Assert.Equal(text, result);
            Assert.True(ReferenceEquals(text, result), $"'{text}' was copied although nothing matched");
        }
    }

    [Fact]
    public void Null_text_throws()
    {
        Assert.Throws<ArgumentNullException>(() => NoHome.Redact(null!));
    }

    [Fact]
    public void Environment_admits_only_KE_and_redacts_values()
    {
        var input = new List<KeyValuePair<string, string?>>
        {
            new("KE_GPU", "metal"),
            new("HOME", "/x"),
            new("ke_trace", "1"),
            new("KE_TOKEN", "password=abc"),
        };

        IReadOnlyList<TelemetryHeaderValue> selected = NoHome.SelectEnvironment(input);

        Assert.Collection(selected,
            v => Assert.Equal(new TelemetryHeaderValue("KE_GPU", "metal"), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_TOKEN", "password=[redacted]"), v),
            v => Assert.Equal(new TelemetryHeaderValue("ke_trace", "1"), v));
    }

    [Fact]
    public void Environment_values_lose_the_home_directory()
    {
        var redactor = new ReportRedactor("/Users/ann");
        var input = new List<KeyValuePair<string, string?>> { new("KE_CACHE", "/Users/ann/cache") };

        Assert.Equal("~/cache", Assert.Single(redactor.SelectEnvironment(input)).Value);
    }

    [Fact]
    public void Catastrophic_input_fails_closed_within_the_timeout()
    {
        var redactor = new ReportRedactor("/Users/ann");

        // Keys with no terminator are linear for the key and value rule: the first value takes the rest. The
        // timeout is wall clock, so a loaded machine may still fail closed, and both outcomes are safe.
        Assert.Contains(
            TimedRedact(redactor, Repeat("password=", 50_000)),
            new[] { "password=" + Mask, Mask });

        // Every "eyJ" after a dash starts a JWT attempt that scans to the end for a dot, which is quadratic.
        // It must hit the match timeout and come back masked whole rather than hang or leak.
        Assert.Equal(Mask, TimedRedact(redactor, Repeat("eyJ-", 200_000)));

        // The other rules on their worst shapes stay inside the budget, masked or not.
        string[] adversarial =
        [
            Repeat("password ", 50_000),
            "password" + new string(' ', 50_000),
            "-----BEGIN PRIVATE KEY-----" + new string('A', 50_000),
            Repeat("-----BEGIN PRIVATE KEY-----", 50_000),
            Repeat("sk-", 50_000),
            Repeat("bearer ", 50_000),
            Repeat("?token=&", 50_000),
            Repeat("/Users/ann", 50_000),
        ];
        foreach (string text in adversarial)
            TimedRedact(redactor, text);
    }

    static string TimedRedact(ReportRedactor redactor, string text)
    {
        var clock = Stopwatch.StartNew();
        string result = redactor.Redact(text);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"Redact took {clock.Elapsed} on a {text.Length} character '{text[..12]}' input");
        return result;
    }

    static string Repeat(string unit, int totalChars)
    {
        var sb = new System.Text.StringBuilder(totalChars + unit.Length);
        while (sb.Length < totalChars) sb.Append(unit);
        return sb.ToString();
    }
}

/// <summary>
/// The allocation half, in its own class because it belongs in the non-parallel <c>AllocSensitive</c>
/// collection. Clean text is the common case for every report string, so a pass that finds nothing must hand
/// back the input without allocating.
/// </summary>
[Collection("AllocSensitive")]
public sealed class ReportRedactorAllocationTests
{
    [Fact]
    public void Clean_text_does_not_allocate()
    {
        var redactor = new ReportRedactor("/Users/ann");
        const string text = "Loaded 12 tiles in 3.2 ms from /Users/anna/tiles with tokenizer=fast";

        void Run() => redactor.Redact(text);

        Run();

        AllocAssert.NoPerCallAllocation("ReportRedactor.Redact on clean text", Run);
    }
}
