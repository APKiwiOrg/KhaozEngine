using System;
using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReportRedactorDeadlineTests
{
    [Fact]
    public void Production_secret_rules_have_no_wall_clock_deadline()
    {
        Assert.All(new ReportRedactor("").Rules,
            rule => Assert.Equal(Regex.InfiniteMatchTimeout, rule.Pattern.MatchTimeout));
    }

    [Theory]
    [InlineData("error", "error")]
    [InlineData("System.InvalidOperationException: boom at /Users/ann/x", "System.InvalidOperationException: boom at ~/x")]
    public void Ordinary_report_strings_survive_an_elapsed_production_deadline(string text, string expected)
    {
        var redactor = new ReportRedactor("/Users/ann", ElapsedProductionDeadline());

        Assert.Equal(expected, redactor.Redact(text));
    }

    [Fact]
    public void Clean_identity_text_preserves_its_instance_after_an_elapsed_production_deadline()
    {
        ReporterIdentityRedaction identity = ReporterIdentityRedaction.Capture(
            () => ["Aster Dawn", "discord:12345"], ElapsedProductionDeadline());
        string text = new("Loaded 12 tiles in 3.2 ms".ToCharArray());

        Assert.Same(text, identity.Redact(text));
    }

    [Fact]
    public void Clean_composed_text_preserves_its_instance_after_an_elapsed_production_deadline()
    {
        ReportRedactor redactor = new ReportRedactor("", ElapsedProductionDeadline())
            .WithReporterIdentityTerms(() => ["Aster Dawn", "discord:12345"]);
        string text = new("Loaded 12 tiles in 3.2 ms".ToCharArray());

        Assert.Same(text, redactor.Redact(text));
    }

    [Fact]
    public void Identity_matches_preserve_surrounding_text_after_an_elapsed_production_deadline()
    {
        ReporterIdentityRedaction identity = ReporterIdentityRedaction.Capture(
            () => ["Aster Dawn", "discord:12345"], ElapsedProductionDeadline());

        Assert.Equal("Player [redacted] joined.", identity.Redact("Player Aster Dawn joined."));
    }

    static TimeSpan ElapsedProductionDeadline()
    {
        TimeSpan timeout = new ReportRedactor("").Rules[0].Pattern.MatchTimeout;
        // A positive tick rounds to a zero millisecond regex deadline. Collapse finite production budgets
        // instead of sleeping or loading the runner. An explicitly disabled deadline remains disabled.
        return timeout == Regex.InfiniteMatchTimeout ? timeout : TimeSpan.FromTicks(1);
    }
}
