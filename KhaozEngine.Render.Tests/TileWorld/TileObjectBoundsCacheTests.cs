using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The measuring half of object picking: the box comes from the resolved mesh's own vertices, once
/// per archetype, so the box tested is the model drawn.</summary>
public class TileObjectBoundsCacheTests
{
    sealed class CountingResolver : ITileMeshResolver
    {
        readonly ITileMeshResolver _inner;
        public int Resolves;

        public CountingResolver(ITileMeshResolver inner) => _inner = inner;

        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
        {
            Resolves++;
            return _inner.Resolve(archetype);
        }
    }

    [Fact]
    public void The_box_is_the_meshs_own_and_is_measured_once_per_archetype()
    {
        var resolver = new CountingResolver(new GreyboxMeshResolver(1f, 3f));
        var cache = new TileObjectBoundsCache(resolver);
        TileObjectArchetype archetype = TileRenderTestData.Catalogs.Archetype("wall")!;

        Assert.True(cache.TryGetBounds(archetype, out Vector3 min, out Vector3 max));
        // The measured box is a real volume around the anchor, and it is the greybox's rather than a person's
        // or a tile's: what matters here is only that it came from the vertices.
        Assert.True(max.X > min.X && max.Y > min.Y && max.Z > min.Z, $"degenerate box {min}..{max}");

        Assert.True(cache.TryGetBounds(archetype, out Vector3 again, out _));
        Assert.Equal(min, again);
        Assert.Equal(1, resolver.Resolves);
    }
}
