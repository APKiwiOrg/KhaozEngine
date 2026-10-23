using System;
using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReportRedactorDeadlineTests
{
    [Theory]
    [InlineData("secret-0")]
    [InlineData("secret-1")]
    [InlineData("secret-2")]
    [InlineData("secret-3")]
    [InlineData("secret-4")]
    [InlineData("key-value")]
    [InlineData("home")]
    [InlineData("home-windows")]
    [InlineData("identity-single")]
    [InlineData("identity-combined")]
    public void Every_production_matcher_is_non_backtracking_and_has_no_deadline(string name)
    {
        Regex production = Matcher(name);

        AssertPolicy(production);

        // Exercise the same guard with the former finite production deadline and with a backtracking
        // copy. These controls fail by construction, without invoking a match or depending on a clock.
        Regex finite = Matcher(name, TimeSpan.FromMilliseconds(250));
        Assert.ThrowsAny<XunitException>(() => AssertPolicy(finite));
        var backtracking = new Regex(production.ToString(), production.Options & ~RegexOptions.NonBacktracking,
            Regex.InfiniteMatchTimeout);
        Assert.ThrowsAny<XunitException>(() => AssertPolicy(backtracking));
    }

    static void AssertPolicy(Regex pattern)
    {
        Assert.NotEqual(RegexOptions.None, pattern.Options & RegexOptions.NonBacktracking);
        Assert.Equal(Regex.InfiniteMatchTimeout, pattern.MatchTimeout);
    }

    static Regex Matcher(string name, TimeSpan? timeout = null)
    {
        string home = name == "home-windows" ? @"C:\Users\Ann" : "/Users/ann";
        ReportRedactor redactor = timeout is { } finite ? new(home, finite) : new(home);
        return name switch
        {
            "secret-0" => redactor.Rules[0].Pattern,
            "secret-1" => redactor.Rules[1].Pattern,
            "secret-2" => redactor.Rules[2].Pattern,
            "secret-3" => redactor.Rules[3].Pattern,
            "secret-4" => redactor.Rules[4].Pattern,
            "key-value" => redactor.SecretValuePattern,
            "home" or "home-windows" => Assert.IsType<Regex>(redactor.HomePattern),
            "identity-single" => Assert.IsType<Regex>(redactor.WithReporterIdentityTerms(
                () => ["Aster (Dawn)+"]).IdentityPattern),
            "identity-combined" => Assert.IsType<Regex>(redactor.WithReporterIdentityTerms(
                () => ["Aster Dawn", "discord:12345"]).IdentityPattern),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }
}
