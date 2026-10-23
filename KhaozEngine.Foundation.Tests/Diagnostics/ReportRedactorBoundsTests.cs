using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReportRedactorBoundsTests
{
    const int MaximumLength = 4 * 1024 * 1024;

    [Fact]
    public void Clean_text_at_the_limit_keeps_its_instance()
    {
        ReportRedactor redactor = new ReportRedactor("", Regex.InfiniteMatchTimeout)
            .WithReporterIdentityTerms(() => ["Aster Dawn"]);
        string text = new('x', MaximumLength);

        Assert.Same(text, redactor.Redact(text));
    }

    [Fact]
    public void Oversized_input_fails_closed_before_matching()
    {
        var redactor = new ReportRedactor("", Regex.InfiniteMatchTimeout);

        Assert.Equal(ReportRedactor.Mask, redactor.Redact(new string('x', MaximumLength + 1)));
    }

    [Theory]
    [InlineData("password=x")]
    [InlineData("Aster")]
    public void Replacement_growth_past_the_limit_fails_closed(string prefix)
    {
        var original = new ReportRedactor("", Regex.InfiniteMatchTimeout);
        ReportRedactor redactor = prefix == "Aster" ? original.WithReporterIdentityTerms(() => ["Aster"]) : original;
        string text = prefix + " " + new string('x', MaximumLength - prefix.Length - 1);

        Assert.Equal(ReportRedactor.Mask, redactor.Redact(text));
    }

    [Fact]
    public void Identity_also_bounds_input_and_replacement_growth()
    {
        ReporterIdentityRedaction identity = ReporterIdentityRedaction.Capture(() => ["Aster"], Regex.InfiniteMatchTimeout);

        Assert.Equal(ReportRedactor.Mask, identity.Redact(new string('x', MaximumLength + 1)));
        Assert.Equal(ReportRedactor.Mask, identity.Redact("Aster " + new string('x', MaximumLength - 6)));
    }

    [Fact]
    public void Oversized_home_pattern_fails_closed()
    {
        var redactor = new ReportRedactor("/Users/" + new string('x', 4096));

        Assert.Equal(ReportRedactor.Mask, redactor.Redact("diagnostics"));
    }
}
