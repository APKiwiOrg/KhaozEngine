using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class PropVisibilityFilterTests
{
    static readonly PropPlacement[] Placements =
    {
        new("oak", 1f, 0f, 1f, 1f, 0f, 0),
        new("rock", 2f, 0f, 2f, 1f, 0f, 0),
    };

    [Fact]
    public void FlatAndTexturedEmit_ApplyTheSameLayerAndKitFilter()
    {
        var flatMeshes = new Dictionary<string, MeshHandle>
        {
            ["oak"] = new MeshHandle(1),
            ["rock"] = new MeshHandle(2),
        };
        var partMeshes = new Dictionary<string, IReadOnlyList<MeshHandle>>
        {
            ["oak"] = new[] { new MeshHandle(3), new MeshHandle(4) },
            ["rock"] = new[] { new MeshHandle(5) },
        };
        int flatSubmissions = 0;
        int partSubmissions = 0;
        PropDrawFilter filter = static (layer, kit) => layer == "forest" && kit == "oak";

        int flat = PropRenderer.Emit(Placements, flatMeshes, null, 0f, Vector3.Zero, 100f, 0f, 0f,
            0, (_, _, _, _, _) => flatSubmissions++, drawFilter: filter, layerIdentity: "forest");
        int textured = PropRenderer.EmitParts(Placements, partMeshes, null, 0f, Vector3.Zero, 100f, 0f, 0f,
            0, (_, _, _, _, _) => partSubmissions++, drawFilter: filter, layerIdentity: "forest");

        Assert.Equal(1, flat);
        Assert.Equal(1, flatSubmissions);
        Assert.Equal(1, textured);
        Assert.Equal(2, partSubmissions);
    }

    [Fact]
    public void MixedHlodCluster_FallsBackToFilteredIndividuals()
    {
        var backend = new RecordingBackend();
        using var renderer = new PropClusterRenderer(backend, Merge, static (_, _) => { });
        PropLayer layer = PropLayer.PlacementLayer(Placements,
                new Dictionary<string, MeshHandle>
                {
                    ["oak"] = new MeshHandle(1),
                    ["rock"] = new MeshHandle(2),
                }, 500f, castsShadows: false)
            .WithHlod(new Dictionary<string, GltfMesh>
            {
                ["oak"] = MeshPrimitives.Box(1f),
                ["rock"] = MeshPrimitives.Box(1f),
            }, 10f, 0f)
            .WithIdentity("forest");
        var key = new PropClusterKey("forest", 0, 0, 0);
        var request = new PropClusterBuildRequest(key, 1, new RectArea(0f, 0f, 10f, 10f), layer, Placements);
        renderer.Apply(key, renderer.BuildCpu(request));

        renderer.Draw(new Vector3(100f, 0f, 100f), static (_, kit) => kit == "oak");

        Assert.Equal(1, backend.VisibleIndividuals);
        Assert.Equal(0, backend.MergedDraws);
        Assert.False(backend.LastCastsShadows);

        backend.Reset();
        renderer.Draw(new Vector3(1000f, 0f, 1000f), static (_, kit) => kit == "oak");
        Assert.Equal(0, backend.DrawCalls);
    }

    static GltfMesh Merge(IReadOnlyList<PropPlacement> placements,
        IReadOnlyDictionary<string, GltfMesh> meshes, float weldCell, out long dropped)
    {
        dropped = 0;
        return MeshPrimitives.Box(1f);
    }

    sealed class RecordingBackend : IPropClusterRenderBackend
    {
        public int VisibleIndividuals;
        public int MergedDraws;
        public int DrawCalls;
        public bool LastCastsShadows = true;

        public MeshHandle LoadMesh(GltfMesh mesh) => new(99);
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
            float dissolveFloor)
        {
            DrawCalls++;
            VisibleIndividuals += placements.Count;
            LastCastsShadows = layer.CastsShadows;
        }
        public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve) =>
            MergedDraws++;
        public void Reset() { VisibleIndividuals = 0; MergedDraws = 0; DrawCalls = 0; }
    }
}
