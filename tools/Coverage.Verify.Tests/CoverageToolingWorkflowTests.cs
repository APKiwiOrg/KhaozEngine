using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KhaozEngine.Tests.CoverageTooling;

public sealed class CoverageToolingWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Coverage_tooling_suite_is_outside_engine_default_tests(bool crlf)
    {
        string[] projects = XDocument.Parse(CoverageUnitSelectionTests.Checkout("KhaozEngine.slnx", crlf))
            .Descendants("Project").Select(p => (string)p.Attribute("Path")!).ToArray();
        Assert.NotEmpty(projects);
        Assert.DoesNotContain(projects, p => p.StartsWith("tools/Coverage.", StringComparison.Ordinal));
        string render = CoverageUnitSelectionTests.Checkout("KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj", crlf);
        Assert.DoesNotContain("tools/Coverage", render);
        string workflow = CoverageUnitSelectionTests.Checkout(".github/workflows/tooling-validation.yml", crlf)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string suite = Regex.Match(workflow, @"(?ms)^  coverage:\n.*?(?=^  [\w-]+:|\z)").Value;
        Assert.NotEmpty(suite);
        Assert.Contains("runs-on: ubuntu-26.04", suite);
        Assert.Contains("KE_GPU_TESTS: '0'", suite);
        Assert.Contains("dotnet build tools/Coverage.Verify/Coverage.Verify.csproj -c Release", suite);
        Assert.Contains("dotnet test tools/Coverage.Verify.Tests/Coverage.Verify.Tests.csproj -c Release", suite);
        Assert.Contains("--list-tests", suite);
        Assert.Contains("--no-build", suite);
        Assert.Contains("total -le 0", suite);
        Assert.Contains("notExecuted -ne '0'", suite);
        Assert.Contains("CoverageVerifyProcessTests", suite);
        Assert.DoesNotContain("KE_GPU_TESTS: '1'", suite);
        Assert.DoesNotContain("workflow run", suite);
        Assert.DoesNotContain("gpu-hosted", suite);
        Assert.DoesNotContain("Showcase", suite);
        Assert.DoesNotContain("--filter", suite);
    }
}
