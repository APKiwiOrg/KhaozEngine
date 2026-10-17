using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class IssueMarkdownTests
{
    [Fact]
    public void Neutralise_At_GetsZeroWidthSpace() =>
        Assert.Equal("@\u200boctocat", IssueMarkdown.Neutralise("@octocat"));

    [Fact]
    public void Neutralise_HashDigit_Only() =>
        Assert.Equal("#\u200b123 #abc owner/repo#\u200b12 #١", IssueMarkdown.Neutralise("#123 #abc owner/repo#12 #١"));

    [Fact]
    public void Neutralise_GhReference_KeepsCase() =>
        Assert.Equal("GH\u200b-7 gh\u200b-8 Gh\u200b-9 gh-x gh-١", IssueMarkdown.Neutralise("GH-7 gh-8 Gh-9 gh-x gh-١"));

    [Fact]
    public void Neutralise_GithubDotCom() =>
        Assert.Equal("https://GitHub\u200b.com/a/b/issues/1", IssueMarkdown.Neutralise("https://GitHub.com/a/b/issues/1"));

    [Fact]
    public void Neutralise_HtmlBecomesEntities() =>
        Assert.Equal("&lt;img src=x&gt; &amp; &gt;", IssueMarkdown.Neutralise("<img src=x> & >"));

    [Fact]
    public void Neutralise_ControlCharactersRemoved() =>
        Assert.Equal("ab\n\tc", IssueMarkdown.Neutralise("a\u0007b\r\n\t\u007fc"));

    [Fact]
    public void Quote_EveryLineQuoted_BlankLinesStayInside() =>
        Assert.Equal("> a\n>\n> b\n> c", IssueMarkdown.Quote("a\r\n\r\nb\rc"));

    [Fact]
    public void Quote_NeutralisesHostileLines() =>
        Assert.Equal("> &lt;/details&gt;\n> @\u200bhere", IssueMarkdown.Quote("</details>\n@here"));

    [Fact]
    public void TableCell_PipeAndNewlines() =>
        Assert.Equal("a\\|b c", IssueMarkdown.TableCell("a|b\r\n\nc"));

    [Fact]
    public void TableCell_NeutralisesMentionsAndHtml() =>
        Assert.Equal("&lt;@\u200b42&gt; #\u200b1", IssueMarkdown.TableCell("<@42> #1"));

    [Fact]
    public void Handle_and_subject_render_as_inline_code() =>
        Assert.Equal("`<@42>`", IssueMarkdown.InlineCode("<@42>"));

    [Theory]
    [InlineData("a`b", "``a`b``")]
    [InlineData("`a`", "`` `a` ``")]
    [InlineData("a```b|c\r\n\nd", "````a```b\\|c d````")]
    [InlineData(" a ", "`  a  `")]
    public void InlineCode_FenceProtectsBackticksAndTable(string input, string expected) =>
        Assert.Equal(expected, IssueMarkdown.InlineCode(input));

    [Fact]
    public void Fenced_LongerThanLongestBacktickRun() =>
        Assert.Equal("`````text\na````b\n@here <img>\n`````", IssueMarkdown.Fenced(new[] { "a````b", "@here <img>" }));
}
