using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public partial class McpAdapterTests
{
    [Fact]
    public async Task NativeRenameToolSchemaDistinguishesLabelFromAnalyticId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpHarness.StartAsync(cts.Token);
        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
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
