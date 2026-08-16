using System.Reflection;
using KhaozEngine.TileEdit.Tools;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Scaffold-level guard for the ke-tileedit tool: the test project is wired to the tool assembly and
/// that assembly loads. Replaced in substance as the session, the services and the verb classes land, but kept
/// so this project always has at least one test and a green run means something.</summary>
public class ScaffoldTests
{
    [Fact]
    public void ToolAssembly_LoadsThroughItsCompositionRoot()
    {
        Assembly tool = typeof(McpBootstrap).Assembly;

        Assert.Equal("KhaozEngine.TileEdit.Tool", tool.GetName().Name);
    }
}
