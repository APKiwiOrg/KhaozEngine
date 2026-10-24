using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

internal static class ReportRedactionAdversarialCases
{
    const string Mask = ReportRedactor.Mask;

    internal static void AssertCorrectness(ReportRedactor redactor)
    {
        // Linear time is a property of how the rules are built, not of a clock: every secret rule runs on the
        // non-backtracking engine, which scans in time linear in the input whatever the pattern. The home rule
        // also uses that engine. Production matching has no wall-clock deadline.
        const int Length = 200_000;

        Assert.Equal(5, redactor.Rules.Count);
        Assert.All(redactor.Rules, rule =>
            Assert.True((rule.Pattern.Options & RegexOptions.NonBacktracking) != 0, rule.Pattern.ToString()));
        Assert.NotEqual(RegexOptions.None, redactor.HomePattern!.Options & RegexOptions.NonBacktracking);
        Assert.NotEqual(RegexOptions.None, redactor.SecretValuePattern.Options & RegexOptions.NonBacktracking);

        // Keys with no terminator: the first value takes the rest.
        Assert.Equal("password=" + Mask, redactor.Redact(Repeat("password=", Length)));

        // Every "eyJ" after a dash used to start a JWT attempt that scanned the run for a dot, which was
        // quadratic and timed out. Only the start of the run may start one now. Nothing here is a JWT.
        string jwtBomb = Repeat("eyJ-", Length);
        Assert.Equal(jwtBomb, redactor.Redact(jwtBomb));

        // A private key block with a 200,000 character body, closed and left open.
        string body = Repeat("MIIBOgIBAAJBAKj34Gkx", Length);
        Assert.Equal(Mask, redactor.Redact("-----BEGIN PRIVATE KEY-----\n" + body + "\n-----END PRIVATE KEY-----"));
        string open = "-----BEGIN PRIVATE KEY-----" + new string('A', Length);
        Assert.Equal(open, redactor.Redact(open));

        // The other rules on their worst shapes.
        foreach (string clean in new[]
        {
            Repeat("password ", Length),
            "password" + new string(' ', Length),
            Repeat("-----BEGIN PRIVATE KEY-----", Length),
            Repeat("bearer ", Length),
            Repeat("?token=&", Length),
            Repeat(" eyJaaaaa.bbbbb", Length),
            Repeat(" ghp_aaaa", Length),
            Repeat("basic ", Length),
            Repeat("&sig", Length),
            Repeat("token ", Length),
            Repeat("-----BEGIN PRIVATE KEY-----A-----END ", Length),
        })
        {
            Assert.Equal(clean, redactor.Redact(clean));
        }
        // 22,224 dot-joined "eyJaaaaa" units, 200,016 characters, are 7,408 back to back JWTs of three segments each.
        Assert.Equal(string.Concat(Enumerable.Repeat("." + Mask, 7_408)), redactor.Redact(Repeat(".eyJaaaaa", 200_016)));
        Assert.Equal(string.Concat(Enumerable.Repeat(Mask + ".", 7_408)), redactor.Redact(Repeat("eyJaaaaa.", 200_016)));
        Assert.Equal(Mask + "-", redactor.Redact(Repeat("sk-", Length)));
        Assert.Equal("-" + Mask, redactor.Redact(Repeat("-sk", Length)));
        Assert.Equal(new string('~', 20_000), redactor.Redact(Repeat("/Users/ann", Length)));
    }

    internal static string Repeat(string unit, int totalChars)
    {
        var sb = new System.Text.StringBuilder(totalChars + unit.Length);
        while (sb.Length < totalChars) sb.Append(unit);
        return sb.ToString();
    }
}
