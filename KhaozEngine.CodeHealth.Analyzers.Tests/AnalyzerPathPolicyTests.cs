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
    public void RelativePathNormalizesRootAndSource(string root, string path, string expectedRelative)
    {
        Assert.True(AnalyzerPathPolicy.TryGetCheckedRelativePath(path, root, out var relative));
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
