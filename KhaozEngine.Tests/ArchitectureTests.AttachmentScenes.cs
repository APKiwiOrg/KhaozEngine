using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    [Fact]
    public void TileWorldRenderer_ReferencesTheOptionalAttachmentCapability()
    {
        string[] actual = LoadGraph()["KhaozEngine.TileWorld.Render3D"].ProjectRefs
            .Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "Attachments.Render3D", "Render2D", "Render3D", "Terrain", "Terrain.Render3D", "TileWorld" }, actual);
    }
}
