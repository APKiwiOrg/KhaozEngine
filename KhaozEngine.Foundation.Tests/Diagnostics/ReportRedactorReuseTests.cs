using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

[Collection("AllocSensitive")]
public sealed class ReportRedactorReuseTests
{
    [Fact]
    public void Adversarial_report_cannot_change_later_rule_matches()
    {
        ReportRedactionAdversarialCases.AssertCorrectness(new ReportRedactor("/Users/ann"));
        var redactor = new ReportRedactor("");
        const string input = "?TOKEN=a&Signature=b&api_key=c&id_token=d&refresh_token=e&client_secret=f&password=g";
        foreach (ReportRedactor.Rule rule in redactor.Rules)
        {
            const string clean = "Loaded 12 tiles in 3.2 ms from /Users/anna/tiles with tokenizer=fast";
            AllocAssert.NoPerCallAllocation($"Pattern {rule.Pattern}", () => rule.Pattern.IsMatch(clean));
            var original = new Regex(rule.Pattern.ToString(), rule.Pattern.Options & ~RegexOptions.NonBacktracking,
                Regex.InfiniteMatchTimeout);
            string expected = original.Replace(input, rule.Replacement);
            string actual = rule.Pattern.Replace(input, rule.Replacement);
            Assert.True(expected == actual, $"Pattern {rule.Pattern}\nExpected {expected}\nActual {actual}");
        }
        const string expectedReport = "?TOKEN=[redacted]&Signature=[redacted]&api_key=[redacted]&id_token=[redacted]&refresh_token=[redacted]&client_secret=[redacted]&password=[redacted]";
        Assert.Equal(expectedReport, redactor.Redact(input));
        const string ordinary = "Loaded 12 tiles in 3.2 ms from /Users/anna/tiles with tokenizer=fast";
        AllocAssert.NoPerCallAllocation("Redaction after adversarial input", () => redactor.Redact(ordinary));
    }
}
