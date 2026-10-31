using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace KhaozEngine.Tests.CodeHealth;

public class TestHealthRatchetTests
{
    private const string Path = "/repo/Tests/R.cs";
    private const string Source = """
        using System.Reflection;
        using Xunit;
        public class R
        {
            [Fact]
            public void Inspect()
            {
                var first = BindingFlags.NonPublic | BindingFlags.Instance;
                var second = BindingFlags.NonPublic | BindingFlags.Static;
                Assert.NotEqual(first, second);
            }
        }
        """;

    private static Task<ImmutableArray<Diagnostic>> Run(string? baseline, string? mode = null, bool withXunit = true) =>
        TestHealthHarness.Run(new[] { (Path, Source) }, baseline, mode, withXunit);

    [Fact]
    public async Task NoBaseline_IsSilent() => Assert.Empty(await Run(baseline: null));

    [Fact]
    public async Task NoXunit_IsSilent() => Assert.Empty(await Run(baseline: "", withXunit: false));

    [Fact]
    public async Task AtAllowance_Passes() => Assert.Empty(await Run(baseline: "2 KETEST002 Tests/R.cs\n"));

    [Fact]
    public async Task BelowAllowance_Passes() => Assert.Empty(await Run(baseline: "3 KETEST002 Tests/R.cs\n"));

    [Fact]
    public async Task OverAllowance_ReportsEverySite()
    {
        var diagnostics = await Run(baseline: "1 KETEST002 Tests/R.cs\n");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("KETEST002", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.StartsWith("'Tests/R.cs' has 2 ", diagnostic.GetMessage());
            Assert.Contains(".testhealth-baseline allows 1.", diagnostic.GetMessage());
            Assert.Equal("NonPublic", Source.Substring(diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length));
        });
        Assert.Equal(2, diagnostics.Select(diagnostic => diagnostic.Location.SourceSpan).Distinct().Count());
    }

    [Fact(Skip = "enabled in Task 3")]
    public async Task ExemptPath_IsSilentForThatRuleOnly()
    {
        var sourceWithMessageAssert = Source.Replace("Assert.NotEqual(first, second);",
            "Assert.NotEqual(first, second); Assert.Equal(\"wording\", new System.Exception().Message);");
        var diagnostics = await TestHealthHarness.Run(new[] { (Path, sourceWithMessageAssert) },
            baseline: "exempt KETEST002 Tests/R.cs\n");
        Assert.Equal("KETEST001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task ReportMode_IgnoresEntries_AndWarns()
    {
        var diagnostics = await Run(baseline: "2 KETEST002 Tests/R.cs\n", mode: "report");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.DefaultSeverity);
            Assert.StartsWith("'Tests/R.cs' has 2 non-public reflection access(es), .testhealth-baseline allows 0.",
                diagnostic.GetMessage());
        });
    }

    [Fact]
    public async Task ReportMode_HonoursExempt() =>
        Assert.Empty(await Run(baseline: "exempt KETEST002 Tests/R.cs\n", mode: "report"));

    [Fact]
    public async Task ReportMode_WithoutBaseline_IsSilent() =>
        Assert.Empty(await Run(baseline: null, mode: "report"));

    [Fact]
    public async Task BackslashSourcePath_MatchesForwardSlashEntry() =>
        Assert.Empty(await TestHealthHarness.Run(new[] { (@"\repo\Tests\R.cs", Source) },
            baseline: "2 KETEST002 Tests/R.cs\n"));

    [Fact]
    public async Task UnlistedFile_AllowsZero()
    {
        var diagnostics = await Run(baseline: "2 KETEST001 Tests/R.cs\n");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Contains(".testhealth-baseline allows 0.", diagnostic.GetMessage()));
    }

    [Fact]
    public async Task CountsAndAllowances_ArePerFile()
    {
        var diagnostics = await TestHealthHarness.Run(new[]
        {
            (Path, Source),
            ("/repo/Tests/Other.cs", Source.Replace("class R", "class Other")),
        }, baseline: "2 KETEST002 Tests/R.cs\n1 KETEST002 Tests/Other.cs\n");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
            Assert.StartsWith("'Tests/Other.cs' has 2 ", diagnostic.GetMessage()));
    }

    [Theory]
    [InlineData("obj/R.cs")]
    [InlineData("bin/R.cs")]
    [InlineData("vendor/R.cs")]
    [InlineData("Tests/obj/R.cs")]
    [InlineData("Tests/bin/R.cs")]
    [InlineData("Tests/vendor/R.cs")]
    [InlineData("Tests/R.Designer.cs")]
    [InlineData("Tests/R.g.cs")]
    [InlineData("Tests/R.generated.cs")]
    [InlineData("Tests/R.AssemblyInfo.cs")]
    public async Task ExcludedPaths_AreSilent(string relative) =>
        Assert.Empty(await TestHealthHarness.Run(new[] { ("/repo/" + relative, Source) }, baseline: ""));

    [Theory]
    [InlineData("/elsewhere/R.cs")]
    [InlineData("/repository/R.cs")]
    [InlineData("")]
    public async Task OutsideRootOrMissingPath_IsSilent(string path) =>
        Assert.Empty(await TestHealthHarness.Run(new[] { (path, Source) }, baseline: ""));

    [Fact]
    public async Task GeneratedHeader_IsSilent() =>
        Assert.Empty(await TestHealthHarness.Run(new[] { (Path, "// <auto-generated/>\n" + Source) }, baseline: ""));
}
