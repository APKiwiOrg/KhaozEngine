using System.Globalization;
using System.Linq;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class IssueTitleTests
{
    [Fact]
    public void Title_FirstLineTrimmed() =>
        Assert.Equal("[Grimhollow 0.29.3] Stuck", IssueTitle.Compose("Grimhollow", "0.29.3", "  Stuck  \nmore"));

    [Fact]
    public void Title_LeadingBlankLines_UseFirstNonEmpty() =>
        Assert.Equal("[Grimhollow 0.29.3] Stuck", IssueTitle.Compose("Grimhollow", "0.29.3", " \r\n\t\rStuck\nmore"));

    [Fact]
    public void Title_Over70_CutTo67PlusEllipsis()
    {
        Assert.Equal(70, BugReportLimits.MaxTitleChars);
        Assert.Equal("[Grimhollow 0.29.3] " + new string('a', 67) + "...",
            IssueTitle.Compose("Grimhollow", "0.29.3", new string('a', 80)));
    }

    [Fact]
    public void Title_Exactly70_Unchanged() =>
        Assert.Equal("[Grimhollow 0.29.3] " + new string('a', 70),
            IssueTitle.Compose("Grimhollow", "0.29.3", new string('a', 70)));

    [Fact]
    public void Title_EmojiAndCombiningMarksAtBoundary_NotSplit()
    {
        string description = new string('a', 66) + "\U0001F600e\u0301" + new string('b', 10);
        string title = IssueTitle.Compose("Grimhollow", "0.29.3", description);

        Assert.Equal("[Grimhollow 0.29.3] " + new string('a', 66) + "\U0001F600...", title);
        string combining = string.Concat(Enumerable.Repeat("e\u0301", 80));
        string suffix = IssueTitle.Compose("Grimhollow", "0.29.3", combining)["[Grimhollow 0.29.3] ".Length..];
        Assert.Equal(string.Concat(Enumerable.Repeat("e\u0301", 67)) + "...", suffix);
        Assert.Equal(70, new StringInfo(suffix).LengthInTextElements);
    }

    [Fact]
    public void Title_MissingVersion_UsesUnknown() =>
        Assert.Equal("[Grimhollow unknown] Stuck", IssueTitle.Compose("Grimhollow", null, "Stuck"));

    [Fact]
    public void Title_NeutralisesMentions() =>
        Assert.Equal("[Grimhollow 0.29.3] @\u200bdev #\u200b12 & <img>",
            IssueTitle.Compose("Grimhollow", "0.29.3", "@dev #12 & <img>"));

    [Fact]
    public void Title_StripsBidiControls() =>
        Assert.Equal("[Grimhollow 0.29.3] safe title", IssueTitle.Compose("Grimhollow", "0.29.3",
            "\u061c\u200e\u200f\u202a\u202b\u202c\u202d\u202esafe\u2066\u2067\u2068\u2069 title"));

    [Fact]
    public void Title_BidiRemoval_DoesNotRecreateReferences() =>
        Assert.Equal("[Grimhollow 0.29.3] #\u200b12 GH\u200b-7 GitHub\u200b.com",
            IssueTitle.Compose("Grimhollow", "0.29.3", "#\u202e12 GH\u2066-7 Git\u061cHub.com"));

    [Theory]
    [InlineData("#\u000712", "#\u200b12")]
    [InlineData("GH\u0007-7", "GH\u200b-7")]
    [InlineData("git\u0007hub.com", "github\u200b.com")]
    public void Title_RemovesControlsBeforeMatchingReferences(string input, string expected) =>
        Assert.Equal("[Grimhollow 0.29.3] " + expected, IssueTitle.Compose("Grimhollow", "0.29.3", input));

    [Fact]
    public void Title_ReferenceAndControlNeutralisation() =>
        Assert.Equal("[Grimhollow 0.29.3] GH\u200b-7 GitHub\u200b.com ab",
            IssueTitle.Compose("Grimhollow", "0.29.3", "GH-7 GitHub.com a\u0007b"));
}
