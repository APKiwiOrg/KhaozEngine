using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Native GPU acceptance for TileWorld's exact LOD, HLOD, exit, shadow, and coarse-ground boundaries.</summary>
public sealed class TileWorldPropHlodGpuTests : IClassFixture<TileWorldPropHlodGpuScene>
{
    readonly TileWorldPropHlodGpuScene _forest;
    readonly ITestOutputHelper _output;

    public TileWorldPropHlodGpuTests(TileWorldPropHlodGpuScene forest, ITestOutputHelper output)
    {
        _forest = forest;
        _output = output;
    }

    [GpuFact]
    public void Forest_keeps_exact_boundary_coverage_shadows_draw_collapse_and_coarse_ground()
    {
        ForestFrame at55 = _forest.Capture(55f);
        ForestFrame at64 = _forest.Capture(64f);
        ForestFrame at72 = _forest.Capture(72f);
        ForestFrame at176 = _forest.Capture(176f);
        ForestFrame at192 = _forest.Capture(192f);
        ForestFrame at208 = _forest.Capture(208f);
        ForestFrame at544 = _forest.Capture(544f);
        ForestFrame at576 = _forest.Capture(576f);
        ForestFrame shadowBaseline = _forest.CaptureGameplayBaseline(576f);

        foreach (ForestFrame frame in new[] { at55, at64, at72, at176, at192, at208, at544, at576 })
            _output.WriteLine(frame.ToString());

        ForestFrame[] visible = { at55, at64, at72, at176, at192, at208, at544 };
        Assert.All(visible, frame => Assert.True(frame.PropPixels > 200,
            $"{frame.Distance} m has only {frame.PropPixels} visible prop pixels"));

        Assert.True(at55.RedPixels > 300 && at55.BluePixels == 0,
            $"55 m must be LOD0 only, red={at55.RedPixels}, blue={at55.BluePixels}");
        Assert.True(at64.RedPixels > 100 && at64.BluePixels > 100,
            $"64 m must share LOD0 and LOD1, red={at64.RedPixels}, blue={at64.BluePixels}");
        Assert.True(at72.BluePixels > 300 && at72.RedPixels == 0,
            $"72 m must be LOD1 only, red={at72.RedPixels}, blue={at72.BluePixels}");
        Assert.True(at176.BluePixels > 300 && at176.HlodPixels == 0,
            $"176 m must be individual LOD1 only, blue={at176.BluePixels}, hlod={at176.HlodPixels}");
        Assert.True(at192.HlodPixels > 100,
            $"192 m must visibly carry HLOD, hlod={at192.HlodPixels}");
        Assert.True(at208.HlodPixels > 300 && at208.BluePixels == 0,
            $"208 m must be HLOD only, blue={at208.BluePixels}, hlod={at208.HlodPixels}");
        Assert.True(at544.HlodPixels > 300, "544 m must retain the HLOD at the exit band's near edge");
        Assert.Equal(0, at576.PropPixels);

        Assert.True(at64.Stats.Instances > at55.Stats.Instances,
            "the LOD midpoint must submit both complementary individual tiers");
        Assert.True(at192.Stats.Instances > at208.Stats.Instances * 5,
            "the HLOD midpoint must still carry the individual forest beside the merged draw");
        Assert.True(at208.Stats.Instances * 5 < at176.Stats.Instances,
            "the completed HLOD handoff must collapse the individual forest");
        Assert.Equal(at208.Stats.Instances, at544.Stats.Instances);
        Assert.True(at576.Stats.Instances < at544.Stats.Instances,
            "the exact draw radius must remove the final merged prop draw");

        Assert.True(at544.GroundPixels > 500,
            $"coarse decor ground has only {at544.GroundPixels} visible pixels");
        Assert.True(at544.Stats.Triangles < at208.Stats.Triangles,
            $"coarse ground did not reduce triangles: full={at208.Stats.Triangles}, coarse={at544.Stats.Triangles}");

        int shadowFloor = Math.Min(at55.ShadowTexels, Math.Min(at72.ShadowTexels, at208.ShadowTexels));
        Assert.True(shadowFloor > 500, $"solid boundary shadow floor is only {shadowFloor} texels");
        Assert.True(at64.ShadowTexels >= shadowFloor * 0.9f,
            $"LOD midpoint shadow {at64.ShadowTexels} fell below the {shadowFloor} solid floor");
        Assert.True(at192.ShadowTexels >= shadowFloor * 0.9f,
            $"HLOD midpoint shadow {at192.ShadowTexels} fell below the {shadowFloor} solid floor");
        Assert.NotEqual(shadowBaseline.ShadowHash, at55.ShadowHash);
        Assert.Equal(at55.ShadowHash, at64.ShadowHash);
        Assert.Equal(at64.ShadowHash, at72.ShadowHash);
        Assert.Equal(at176.ShadowHash, at192.ShadowHash);
        Assert.Equal(at192.ShadowHash, at208.ShadowHash);

    }

    [GpuFact]
    public void Aged_shared_scene_matches_a_fresh_scene_at_lod0()
    {
        _forest.Capture(208f);
        _forest.Capture(544f);
        byte[] aged = _forest.Capture(55f).Pixels;
        byte[] fresh = TileWorldPropHlodGpuScene.CaptureFresh(55f).Pixels;

        Assert.Equal(fresh, aged);
    }
}

public readonly record struct ForestFrame(
    float Distance,
    byte[] Pixels,
    RenderFrameStats Stats,
    int RedPixels,
    int BluePixels,
    int HlodPixels,
    int GroundPixels,
    int ShadowTexels,
    ulong ShadowHash)
{
    public int PropPixels => RedPixels + BluePixels + HlodPixels;

    public override string ToString() =>
        $"{Distance:F0} m: instances={Stats.Instances}, triangles={Stats.Triangles}, props={PropPixels}, " +
        $"ground={GroundPixels}, shadows={ShadowTexels}";
}

/// <summary>One lazily created TileWorld scene reused for the boundary captures.</summary>
public sealed class TileWorldPropHlodGpuScene : IDisposable
{
    const int Width = 256;
    const int Height = 192;
    static readonly Vector3 Centre = new(32f, 0f, -32f);

    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    TileWorldView? _view;

    public ForestFrame Capture(float distance)
    {
        EnsureScene();
        return Capture(_preview!, _view!, distance);
    }

    public ForestFrame CaptureGameplayBaseline(float distance)
    {
        EnsureScene();
        return Capture(_preview!, _view!, distance, TileRegionResidencyState.Gameplay);
    }

    public static ForestFrame CaptureFresh(float distance)
    {
        using GpuDeviceContext context = GpuDeviceContext.CreateHeadless();
        using var preview = CreatePreview(context.GpuDevice);
        using TileWorldView view = CreateView(preview.Scene);
        return Capture(preview, view, distance);
    }

    void EnsureScene()
    {
        if (_preview is not null) return;
        _context = GpuDeviceContext.CreateHeadless();
        _preview = CreatePreview(_context.GpuDevice);
        _view = CreateView(_preview.Scene);
    }

    static Render3DPreview CreatePreview(IGpuDevice device)
    {
        var preview = new Render3DPreview(device, Width, Height, new ShadowSettings
        {
            Mode = ShadowMode.ShadowMap,
            ShadowCascadeCount = 4,
            ShadowNearDistance = 16f,
            ShadowMaxDistance = 250f,
        });
        Scene3D scene = preview.Scene;
        scene.FrustumCulling = false;
        scene.Post.TransparentBackground = false;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.BackgroundColor = Color.Black;
        scene.Post.AmbientColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.3f, -0.9f, 0.25f));
        scene.Camera.Frame(Centre + new Vector3(48f, 34f, 72f), Centre + new Vector3(0f, 1f, 0f));
        return preview;
    }

    static TileWorldView CreateView(Scene3D scene)
    {
        TileWorldDocument document = ForestWorld();
        var options = new TileWorldViewOptions
        {
            PropLayers = new[]
            {
                new TilePropLayerDefinition
                {
                    Id = "trees",
                    ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree" },
                    DrawRadius = 576f,
                    LodDistance = 64f,
                    LodCrossfadeWidth = 16f,
                    HlodDistance = 192f,
                    HlodCrossfadeWidth = 32f,
                    HlodWeldCell = 1.5f,
                },
            },
        };
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        TileObjectArchetype tree = catalogs.Archetype("tree")!;
        tree.SizeX = 2;
        tree.SizeZ = 2;
        var view = new TileWorldView(
            new Scene3DTileWorldScene(scene), document, catalogs, new ForestMeshResolver(),
            options, new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 1,
                MaxFullGroundAppliesPerPump = 1,
                MaxCoarseGroundAppliesPerPump = 1,
                MaxHlodAppliesPerPump = 1,
            }, new ImmediateDispatcher());
        view.LoadRegion(default);
        return view;
    }

    static TileWorldDocument ForestWorld()
    {
        var document = new TileWorldDocument
        {
            Id = "gpu-forest",
            DisplayName = "GPU forest",
            PlaneCount = 1,
        };
        document.GetOrCreateRegion(default);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                document.SetUnderlay(x, z, 0, 1);
        for (int i = 0; i < 25; i++) document.AddObject("tree", 31, 31, 0, i & 3);
        return document;
    }

    static ForestFrame Capture(Render3DPreview preview, TileWorldView view, float distance,
                               TileRegionResidencyState? forcedResidency = null)
    {
        view.LoadRegion(default, forcedResidency ?? (distance >= 544f
            ? TileRegionResidencyState.Decor
            : TileRegionResidencyState.Gameplay));
        preview.Capture(_ => view.Draw(Centre + new Vector3(distance, 0f, 0f)));
        byte[] pixels = preview.ReadbackRgba();
        float[] shadow = preview.Scene.DebugReadShadowMap(out _, out _);
        return new ForestFrame(
            distance,
            pixels,
            preview.Scene.LastFrameStats,
            Count(pixels, static (r, g, b) => r > 40 && r > g * 1.6f && r > b * 1.6f),
            Count(pixels, static (r, g, b) => b > 40 && b > r * 1.6f && b > g * 1.4f),
            Count(pixels, static (r, g, b) => r > 40 && b > 40 && r > g * 1.4f && b > g * 1.4f),
            Count(pixels, static (r, g, b) => g > 40 && g > r * 1.25f && g > b * 1.25f),
            CountShadow(shadow),
            HashShadow(shadow));
    }

    static int Count(byte[] pixels, Func<int, int, int, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (predicate(pixels[i], pixels[i + 1], pixels[i + 2])) count++;
        return count;
    }

    static int CountShadow(float[] depth)
    {
        int count = 0;
        for (int i = 0; i < depth.Length; i++) if (depth[i] < 0.999f) count++;
        return count;
    }

    static ulong HashShadow(float[] depth)
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        ulong hash = Offset;
        for (int i = 0; i < depth.Length; i++)
        {
            hash ^= (uint)BitConverter.SingleToInt32Bits(depth[i]);
            hash *= Prime;
        }
        return hash;
    }

    public void Dispose()
    {
        _view?.Dispose();
        _preview?.Dispose();
        _context?.Dispose();
    }

    sealed class ForestMeshResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GltfMesh _full = Colored(MeshPrimitives.Box(3f), new Vector4(1f, 0.05f, 0.05f, 1f));
        readonly GltfMesh _lod = Colored(MeshPrimitives.Box(3f), new Vector4(0.05f, 0.1f, 1f, 1f));
        readonly GltfMesh _hlod = Colored(MeshPrimitives.Box(3f), new Vector4(1f, 0.05f, 1f, 1f));

        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_full, default) };
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_lod, default) };
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _hlod;
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }

    static GltfMesh Colored(GltfMesh source, Vector4 color)
    {
        var vertices = new ModelVertex[source.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = source.Vertices[i];
            vertices[i].Color = color;
        }
        return new GltfMesh(vertices, source.Indices32);
    }
}
