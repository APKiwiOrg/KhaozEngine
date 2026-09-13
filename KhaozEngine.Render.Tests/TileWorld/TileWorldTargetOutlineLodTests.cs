using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldTargetOutlineLodTests
{
    [Fact]
    public void Selected_tree_outline_follows_the_live_Lod_and_Hlod_dissolve_representations()
    {
        TileWorldDocument document = TileRenderTestData.HouseWorld();
        TileObject tree = document.AddObject("tree", 25, 25, 0, 0);
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, document, TileRenderTestData.Catalogs, new LodResolver(),
            new TileWorldViewOptions { PropLayers = new[] { TreeLayer() } },
            new TileWorldBuildQueueOptions { MaxHlodAppliesPerPump = 4 }, new ImmediateDispatcher());
        view.LoadRegion(TileRenderTestData.Region);
        view.SetOutlinedObject(tree.Id, new KhaozEngine.Primitives.Color(1f, 0f, 0f, 1f));
        var placement = new Vector3(25.5f, 0f, -25.5f);

        view.Draw(placement + new Vector3(64f, 0f, 0f));

        RecordedOutlineGroup lod = Assert.Single(scene.OutlineGroups);
        Assert.Equal(2, lod.Parts.Count);
        Assert.Contains(lod.Parts, part => !part.Complement && MathF.Abs(part.Dissolve - 0.5f) < 0.001f);
        Assert.Contains(lod.Parts, part => part.Complement && MathF.Abs(part.Dissolve - 0.5f) < 0.001f);

        scene.OutlineGroups.Clear();
        Vector3 regionCenter = new(32f, 0f, -32f);
        view.Draw(regionCenter + new Vector3(192f, 0f, 0f));

        RecordedOutlineGroup hlod = Assert.Single(scene.OutlineGroups);
        Assert.Contains(hlod.Parts, part => part.Complement);
        Assert.Contains(hlod.Parts, part => !part.Complement);
        Assert.True(scene.MeshLoads.Count > 0, "selected HLOD mask mesh was not uploaded");

        int unloads = scene.MeshUnloads.Count;
        view.ClearOutlinedObject();
        Assert.Equal(unloads + 1, scene.MeshUnloads.Count);
    }

    static TilePropLayerDefinition TreeLayer() => new()
    {
        Id = "trees",
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree" },
        DrawRadius = 576f,
        LodDistance = 64f,
        LodCrossfadeWidth = 16f,
        HlodDistance = 192f,
        HlodCrossfadeWidth = 32f,
        HlodWeldCell = 1.5f,
    };

    sealed class LodResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GreyboxMeshResolver _inner = new();
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) => _inner.Resolve(archetype);
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) => _inner.Resolve(archetype);
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _inner.Resolve(archetype)?.Single().Mesh;
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }
}
