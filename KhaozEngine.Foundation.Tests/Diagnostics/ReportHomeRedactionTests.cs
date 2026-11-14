using System.Text.RegularExpressions;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class ReportHomeRedactionTests
{
    [Theory]
    // #1463: both boundaries belong to the original text, including when scanning resumes.
    [InlineData("/Users/ann", "/Users/ann/Users/ann", "~/Users/ann")]
    [InlineData("/aba/ab", "/aba/aba/ab", "/aba/aba/ab")]
    [InlineData("/Users/al", "/Volumes/Backup/Users/al/x", "/Volumes/Backup/Users/al/x")]
    [InlineData("/Users/al", "/Users/al", "~")]
    [InlineData("/Users/al", " /Users/al", " ~")]
    [InlineData("/Users/al", "\"/Users/al\"", "\"~\"")]
    [InlineData("/Users/al", "path=/Users/al", "path=~")]
    [InlineData("/Users/al", "file:///Users/al/x", "file://~/x")]
    [InlineData(@"C:\Users\al", @"\\?\C:\Users\al\x", @"\\?\~\x")]
    [InlineData("/Users/al", "/Users/al /Users/al", "~ ~")]
    [InlineData("/Users/al", "/Users/al//Users/al", "~/~")]
    [InlineData("/Users/al", "a/Users/al ./Users/al _/Users/al -/Users/al 1/Users/al", "a/Users/al ./Users/al _/Users/al -/Users/al 1/Users/al")]
    [InlineData("/Users/ann", "/Users/anna /Users/ann. /Users/ann_ /Users/ann-", "/Users/anna /Users/ann. /Users/ann_ /Users/ann-")]
    [InlineData("/Users/ann", "/Users/ann!\n/Users/ann\n", "~!\n~\n")]
    [InlineData(@"C:\Users\Ann", @"C:\Users\Ann\C:\Users\Ann", @"~\~")]
    [InlineData("/Users/ann", @"\users\ANN/x", "~/x")]
    [InlineData("/Users/a+b", "/USERS/A+B/x", "~/x")]
    [InlineData("/Users/ann", "/Users/ann\u212a /Users/ann\u0130 /Users/ann\u0131", "/Users/ann\u212a ~\u0130 ~\u0131")]
    public void Home_boundary_preserves_siblings_and_reuses_adjacent_separators(string home, string text, string expected)
    {
        var redactor = new ReportRedactor(home);

        Assert.Equal(expected, redactor.Redact(text));
    }

    [Fact]
    public void Home_matching_is_linear_and_has_no_wall_clock_deadline()
    {
        Regex pattern = new ReportRedactor("/Users/ann").HomePattern!;

        Assert.NotEqual(RegexOptions.None, pattern.Options & RegexOptions.NonBacktracking);
        Assert.Equal(Regex.InfiniteMatchTimeout, pattern.MatchTimeout);
    }
}
