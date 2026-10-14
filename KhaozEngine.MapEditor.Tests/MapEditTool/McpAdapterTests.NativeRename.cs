using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public partial class McpAdapterTests
{
    [Fact]
    public async Task NativeRenameToolSchemaDistinguishesLabelFromAnalyticId()
    {
        await using var harness = await McpWire.StartAsync();
        var tools = await harness.Client.ListAsync();
        var rename = tools.Single(t => t.Name == "placement_rename");
        string parameter = rename.JsonSchema.GetProperty("properties").GetProperty("newId").GetProperty("description").GetString()!;
        foreach (string description in new[] { rename.Description!, parameter })
        {
            Assert.Contains("native", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("label", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("analytic", description, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(rename.JsonSchema.GetProperty("properties").TryGetProperty("oldId", out _));
    }
}
