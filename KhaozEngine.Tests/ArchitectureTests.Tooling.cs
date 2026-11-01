using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    // Explicit paths avoid scanning build outputs or sibling worktrees.
    static readonly string[] ExternalToolingTests =
    [
        "tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj",
        "tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj",
        "tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj"
    ];

    [Fact]
    public void Tooling_tests_stay_nonpackable_and_outside_engine_solution_and_references()
    {
        string root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, ExternalToolingTests[0])));
        Assert.True(File.Exists(Path.Combine(root, ExternalToolingTests[1])));
        Assert.True(File.Exists(Path.Combine(root, ExternalToolingTests[2])));
        string[] solution = XDocument.Load(Path.Combine(root, "KhaozEngine.slnx")).Descendants("Project")
            .Select(p => Path.GetFullPath(Path.Combine(root, p.Attribute("Path")!.Value.Replace('\\', '/')))).ToArray();
        string[] tools = ExternalToolingTests.Select(p => Path.GetFullPath(Path.Combine(root, p))).ToArray();
        foreach (string tool in tools)
        {
            Assert.DoesNotContain(tool, solution);
            XElement project = XDocument.Load(tool).Root!;
            Assert.Contains(project.Descendants("IsPackable"), e => e.Value == "false");
            Assert.Contains(project.Descendants("IsTestProject"), e => e.Value == "true");
            Assert.Contains(project.Descendants("OutputType"), e => e.Value == "Library");
            Assert.Contains(project.Descendants("RootNamespace"), e => e.Value == "KhaozEngine.Tests");
        }
        foreach (string directory in Directory.EnumerateDirectories(root, "KhaozEngine*"))
        foreach (string project in Directory.EnumerateFiles(directory, "*.csproj"))
        foreach (string target in ToolingProjectReferences(project))
            Assert.DoesNotContain(target, tools);
    }

    [Theory]
    [InlineData("Renderer.Perf.Verify")]
    [InlineData("Renderer.Perf.Tests")]
    public void Renderer_executables_reference_only_the_isolated_measurement_assembly(string name)
    {
        string root = RepoRoot();
        string project = Path.Combine(root, "tools", name, name + ".csproj");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, ExternalToolingTests[0])), Assert.Single(ToolingProjectReferences(project)));
        Assert.DoesNotContain(ToolingProjectReferences(Path.Combine(root, ExternalToolingTests[0])),
            target => target.Contains("KhaozEngine.Render.Tests", StringComparison.Ordinal));
    }

    static string[] ToolingProjectReferences(string project) => XDocument.Load(project).Descendants("ProjectReference")
        .Select(e => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, e.Attribute("Include")!.Value.Replace('\\', '/'))))
        .ToArray();
}
