using System;
using System.Reflection;
using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReportRedactorMatchingFailureTests
{
    [Theory]
    [InlineData("secret")]
    [InlineData("key-value")]
    [InlineData("home")]
    [InlineData("identity")]
    public void A_matching_failure_masks_the_whole_string(string stage)
    {
        ReportRedactor redactor = new ReportRedactor("/Users/ann", Regex.InfiniteMatchTimeout)
            .WithReporterIdentityTerms(() => ["Aster Dawn"]);
        Regex pattern = stage switch
        {
            "secret" => redactor.Rules[0].Pattern,
            "key-value" => redactor.SecretValuePattern,
            "home" => Assert.IsType<Regex>(redactor.HomePattern),
            "identity" => Assert.IsType<Regex>(redactor.IdentityPattern),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        // Each matcher is fresh and unshared. Use Regex's existing runner-factory seam to inject a
        // failure at matching time, without a production branch or a wall-clock deadline.
        FieldInfo factory = typeof(Regex).GetField("factory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        factory.SetValue(pattern, new FailingMatcherFactory());

        Assert.Throws<RegexMatchTimeoutException>(() => pattern.IsMatch("error"));
        Assert.Equal(ReportRedactor.Mask, redactor.Redact("error"));
    }

    sealed class FailingMatcherFactory : RegexRunnerFactory
    {
        protected override RegexRunner CreateInstance() => throw new RegexMatchTimeoutException();
    }
}
