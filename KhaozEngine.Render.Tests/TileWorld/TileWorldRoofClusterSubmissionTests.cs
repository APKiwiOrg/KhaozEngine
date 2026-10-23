using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldRoofClusterSubmissionTests
{
    static readonly PropClusterKey Key = new("roofs", 0, 0, 1);
    static readonly PropPlacement[] Placements =
    [new("roof", 4.5f, 3f, -4.5f, 1f, 0f, 0), new("roof", 12.5f, 3f, -4.5f, 1f, 0f, 0)];

    [Fact]
    public void Mixed_cluster_without_individual_geometry_submits_only_the_merged_shadow()
    {
        var backend = new Backend();
        using var renderer = new PropClusterRenderer(backend, PropHlod.BuildMergedMeshMeasured, static (_, _) => { });
        PropLayer layer = Layer();
        var built = renderer.BuildCpu(new PropClusterBuildRequest(Key, 1, new RectArea(0, -64, 64, 0), layer, Placements));
        renderer.Apply(Key, built.WithPlacementBatch(Array.Empty<PropPlacement>()));

        var counts = renderer.DrawWithVisibility(TileWorldRoofPropLayerTests.Focus(300f), new Visibility());

        Assert.Equal(0, backend.Visible);
        Assert.Equal(1, backend.MergedShadows);
        Assert.Equal(new PropClusterDrawCounts(0, 2), counts);
    }

    [Fact]
    public void Failed_rebuild_checks_the_retained_merged_generation_visibility()
    {
        var backend = new Backend();
        using var renderer = new PropClusterRenderer(backend, PropHlod.BuildMergedMeshMeasured, static (_, _) => { });
        PropLayer layer = Layer();
        renderer.Apply(Key, renderer.BuildCpu(new PropClusterBuildRequest(Key, 1, new RectArea(0, -64, 64, 0), layer, Placements)));
        renderer.Apply(Key, new PropClusterCpuBuild(Key, 2, new RectArea(0, -64, 64, 0), layer,
            Array.Empty<PropPlacement>(), null, false, true, new InvalidOperationException("fixture failure"), 0));
        var visibility = new Visibility();

        renderer.DrawWithVisibility(TileWorldRoofPropLayerTests.Focus(300f), visibility);

        Assert.Equal(1, visibility.LastGeneration);
        Assert.Equal(0, backend.Visible);
        Assert.Equal(1, backend.MergedShadows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shadow_submission_preserves_transform_lod_complement_and_hlod_floor(bool parts)
    {
        var placements = new[] { new PropPlacement("roof", 10f, 3f, 0f, 2f, MathF.PI / 2f, 0) };
        PropLayer layer = parts
            ? PropLayer.PlacementLayer(placements,
                new Dictionary<string, IReadOnlyList<MeshHandle>> { ["roof"] = new[] { new MeshHandle(1) } },
                100f, 20f,
                new Dictionary<string, IReadOnlyList<MeshHandle>> { ["roof"] = new[] { new MeshHandle(2) } }, 10f)
            : PropLayer.PlacementLayer(placements,
                new Dictionary<string, MeshHandle> { ["roof"] = new(1) }, 100f, 20f,
                new Dictionary<string, MeshHandle> { ["roof"] = new(2) }, 10f);
        layer = layer.WithLodCrossfade(4f);
        var draws = new List<(MeshHandle Mesh, Matrix4x4 World, float Dissolve, float Complement)>();

        int count = PropClusterShadowSubmission.Emit(placements, layer, Vector3.Zero, .25f, draws,
            static (list, mesh, world, dissolve, complement) => list.Add((mesh, world, dissolve, complement)));

        Assert.Equal(1, count);
        Assert.Equal(2, draws.Count);
        Assert.Equal(new MeshHandle(1), draws[0].Mesh);
        Assert.Equal(new MeshHandle(2), draws[1].Mesh);
        Assert.Equal(.625f, draws[0].Dissolve);
        Assert.Equal(.375f, draws[1].Dissolve);
        Assert.Equal(0f, draws[0].Complement);
        Assert.Equal(1f, draws[1].Complement);
        Vector3 point = Vector3.Transform(Vector3.UnitX, draws[0].World);
        Assert.Equal(10f, point.X, 5);
        Assert.Equal(3f, point.Y);
        Assert.Equal(-2f, point.Z, 5);
        Assert.Equal(draws[0].World, draws[1].World);
    }

    [Fact]
    public void Older_cluster_adapter_keeps_uncontrolled_draws_and_refuses_roof_visibility()
    {
        var legacy = new LegacyOwner();
        ITileWorldPropClusterOwner adapter = legacy;
        Assert.Equal(default, adapter.DrawWithVisibility(Vector3.Zero, new Visibility { RequiresVisibility = false }));
        Assert.Equal(1, legacy.Draws);
        Assert.Throws<NotSupportedException>(() => adapter.DrawWithVisibility(Vector3.Zero, new Visibility()));
        Assert.Equal(1, legacy.Draws);
    }

    sealed class LegacyOwner : ITileWorldPropClusterOwner
    {
        public int Draws;
        public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request) => throw new NotSupportedException();
        public void Apply(PropClusterKey key, PropClusterCpuBuild build) { }
        public void Invalidate(PropClusterKey key) { }
        public void Unload(PropClusterKey key) { }
        public void Draw(Vector3 focus) => Draws++;
        public void Dispose() { }
    }

    static PropLayer Layer() => PropLayer.PlacementLayer(Placements,
            new Dictionary<string, MeshHandle> { ["roof"] = new(1) }, 576f, 32f)
        .WithHlod(new Dictionary<string, GltfMesh> { ["roof"] = MeshPrimitives.Box(1f) }, 192f, 0f, 32f);

    sealed class Visibility : IPropClusterVisibility
    {
        public long LastGeneration;
        public bool RequiresVisibility { get; init; } = true;
        public bool Controls(PropClusterKey key) => true;
        public int PlacementCount(PropClusterKey key, long generation) => 2;
        public bool IsHidden(PropClusterKey key, long generation, int index)
        {
            LastGeneration = generation;
            return generation == 1 && index == 0;
        }
    }

    sealed class Backend : IPropClusterRenderBackend
    {
        public int Visible;
        public int MergedShadows;
        public MeshHandle LoadMesh(GltfMesh mesh) => new(10);
        public void UnloadMesh(MeshHandle mesh) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float floor)
        {
            Visible += placements.Count;
            return placements.Count;
        }
        public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) => Visible++;
        public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
            float floor) => placements.Count;
        public void DrawShadowOnlyMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) => MergedShadows++;
    }
}
