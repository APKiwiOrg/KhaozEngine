using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public class PropCollidersTests
{
    static readonly ScatterConfig Cfg = ScatterConfig.ForestRing(seed: 1337);

    static TerrainField Field() => new(TerrainPresets.Clearing());

    [Fact]
    public void FromScatter_OneColliderPerPlacementWithAShape()
    {
        TerrainField f = Field();
        var area = new RectArea(-60f, -60f, 60f, 60f);
        IReadOnlyList<PropPlacement> placements = PropScatter.Generate(f, Cfg, area);
        Assert.NotEmpty(placements);

        WorldColliders set = PropColliders.FromScatter(placements, _ => ColliderShape.Cylinder(0.4f));
        Assert.Equal(placements.Count, set.Count);
    }

    [Fact]
    public void FromScatter_MatchesScatterPositionsScaled()
    {
        TerrainField f = Field();
        var area = new RectArea(-60f, -60f, 60f, 60f);
        IReadOnlyList<PropPlacement> placements = PropScatter.Generate(f, Cfg, area);
        WorldColliders set = PropColliders.FromScatter(placements, _ => ColliderShape.Cylinder(0.5f));

        var byPos = new Dictionary<(float, float), WorldCollider>();
        foreach (WorldCollider wc in set.Colliders) byPos[(wc.Center.X, wc.Center.Y)] = wc;
        foreach (PropPlacement p in placements)
        {
            Assert.True(byPos.TryGetValue((p.X, p.Z), out WorldCollider wc));
            Assert.Equal(ColliderKind.Cylinder, wc.Kind);
            Assert.Equal(0.5f * p.Scale, wc.Radius, 4);
        }
    }

    [Fact]
    public void FromScatter_PerAreaDeterministic_UnionEqualsWhole()
    {
        TerrainField f = Field();
        var whole = PropColliders.FromScatter(
            PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f)),
            _ => ColliderShape.Cylinder(0.4f));
        // Two tiles covering the same region (half-open intervals -> each cell once).
        var left = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 0f, 60f));
        var right = PropScatter.Generate(f, Cfg, new RectArea(0f, -60f, 60f, 60f));
        Assert.Equal(whole.Count, left.Count + right.Count);
    }

    [Fact]
    public void FromScatter_DefaultShape_UsedWhenLookupReturnsNull()
    {
        TerrainField f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        WorldColliders set = PropColliders.FromScatter(placements, _ => null, defaultShape: ColliderShape.Cylinder(0.3f));
        Assert.Equal(placements.Count, set.Count);
    }

    [Fact]
    public void FromScatter_NoShapeAndNoDefault_SkipsPlacement()
    {
        TerrainField f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        WorldColliders set = PropColliders.FromScatter(placements, _ => null);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void FromScatter_ObstaclesAreIncluded()
    {
        TerrainField f = Field();
        var placements = PropScatter.Generate(f, Cfg, new RectArea(-60f, -60f, 60f, 60f));
        var inn = WorldCollider.Box(new Vector2(0f, 10f), new Vector2(3f, 2f), yaw: 0f);
        WorldColliders set = PropColliders.FromScatter(placements, _ => ColliderShape.Cylinder(0.4f),
            obstacles: new[] { inn });
        Assert.Equal(placements.Count + 1, set.Count);
        Assert.Contains(inn, set.Colliders);
    }
}
