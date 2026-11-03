using System.Threading.Tasks;
using KhaozEngine.CodeHealth.Analyzers;
using Xunit;

namespace KhaozEngine.Tests.CodeHealth;

public class AnalyzerPathPolicyTests
{
    [Theory]
    [InlineData("/repo/A/..", "/repo/A/../X.cs", "X.cs")]
    [InlineData("/repo/", "/repo//X.cs", "X.cs")]
    [InlineData("/repo", "/repo/X.cs", "X.cs")]
    [InlineData(".", "./X.cs", "X.cs")]
    [InlineData("A/..", "A/../X.cs", "X.cs")]
    [InlineData("", "/X.cs", "X.cs")]
    [InlineData("C:/repo", @"c:\repo/A\..\X.cs", "X.cs")]
    [InlineData("//server/share", @"\\SERVER\share\A/../X.cs", "X.cs")]
    [InlineData("/", "//server/share/X.cs", "server/share/X.cs")]
    [InlineData("C:", "C:/repo/X.cs", "repo/X.cs")]
    [InlineData("//?/C:/repo", "//?/C:/repo/X.cs", "X.cs")]
    public void RelativePathKeepsPreTaskRootHandling(string root, string path, string expectedRelative)
    {
        Assert.True(AnalyzerPathPolicy.TryGetCheckedRelativePath(path, root, out var relative));
        Assert.Equal(expectedRelative, relative);
    }

    [Theory]
    [InlineData("..", "../X.cs", "X.cs")]
    [InlineData("/repo", "/repo/", "")]
    [InlineData("/repo", "/repo/X.g.cs/", "X.g.cs/")]
    [InlineData("/repo", "/repo//X.cs", "/X.cs")]
    [InlineData("/repo", "/repo/A//X.cs/", "A//X.cs/")]
    public void CheckedPathsKeepTheirPreTaskAcceptance(string root, string path, string expectedRelative)
    {
        Assert.True(AnalyzerPathPolicy.TryGetCheckedRelativePath(path, root, out var relative));
        Assert.Equal(expectedRelative, relative);
    }

    [Theory]
    [InlineData(".", "../X.cs", "", ".", "./X.cs")]
    [InlineData(".", "/X.cs", "", ".", "./X.cs")]
    [InlineData("/", "X.cs", "", "/", "//X.cs")]
    [InlineData("", "X.cs", "", "", "/X.cs")]
    [InlineData(".", "./obj/X.cs", "obj/X.cs", ".", "./X.cs")]
    [InlineData(".", "./X.g.cs", "X.g.cs", ".", "./X.cs")]
    [InlineData(".", "C:/repo/X.cs", "", ".", "./X.cs")]
    [InlineData(".", @"C:\repo\X.cs", "", ".", "./X.cs")]
    [InlineData("C:/", "X.cs", "", "C:/", "C://X.cs")]
    [InlineData("C:/", "C:/../X.cs", "", "C:/", "C://X.cs")]
    [InlineData("C:/", "C:/../C:/X.cs", "", "C:/", "C://X.cs")]
    [InlineData("C:/", "C:/repo/../X.cs", "", "C:/", "C://X.cs")]
    [InlineData("c:/", @"C:\repo\.\..\X.cs", "", "c:/", "C://X.cs")]
    [InlineData("//server/share", "//server/share/../share/X.cs", "../share/X.cs",
        "//server/share", "//server/share/./X.cs")]
    [InlineData("/repo", "/repo/A/../obj/X.cs", "obj/X.cs", "/repo", "/repo/A/../X.cs")]
    [InlineData("/repo/", "/repo/obj/X.cs", "", "/repo/", "/repo//X.cs")]
    [InlineData(@"C:\repo", @"C:\repo\X.cs", "", "C:/repo", @"C:\repo\X.cs")]
    [InlineData("/repo", "/repo/A/../../X.cs", "A/../../X.cs", "/repo", "/repo/A/../X.cs")]
    [InlineData("/repo", "/repo/./../X.cs", "./../X.cs", "/repo", "/repo/./X.cs")]
    [InlineData("/repo", "/repo/A/../X.g.cs", "X.g.cs", "/repo", "/repo/A/../X.cs")]
    public void RejectedPathsKeepContainmentAndCollapsedExclusions(
        string root, string path, string expectedRelative, string controlRoot, string controlPath)
    {
        Assert.True(AnalyzerPathPolicy.TryGetCheckedRelativePath(controlPath, controlRoot, out var control));
        Assert.Equal("X.cs", control);
        Assert.False(AnalyzerPathPolicy.TryGetCheckedRelativePath(path, root, out var relative));
        Assert.Equal(expectedRelative, relative);
    }

    [Theory]
    [InlineData("A//B/../X.cs", "A//X.cs")]
    [InlineData("A//../X.cs", "A/X.cs")]
    [InlineData("/./X.cs", "/X.cs")]
    [InlineData("A/../X.g.cs/", "X.g.cs/")]
    [InlineData("obj/../X.cs", "X.cs")]
    [InlineData("A/..", "")]
    [InlineData("./", "")]
    public void DotSegmentCollapsePreservesOtherRelativeSegments(string suffix, string expectedRelative)
    {
        Assert.True(AnalyzerPathPolicy.TryGetCheckedRelativePath("/repo/" + suffix, "/repo", out var relative));
        Assert.Equal(expectedRelative, relative);
    }

    [Theory]
    [InlineData("/repo/A/../test-shared/X.cs", "test-shared/X.cs", true)]
    [InlineData("/repo/A/./B.cs", "A/B.cs", true)]
    [InlineData("/repo/../outside/X.cs", "outside/X.cs", false)]
    [InlineData(@"\repo\A\..\test-shared\X.cs", "test-shared/X.cs", true)]
    public async Task RelativePathCollapsesDotSegments(string path, string expectedRelative, bool expectedChecked)
    {
        var source = AnalyzerHarness.SourceOfLines(2);
        var baseline = $"1 {expectedRelative}\n";
        var control = await AnalyzerHarness.Run(
            new[] { (AnalyzerHarness.Root + "/" + expectedRelative, source) }, baseline, capOverride: "1");
        Assert.Equal("KESIZE001", Assert.Single(control).Id);

        var diagnostics = await AnalyzerHarness.Run(new[] { (path, source) }, baseline, capOverride: "1");
        if (expectedChecked)
            Assert.Equal("KESIZE001", Assert.Single(diagnostics).Id);
        else
            Assert.Empty(diagnostics);
    }
}
