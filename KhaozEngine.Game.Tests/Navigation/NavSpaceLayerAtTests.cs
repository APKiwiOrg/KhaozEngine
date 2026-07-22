using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavSpaceLayerAtTests
{
    const float StepHeight = 0.5f;
    const float AgentHeight = 0f;
    const float JumpHeight = 1.2f;

    // Reuses the isolated bridge footprint from NavLayerBakerTests: deck at height 2.0 over x in
    // [6..9], z in [2..4] (two surfaces per column), ramps climbing from ground either side. Scoped to
    // exactly the bridge's footprint so the bake stays the clean two-layer split (see the discovered
    // BFS-race note in NavLayerBakerTests.cs for why a wider, open-field bake is not used here).
    static DelegateColumnProvider BridgeProvider() => new((x, z, surfaces) =>
    {
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);

        if (cz is >= 2 and <= 4)
        {
            if (cx == 2) { surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1; }
            if (cx == 3) { surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1; }
            if (cx == 4) { surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1; }
            if (cx == 5) { surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1; }
            if (cx == 10) { surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1; }
            if (cx == 11) { surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1; }
            if (cx == 12) { surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1; }
            if (cx == 13) { surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1; }
            if (cx is >= 6 and <= 9)
            {
                surfaces[0] = new NavSurfaceSample(true, 0f, 2.0f);
                surfaces[1] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
                return 2;
            }
        }

        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    static int FindLayer(NavSpace space, int cx, int cz, float expectedHeight, float tolerance = 0.01f)
    {
        for (int l = 0; l < space.Layers.Count; l++)
        {
            float? h = space.Layers[l].SurfaceHeightAt(cx, cz);
            if (h is not null && MathF.Abs(h.Value - expectedHeight) <= tolerance)
                return l;
        }

        throw new Xunit.Sdk.XunitException(
            $"No layer has a passable surface at ({cx}, {cz}) near height {expectedHeight}.");
    }

    static NavSpace BridgeSpace()
        => NavLayerBaker.BakeOverworldLayered(
            BridgeProvider(), minX: 0f, minZ: 2f, maxX: 16f, maxZ: 5f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

    [Fact]
    public void OnDeck_ResolvesToDeckLayer()
    {
        NavSpace space = BridgeSpace();
        int deckLayer = FindLayer(space, 7, 1, 2.0f);

        int resolved = space.LayerAt(new Vector3(7.5f, 2.0f, 3.5f));

        Assert.Equal(deckLayer, resolved);
    }

    [Fact]
    public void UnderDeck_ResolvesToGroundLayer()
    {
        NavSpace space = BridgeSpace();
        int groundLayer = FindLayer(space, 7, 1, 0.0f);

        int resolved = space.LayerAt(new Vector3(7.5f, 0.1f, 3.5f));

        Assert.Equal(groundLayer, resolved);
    }

    [Fact]
    public void DeckAndGroundLayers_AreDistinct()
    {
        NavSpace space = BridgeSpace();

        int deckLayer = FindLayer(space, 7, 1, 2.0f);
        int groundLayer = FindLayer(space, 7, 1, 0.0f);

        Assert.NotEqual(deckLayer, groundLayer);
    }

    [Fact]
    public void SingleLayerSpace_AlwaysZero()
    {
        NavGrid grid = NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true);
        NavSpace single = NavSpace.Single(grid);

        Assert.Equal(0, single.LayerAt(new Vector3(1.5f, 100f, 1.5f)));
        Assert.Equal(0, single.LayerAt(new Vector3(-50f, -50f, -50f)));
    }

    [Fact]
    public void NoSurfaceHeights_FallsBackToLayerOf_YBand()
    {
        // Two FromWalkable grids (no per-cell surface heights), each with its own Y band. LayerAt has
        // no surface data to be surface-aware about, so it must fall back to LayerOf's band resolution
        // exactly, matching every pre-layered space.
        NavGrid lower = NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true, yMin: 0f, yMax: 2f);
        NavGrid upper = NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true, yMin: 5f, yMax: 7f);
        var space = new NavSpace(new[] { lower, upper });

        var lowerPoint = new Vector3(1.5f, 1f, 1.5f);
        var upperPoint = new Vector3(1.5f, 6f, 1.5f);

        Assert.Equal(space.LayerOf(lowerPoint.Y), space.LayerAt(lowerPoint));
        Assert.Equal(space.LayerOf(upperPoint.Y), space.LayerAt(upperPoint));
        Assert.Equal(0, space.LayerAt(lowerPoint));
        Assert.Equal(1, space.LayerAt(upperPoint));
    }
}
