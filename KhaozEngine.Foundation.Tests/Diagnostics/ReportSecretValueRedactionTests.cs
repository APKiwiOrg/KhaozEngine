using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReportSecretValueRedactionTests
{
    [Theory]
    [InlineData("password=x", "password=[redacted]")]
    [InlineData("password=", "password=")]
    [InlineData("password=\"\" token=x", "password=\"\" token=[redacted]")]
    [InlineData("password='x y'", "password='[redacted] y'")]
    [InlineData("token=x;token=y&token=z", "token=[redacted];token=[redacted]&token=[redacted]")]
    [InlineData("password=token=x", "password=[redacted]")]
    [InlineData("token=x\u0085token=y", "token=[redacted]\u0085token=[redacted]")]
    [InlineData("token=x\u2028token=y", "token=[redacted]\u2028token=[redacted]")]
    [InlineData("token=x\u00a0token=y", "token=[redacted]\u00a0token=[redacted]")]
    [InlineData("token=x\u200btoken=y", "token=[redacted]")]
    public void Prefix_and_value_scanning_preserves_the_original_boundaries(string text, string expected)
    {
        var redactor = new ReportSecretValueRedaction(Regex.InfiniteMatchTimeout);

        Assert.Equal(expected, redactor.Redact(text));
        if (expected == text) Assert.Same(text, redactor.Redact(text));
    }
}
