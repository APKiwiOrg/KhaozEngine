using KhaozEngine.CodeHealth.Analyzers;
using Xunit;

namespace KhaozEngine.CodeHealth.Analyzers.Tests;

public class TestHealthBaselineTests
{
    [Fact] public void Entry_GivesItsAllowance() =>
        Assert.Equal(3, TestHealthBaseline.Parse("3 KETEST001 Src/A Tests.cs\n").AllowanceFor("KETEST001", "Src/A Tests.cs"));
    [Fact] public void MissingEntry_AllowsZero() =>
        Assert.Equal(0, TestHealthBaseline.Parse("3 KETEST001 Src/A.cs\n").AllowanceFor("KETEST002", "Src/A.cs"));
    [Fact] public void CrlfAndTabs_ParseLikeLf()
    {
        var b = TestHealthBaseline.Parse("# reason\r\n\t2\tKETEST003\tSrc/B.cs\r\n");
        Assert.Equal(2, b.AllowanceFor("KETEST003", "Src/B.cs"));
    }
    [Fact] public void FirstEntryWins() =>
        Assert.Equal(1, TestHealthBaseline.Parse("1 KETEST001 A.cs\n9 KETEST001 A.cs\n").AllowanceFor("KETEST001", "A.cs"));
    [Fact] public void Exempt_WinsOverEntry_InEitherOrder()
    {
        foreach (var text in new[] { "4 KETEST002 A.cs\nexempt KETEST002 A.cs\n", "exempt KETEST002 A.cs\n4 KETEST002 A.cs\n" })
        {
            var b = TestHealthBaseline.Parse(text);
            Assert.True(b.IsExempt("KETEST002", "A.cs"));
            Assert.Equal(0, b.AllowanceFor("KETEST002", "A.cs"));
        }
    }
    [Fact] public void Exempt_IsPerRule()
    {
        var b = TestHealthBaseline.Parse("exempt KETEST002 A.cs\n");
        Assert.False(b.IsExempt("KETEST001", "A.cs"));
    }
    [Fact] public void JunkLines_AreSkipped() =>
        Assert.Equal(0, TestHealthBaseline.Parse("3\nKETEST001 A.cs\nexempt\n3KETEST001 A.cs\n").AllowanceFor("KETEST001", "A.cs"));
}
