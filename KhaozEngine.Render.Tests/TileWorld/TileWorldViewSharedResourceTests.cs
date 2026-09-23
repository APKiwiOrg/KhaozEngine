using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>What a view holds for its whole life and must hold exactly once: the placeholder box every unresolved
/// archetype draws as, and the mesher settings every ground mesh is built from. The recording fake throws on a
/// double free, so every dispose here also proves a shared handle is freed once.</summary>
public sealed class TileWorldViewSharedResourceTests
{
    static readonly Vector3 HouseFocus = new(11f, 0f, -10.5f);

    // The house's ten wall-family objects on plane 0 and its six roof tiles on plane 1.
    const int HouseProps = 16;

    [Fact]
    public void Every_unresolved_archetype_shares_one_placeholder_upload()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var log = new List<string>();
        var view = new TileWorldView(scene, TileRenderTestData.HouseWorld(), catalogs, new Unresolving(),
                                     new TileWorldViewOptions { Log = log.Add });

        // Twelve archetypes, twelve log lines, one upload.
        Assert.Equal(catalogs.Archetypes.Count, log.Count);
        Assert.Single(scene.PropMeshLoads);
        int placeholderParts = scene.PropMeshLoads[0].Count;
        Assert.Equal(placeholderParts, scene.AliveMeshCount);

        // Walls, doorway and roofs all still draw, through the one set.
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(HouseFocus);
        Assert.Equal(HouseProps, view.LastDrawnProps);

        view.Dispose();
        Assert.Equal(0, scene.AliveMeshCount);
        Assert.Equal(placeholderParts, scene.MeshUnloads.Count(h => scene.PropMeshLoads[0].Contains(h)));
    }

    [Fact]
    public void Resolved_archetypes_keep_their_own_uploads_beside_the_shared_placeholder()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var unresolved = new HashSet<string>(StringComparer.Ordinal) { "wall", "doorway", "roof_flat" };
        var view = new TileWorldView(scene, TileRenderTestData.HouseWorld(), catalogs, new Unresolving(unresolved));

        Assert.Equal(catalogs.Archetypes.Count - unresolved.Count + 1, scene.PropMeshLoads.Count);

        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(HouseFocus);
        Assert.Equal(HouseProps, view.LastDrawnProps);

        // Every archetype part and the region's ground mesh, each freed once.
        view.Dispose();
        Assert.Equal(0, scene.AliveMeshCount);
        Assert.Equal(scene.PropMeshLoads.Sum(parts => parts.Count) + scene.MeshLoads.Count, scene.MeshUnloads.Count);
    }

    [Fact]
    public void A_constructor_that_throws_frees_the_shared_placeholder_once()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        // The first three archetypes are unresolved and share the first upload, the fourth resolves, and the fake
        // refuses that second upload. A rollback that freed the placeholder per archetype would double free it,
        // and the fake would answer with its own already-unloaded throw instead of the refusal.
        var unresolved = new HashSet<string>(catalogs.Archetypes.Keys.Take(3), StringComparer.Ordinal);
        scene.ThrowOnPropMeshLoad = 2;

        var ex = Assert.Throws<InvalidOperationException>(() => new TileWorldView(
            scene, TileRenderTestData.HouseWorld(), catalogs, new Unresolving(unresolved)));

        Assert.Contains("refused", ex.Message, StringComparison.Ordinal);
        Assert.Single(scene.PropMeshLoads);
        Assert.Equal(0, scene.AliveMeshCount);
    }

    [Fact]
    public void The_view_builds_every_ground_mesh_from_the_mesher_settings_it_was_constructed_with()
    {
        var scene = new RecordingTileWorldScene();
        // The hill, so flat and smooth normals and a jitter change all move vertices a live read would pick up.
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        var options = new TileWorldViewOptions();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options);
        TileGroundMesherOptions handedIn = options.Mesher;
        view.LoadRegion(TileRenderTestData.Region);
        GltfMesh built = LatestGround(scene);

        // A change to the object the view was handed.
        handedIn.SmoothNormals = false;
        handedIn.JitterAmplitude = 0f;
        GltfMesh afterChange = Rebuild(scene, view);

        // A replacement of it, whose slot map is the identity stand-in rather than the uploaded set.
        options.Mesher = new TileGroundMesherOptions();
        GltfMesh afterReplace = Rebuild(scene, view);

        Assert.True(SameBytes(built, afterChange), "a change to the handed-in mesher settings reached a rebuild");
        Assert.True(SameBytes(built, afterReplace), "a replaced mesher settings object reached a rebuild");
        // The slot map the view wrote is still readable where the caller left it.
        Assert.Same(view.GroundMaterials, handedIn.Slots);
    }

    [Fact]
    public void The_mesher_settings_copy_carries_every_setting()
    {
        var source = new TileGroundMesherOptions
        {
            JitterAmplitude = 0.25f,
            SmoothNormals = false,
            Slots = TileGroundMaterials.Build(TileRenderTestData.Catalogs),
        };
        TileGroundMesherOptions copy = source.Copy();

        Assert.NotSame(source, copy);
        Assert.Equal(source.JitterAmplitude, copy.JitterAmplitude);
        Assert.Equal(source.SmoothNormals, copy.SmoothNormals);
        Assert.Same(source.Slots, copy.Slots);
        // The tripwire: a setting added to the options has to be added to Copy and to the checks above, or a view
        // silently builds without it.
        Assert.Equal(new[] { "JitterAmplitude", "Slots", "SmoothNormals" },
            typeof(TileGroundMesherOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    static GltfMesh Rebuild(RecordingTileWorldScene scene, TileWorldView view)
    {
        int before = scene.MeshLoads.Count;
        view.MarkDirty(TileRenderTestData.Region, 0);
        view.Flush();
        Assert.Equal(before + 1, scene.MeshLoads.Count);
        return LatestGround(scene);
    }

    static GltfMesh LatestGround(RecordingTileWorldScene scene) => scene.GroundMeshes[scene.MeshLoads[^1].Index];

    static bool SameBytes(GltfMesh a, GltfMesh b) =>
        MemoryMarshal.AsBytes(a.Vertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Vertices.AsSpan()))
        && a.Indices32.AsSpan().SequenceEqual(b.Indices32);

    // Greybox for every archetype except the named ones, which resolve to nothing. With no names, nothing resolves.
    sealed class Unresolving : ITileMeshResolver
    {
        readonly GreyboxMeshResolver _inner = new();
        readonly IReadOnlySet<string>? _unresolved;

        public Unresolving(IReadOnlySet<string>? unresolved = null) => _unresolved = unresolved;

        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
        {
            ArgumentNullException.ThrowIfNull(archetype);
            return _unresolved is null || _unresolved.Contains(archetype.Id) ? null : _inner.Resolve(archetype);
        }
    }
}
