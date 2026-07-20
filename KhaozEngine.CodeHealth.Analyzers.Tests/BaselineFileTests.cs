using KhaozEngine.CodeHealth.Analyzers;
using Xunit;

namespace KhaozEngine.CodeHealth.Analyzers.Tests;

public class BaselineFileTests
{
    [Fact]
    public void Parses_LinesAndPath()
    {
        var entries = BaselineFile.Parse("1478 Ruinborne.Server/Program.cs\n900 Src/Game.cs\n");
        Assert.Equal(1478, entries["Ruinborne.Server/Program.cs"]);
        Assert.Equal(900, entries["Src/Game.cs"]);
    }

    [Fact]
    public void Skips_CommentsBlanksAndJunk()
    {
        var entries = BaselineFile.Parse(
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
        var entries = BaselineFile.Parse("1000 Src/My File.cs\n");
        Assert.Equal(1000, entries["Src/My File.cs"]);
    }

    [Fact]
    public void FirstEntryForAPath_Wins()
    {
        var entries = BaselineFile.Parse("1000 Src/Dup.cs\n2000 Src/Dup.cs\n");
        Assert.Equal(1000, entries["Src/Dup.cs"]);
    }

    [Fact]
    public void Tolerates_CrlfAndMissingFinalNewline()
    {
        var entries = BaselineFile.Parse("1000 Src/A.cs\r\n900 Src/B.cs");
        Assert.Equal(1000, entries["Src/A.cs"]);
        Assert.Equal(900, entries["Src/B.cs"]);
    }

    [Fact]
    public void BareNumberWithoutPath_IsSkipped()
    {
        var entries = BaselineFile.Parse("1000\n");
        Assert.Empty(entries);
    }
}
