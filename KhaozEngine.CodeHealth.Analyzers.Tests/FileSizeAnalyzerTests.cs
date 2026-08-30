using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.CodeHealth.Analyzers.Tests;

public class FileSizeAnalyzerTests
{
    private const string Root = AnalyzerHarness.Root;

    [Fact]
    public async Task NoBaseline_IsSilent_EvenOverCap()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Huge.cs", AnalyzerHarness.SourceOfLines(5000)) }, baseline: null);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task UnlistedFile_AtCap_Passes()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Edge.cs", AnalyzerHarness.SourceOfLines(800)) }, baseline: "");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task UnlistedFile_OverCap_Fires002()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Big.cs", AnalyzerHarness.SourceOfLines(801)) }, baseline: "");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE002", d.Id);
        Assert.Contains("Src/Big.cs", d.GetMessage());
        Assert.Contains("801", d.GetMessage());
        Assert.Contains("800-line cap", d.GetMessage());
    }

    [Fact]
    public async Task BaselinedFile_AtBaseline_Passes()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Frozen.cs", AnalyzerHarness.SourceOfLines(1200)) },
            baseline: "1200 Src/Frozen.cs\n");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task BaselinedFile_OverBaseline_Fires001()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Frozen.cs", AnalyzerHarness.SourceOfLines(1201)) },
            baseline: "1200 Src/Frozen.cs\n");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE001", d.Id);
        // The REPO-RELATIVE path, not the absolute one the compilation carries: it is what the reader greps
        // for and what the baseline file is keyed by. Only the 002 message was asserting this.
        Assert.Contains("Src/Frozen.cs", d.GetMessage());
        Assert.DoesNotContain(Root + "/Src/Frozen.cs", d.GetMessage());
        Assert.Contains("1201", d.GetMessage());
        Assert.Contains("baseline is 1200", d.GetMessage());
    }

    [Fact]
    public async Task Fires001_NamesTheRightFile_WhenSeveralAreBaselined()
    {
        // With one path per diagnostic the message could name any of them and still contain "Src/".
        var diags = await AnalyzerHarness.Run(
            new[]
            {
                (Root + "/Src/Frozen.cs", AnalyzerHarness.SourceOfLines(900)),
                (Root + "/Src/Nested/Grown.cs", AnalyzerHarness.SourceOfLines(1201)),
            },
            baseline: "1200 Src/Frozen.cs\n1200 Src/Nested/Grown.cs\n");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE001", d.Id);
        Assert.Contains("Src/Nested/Grown.cs", d.GetMessage());
        Assert.DoesNotContain("Src/Frozen.cs", d.GetMessage());
    }

    [Fact]
    public async Task BaselinedFile_UnderBaseline_Passes_ShrinkIsFree()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Frozen.cs", AnalyzerHarness.SourceOfLines(900)) },
            baseline: "1200 Src/Frozen.cs\n");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task LineCount_IsNewlineCount_WcParity()
    {
        // 800 newlines but 801 SourceText lines (no trailing newline): must pass, matching wc -l.
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Edge.cs", AnalyzerHarness.SourceOfLines(800)) }, baseline: "");
        Assert.Empty(diags);

        // Adding one trailing newline makes it 801 by wc and must fire.
        diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Edge.cs", AnalyzerHarness.SourceOfLines(800) + "\n") }, baseline: "");
        Assert.Single(diags);
    }

    /// <summary>
    /// CRLF, all the way through the ANALYZER rather than only the baseline parser, which is where the coverage
    /// stopped. A `\r\n` pair is ONE line to `wc -l`, so a file authored on Windows must count the same as the
    /// identical file with LF endings. Pinned at the cap boundary, since a per-line off-by-one only shows up
    /// there: were `\r` counted too, a 400-line CRLF file would read as 800 and this pair would both fire.
    /// </summary>
    [Fact]
    public async Task CrlfLineEndings_CountAsOneLineEach_WcParity()
    {
        var atCap = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Windows.cs", AnalyzerHarness.SourceOfCrlfLines(800)) }, baseline: "");
        Assert.Empty(atCap);

        var pastCap = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Windows.cs", AnalyzerHarness.SourceOfCrlfLines(801)) }, baseline: "");
        var d = Assert.Single(pastCap);
        Assert.Equal("KESIZE002", d.Id);
        Assert.Contains("801", d.GetMessage());
    }

    [Fact]
    public async Task CrlfAndLf_OfTheSameLength_AgreeOnTheCount()
    {
        // The same file with the two endings must produce the same reported line count.
        var lf = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Mixed.cs", AnalyzerHarness.SourceOfLines(1201)) },
            baseline: "1200 Src/Mixed.cs\n");
        var crlf = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Mixed.cs", AnalyzerHarness.SourceOfCrlfLines(1201)) },
            baseline: "1200 Src/Mixed.cs\n");

        Assert.Equal(Assert.Single(lf).GetMessage(), Assert.Single(crlf).GetMessage());
    }

    [Theory]
    [InlineData("Src/A.Designer.cs")]
    [InlineData("Src/A.g.cs")]
    [InlineData("Src/A.generated.cs")]
    [InlineData("Src/A.AssemblyInfo.cs")]
    [InlineData("obj/Gen.cs")]
    [InlineData("Src/obj/Gen.cs")]
    [InlineData("bin/Gen.cs")]
    [InlineData("vendor/V.cs")]
    public async Task ExcludedPaths_AreSilent(string relative)
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/" + relative, AnalyzerHarness.SourceOfLines(5000)) }, baseline: "");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task FileOutsideBaselineRoot_IsSkipped()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { ("/elsewhere/Huge.cs", AnalyzerHarness.SourceOfLines(5000)) }, baseline: "");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task BackslashSeparators_MatchBaselineEntries()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + @"\Src\Frozen.cs", AnalyzerHarness.SourceOfLines(1201)) },
            baseline: "1200 Src/Frozen.cs\n");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE001", d.Id);
    }

    [Fact]
    public async Task CapOverride_ViaCompilerVisibleProperty()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Small.cs", AnalyzerHarness.SourceOfLines(101)) },
            baseline: "", capOverride: "100");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE002", d.Id);
        Assert.Contains("100-line cap", d.GetMessage());
    }

    [Fact]
    public async Task Diagnostic_PointsAtFirstLinePastTheLimit()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Frozen.cs", AnalyzerHarness.SourceOfLines(12)) },
            baseline: "10 Src/Frozen.cs\n");
        var d = Assert.Single(diags);
        Assert.Equal(10, d.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task Exempt_SuppressesWhatWouldBe002()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Shaders.cs", AnalyzerHarness.SourceOfLines(5000)) },
            baseline: "# size is content, not structure\nexempt Src/Shaders.cs\n");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Exempt_SuppressesWhatWouldBe001()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Shaders.cs", AnalyzerHarness.SourceOfLines(1201)) },
            baseline: "exempt Src/Shaders.cs\n1200 Src/Shaders.cs\n");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Exempt_WinsOverNumericEntry_WhenNumericComesFirst()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Shaders.cs", AnalyzerHarness.SourceOfLines(1201)) },
            baseline: "1200 Src/Shaders.cs\nexempt Src/Shaders.cs\n");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Exempt_PathWithSpaces_Parses()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Big Blob.cs", AnalyzerHarness.SourceOfLines(5000)) },
            baseline: "exempt Src/Big Blob.cs\n");
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Exempt_LeadingWhitespaceAndTabSeparator_AreTolerated()
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Shaders.cs", AnalyzerHarness.SourceOfLines(5000)) },
            baseline: "  \texempt\tSrc/Shaders.cs\n");
        Assert.Empty(diags);
    }

    [Theory]
    [InlineData("exempted Src/Shaders.cs")]
    [InlineData("Exempt Src/Shaders.cs")]
    [InlineData("EXEMPT Src/Shaders.cs")]
    [InlineData("#exempt Src/Shaders.cs")]
    [InlineData("# exempt Src/Shaders.cs")]
    [InlineData("exemptSrc/Shaders.cs")]
    [InlineData("exempt")]
    [InlineData("exempt   ")]
    public async Task NonExemptKeyword_IsSkippedSilently_AndExemptsNothing(string baselineLine)
    {
        var diags = await AnalyzerHarness.Run(
            new[] { (Root + "/Src/Shaders.cs", AnalyzerHarness.SourceOfLines(5000)) },
            baseline: baselineLine + "\n");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE002", d.Id);
    }

    [Fact]
    public async Task Exempt_DoesNotAffectOtherPaths()
    {
        var diags = await AnalyzerHarness.Run(
            new[]
            {
                (Root + "/Src/Shaders.cs", AnalyzerHarness.SourceOfLines(5000)),
                (Root + "/Src/Other.cs", AnalyzerHarness.SourceOfLines(801)),
            },
            baseline: "exempt Src/Shaders.cs\n");
        var d = Assert.Single(diags);
        Assert.Equal("KESIZE002", d.Id);
        Assert.Contains("Src/Other.cs", d.GetMessage());
    }
}
