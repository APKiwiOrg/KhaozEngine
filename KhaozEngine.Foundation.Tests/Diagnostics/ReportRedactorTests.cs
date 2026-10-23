using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;
using static KhaozEngine.Tests.Diagnostics.ReportRedactionAdversarialCases;

namespace KhaozEngine.Tests.Diagnostics;

/// <summary>
/// The bug report redactor, one test per rule of the D-RED table plus the text it must leave alone. The table
/// is the oracle: these pin its patterns, its order and its home rule.
/// </summary>
public sealed class ReportRedactorTests
{
    const string Mask = ReportRedactor.Mask;

    static readonly ReportRedactor NoHome = new("");

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

        Assert.Equal("~", new ReportRedactor(null).Redact(home));
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
            new("KE_CONN", "password=abc"),
        };

        IReadOnlyList<TelemetryHeaderValue> selected = NoHome.SelectEnvironment(input);

        Assert.Collection(selected,
            v => Assert.Equal(new TelemetryHeaderValue("KE_CONN", "password=[redacted]"), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_GPU", "metal"), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_TOKEN", Mask), v),
            v => Assert.Equal(new TelemetryHeaderValue("ke_trace", "1"), v));
    }

    [Fact]
    public void Secret_named_engine_variables_are_masked()
    {
        var input = new List<KeyValuePair<string, string?>>
        {
            new("KE_API_KEY", "abc123"),
            new("KE_GPU", "metal"),
            new("ke_client_secret", "s"),
            new("KE_DB_Password", "p"),
            new("KE_PASSWD_FILE", "/etc/x"),
            new("KE_UPLOAD_TOKEN", ""),
        };

        IReadOnlyList<TelemetryHeaderValue> selected = NoHome.SelectEnvironment(input);

        Assert.Collection(selected,
            v => Assert.Equal(new TelemetryHeaderValue("KE_API_KEY", Mask), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_DB_Password", Mask), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_GPU", "metal"), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_PASSWD_FILE", Mask), v),
            v => Assert.Equal(new TelemetryHeaderValue("KE_UPLOAD_TOKEN", Mask), v),
            v => Assert.Equal(new TelemetryHeaderValue("ke_client_secret", Mask), v));
    }

    [Fact]
    public void Environment_values_lose_the_home_directory()
    {
        var redactor = new ReportRedactor("/Users/ann");
        var input = new List<KeyValuePair<string, string?>> { new("KE_CACHE", "/Users/ann/cache") };

        Assert.Equal("~/cache", Assert.Single(redactor.SelectEnvironment(input)).Value);
    }

    [Fact]
    public void Adjacent_homes_are_all_replaced()
    {
        var redactor = new ReportRedactor("/Users/ann");

        Assert.Equal("~~ ~", redactor.Redact("/Users/ann/Users/ann /Users/ann"));
        Assert.Equal("~/x:~/y", redactor.Redact("/Users/ann/x:/Users/ann/y"));
        Assert.Equal("~\n", redactor.Redact("/Users/ann\n"));
        Assert.Equal(@"~\~", new ReportRedactor(@"C:\Users\Ann").Redact(@"C:\Users\Ann\C:\Users\Ann"));
    }

    [Fact]
    public void Catastrophic_input_is_redacted_in_linear_time() =>
        ReportRedactionAdversarialCases.AssertCorrectness(new ReportRedactor("/Users/ann"));

    [Fact]
    public void Jwt_starts_only_at_the_start_of_a_token_run()
    {
        const string jwt = "eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl";

        Assert.Equal(Mask, NoHome.Redact(jwt));
        Assert.Equal("\"" + Mask + "\"", NoHome.Redact("\"" + jwt + "\""));
        Assert.Equal("id " + Mask, NoHome.Redact("id " + jwt));
        Assert.Equal("x_" + jwt, NoHome.Redact("x_" + jwt));
    }

    [Fact]
    public void Jwt_rule_matches_the_look_behind_form()
    {
        // The rule it replaced. A JWT may start only where no token character precedes it.
        var lookBehind = new Regex(
            @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}",
            RegexOptions.CultureInvariant);
        ReportRedactor.Rule jwtRule = NoHome.Rules[1];
        const string jwt = "eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl";

        (string Input, string Expected)[] cases =
        [
            (jwt, Mask),
            (jwt + " tail", Mask + " tail"),
            ("lead " + jwt, "lead " + Mask),
            (jwt + " " + jwt, Mask + " " + Mask),
            (jwt + "." + jwt, Mask + "." + Mask),
            (jwt + "," + jwt + "\n" + jwt, Mask + "," + Mask + "\n" + Mask),
            ("." + jwt + "." + jwt, "." + Mask + "." + Mask),
            ("-" + jwt, "-" + jwt),
            ("a-" + jwt, "a-" + jwt),
            ("x_" + jwt, "x_" + jwt),
            ("\"" + jwt + "\"", "\"" + Mask + "\""),
            ("=" + jwt + "&", "=" + Mask + "&"),
            ("eyJaaaa.bbbbb.ccccc", "eyJaaaa.bbbbb.ccccc"),
            ("eyJaaaaa.bbbbb", "eyJaaaaa.bbbbb"),
            (Repeat("eyJ-", 2_000), Repeat("eyJ-", 2_000)),
            (Repeat(".eyJaaaaa", 54), string.Concat(Enumerable.Repeat("." + Mask, 2))),
            (Repeat("eyJaaaaa.", 54), string.Concat(Enumerable.Repeat(Mask + ".", 2))),
        ];

        foreach ((string input, string expected) in cases)
        {
            Assert.Equal(expected, lookBehind.Replace(input, Mask));
            Assert.Equal(expected, jwtRule.Pattern.Replace(input, jwtRule.Replacement));
            Assert.Equal(expected, NoHome.Redact(input));
        }
    }

    [Fact]
    public void Secret_rules_match_their_backtracking_form()
    {
        // The non-backtracking engine reports the same matches and captures as the backtracking one, so every
        // replacement, including the kept scheme, spacing, parameter name, key, quotes and JWT prefix, is unchanged.
        string[] corpus =
        [
            "Authorization: Bearer abc.def-123456",
            "Authorization: basic  dXNlcjpwYXNz",
            "BEARER\t\tAbCdEfGh12 and basic\nzzzzzzzz9",
            "bearer short",
            "t=eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl",
            "password=hunter22",
            "\"apiKey\": \"abcd1234\"",
            "AccountKey=abc==;",
            "client_secret : s3cr3t, next",
            "PWD='x y' Private-Key = \"k\" connection_string:Server=a;",
            "passwordtoken=x secretsecret: y",
            "https://x/b?sv=1&sig=AbC%2B&se=2",
            "https://x/cb?code=abc123#state",
            "?TOKEN=a&Signature=b&api_key=c&id_token=d&refresh_token=e&client_secret=f&password=g",
            "?signature&sig=&key=?k",
            "key AKIAABCDEFGHIJKLMNOP end",
            "ghp_" + new string('a', 36),
            "xoxb-1234567890-abcdef",
            "sk-" + new string('Z', 24) + " sk-short",
            "github_pat_" + new string('1', 22),
            "key:\n-----BEGIN RSA PRIVATE KEY-----\nMIIBOg+/=\n-----END RSA PRIVATE KEY-----\ndone",
            "-----BEGIN PRIVATE KEY-----A-----END PRIVATE KEY",
            "Loaded 12 tiles in 3.2 ms from /Users/anna with tokenizer=fast",
            Repeat("password=", 2_000),
            Repeat("?token=&", 2_000),
            Repeat("bearer ", 2_000),
            Repeat(".eyJaaaaa", 2_000),
            Repeat("sk-", 2_000),
            Repeat("-sk", 2_000),
        ];

        foreach (ReportRedactor.Rule rule in NoHome.Rules)
        {
            var backtracking = new Regex(
                rule.Pattern.ToString(), rule.Pattern.Options & ~RegexOptions.NonBacktracking);
            foreach (string text in corpus)
            {
                Assert.Equal(
                    backtracking.Replace(text, rule.Replacement), rule.Pattern.Replace(text, rule.Replacement));
            }
        }

        var originalKeyValues = new Regex(
            @"(?i)((?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|connection[_-]?string|accountkey|sharedaccesskey)[""']?\s*[:=]\s*[""']?)[^\s""',;&]+",
            RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);
        var keyValues = new ReportSecretValueRedaction(Regex.InfiniteMatchTimeout);
        foreach (string text in corpus)
            Assert.Equal(originalKeyValues.Replace(text, "$1" + Mask), keyValues.Redact(text));
    }

    [Fact]
    public void Timeout_fails_closed_to_the_whole_string_mask()
    {
        var redactor = new ReportRedactor("/Users/ann", TimeSpan.FromTicks(1));
        string text = Repeat("password ", 2_000_000) + "password=hunter22";

        Assert.Equal(Mask, redactor.Redact(text));
    }

    [Fact]
    public void Timeout_on_clean_text_fails_closed()
    {
        // Nothing here matches, so no rule reaches its replace step and only a match test can time out.
        var redactor = new ReportRedactor("/Users/ann", TimeSpan.FromTicks(1));
        string text = Repeat("password ", 2_000_000);

        Assert.Equal(Mask, redactor.Redact(text));
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

    [Fact]
    public void Clean_text_does_not_allocate_after_a_full_collection()
    {
        // A regex holds its parsed replacement only weakly, so a full collection drops it and the next replace
        // parses it again. Clean text must not reach a replace, or every full collection costs a report string.
        var redactor = new ReportRedactor("/Users/ann");
        const string text = "Loaded 12 tiles in 3.2 ms from /Users/anna/tiles with tokenizer=fast";

        void Run() => redactor.Redact(text);

        Run();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        AllocAssert.NoPerCallAllocation("ReportRedactor.Redact on clean text after a full collection", Run);
    }
}
