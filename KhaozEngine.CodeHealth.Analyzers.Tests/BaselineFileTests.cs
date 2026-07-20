using KhaozEngine.CodeHealth.Analyzers;
using Xunit;

namespace KhaozEngine.CodeHealth.Analyzers.Tests;

public class BaselineFileTests
{
    [Fact]
    public void Parses_LinesAndPath()
    {
        var entries = ParseEntries("1478 Ruinborne.Server/Program.cs\n900 Src/Game.cs\n");
        Assert.Equal(1478, entries["Ruinborne.Server/Program.cs"]);
        Assert.Equal(900, entries["Src/Game.cs"]);
    }

    [Fact]
    public void Skips_CommentsBlanksAndJunk()
    {
        var entries = ParseEntries(
            "# header comment\n" +
            "   # indented comment\n" +
            "\n" +
            "not-an-entry\n" +
            "12abc not-numeric-first-field\n" +
            "1000 Src/Real.cs\n");
        Assert.Single(entries);
        Assert.Equal(1000, entries["Src/Real.cs"]);
    }

    [Fact]
    public void Path_MayContainSpaces()
    {
        var entries = ParseEntries("1000 Src/My File.cs\n");
        Assert.Equal(1000, entries["Src/My File.cs"]);
    }

    [Fact]
    public void FirstEntryForAPath_Wins()
    {
        var entries = ParseEntries("1000 Src/Dup.cs\n2000 Src/Dup.cs\n");
        Assert.Equal(1000, entries["Src/Dup.cs"]);
    }

    [Fact]
    public void Tolerates_CrlfAndMissingFinalNewline()
    {
        var entries = ParseEntries("1000 Src/A.cs\r\n900 Src/B.cs");
        Assert.Equal(1000, entries["Src/A.cs"]);
        Assert.Equal(900, entries["Src/B.cs"]);
    }

    [Fact]
    public void BareNumberWithoutPath_IsSkipped()
    {
        var entries = ParseEntries("1000\n");
        Assert.Empty(entries);
    }

    [Fact]
    public void Exempt_RecordsPath_AndTakesRestOfLineVerbatim()
    {
        var parsed = BaselineFile.Parse("  \texempt\tSrc/My Blob.cs\n");
        Assert.True(parsed.IsExempt("Src/My Blob.cs"));
        Assert.Empty(parsed.Entries);
    }

    [Theory]
    [InlineData("exempt Src/A.cs\n1000 Src/A.cs\n")]
    [InlineData("1000 Src/A.cs\nexempt Src/A.cs\n")]
    public void Exempt_WinsOverNumericEntry_InEitherOrder(string content)
    {
        var parsed = BaselineFile.Parse(content);
        Assert.True(parsed.IsExempt("Src/A.cs"));
        Assert.Empty(parsed.Entries);
    }

    [Theory]
    [InlineData("exempted Src/A.cs\n")]
    [InlineData("Exempt Src/A.cs\n")]
    [InlineData("#exempt Src/A.cs\n")]
    [InlineData("exemptSrc/A.cs\n")]
    [InlineData("exempt\n")]
    [InlineData("exempt   \n")]
    public void Exempt_KeywordMustBeExact_OtherwiseSkippedSilently(string content)
    {
        var parsed = BaselineFile.Parse(content);
        Assert.False(parsed.IsExempt("Src/A.cs"));
        Assert.Empty(parsed.Exempt);
        Assert.Empty(parsed.Entries);
    }

    private static System.Collections.Generic.Dictionary<string, int> ParseEntries(string content) =>
        BaselineFile.Parse(content).Entries;
}
