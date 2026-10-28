using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfRecipeFacts(string Id, string Version, string Profile, int TerrainChunks,
    int BuildingComponents, int PropInstances, int ActorInstances, string GeometrySha256, string SettingsSha256,
    string AssetSha256, string SequenceSha256, string RecipeSha256, RendererPerfRecipeSchedule Schedule,
    string PlatformFamily, RendererPerfDerivedDigests Derived);
internal sealed record RendererPerfRecipeSchedule(int DirectionalCascades, string DirectionalCache,
    int StaticPointRows, int DynamicPointRows, int StaticRebuildBudget, int WarmStaticRebuilds,
    int DynamicUpdates, int TransientRows, int FacesPerRow, bool Temporal, bool Atmosphere, bool Bloom,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RendererPerfFeatureSchedule? Features = null);
internal sealed record RendererPerfFeatureSchedule
{
    public int FoliagePatches { get; init; }
    public int FoliageInstances { get; init; }
    public int WaterPlanes { get; init; }
    public int OceanDispatches { get; init; }
    public int OceanMipCopies { get; init; }
    public string? OceanPrime { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RendererPerfClipmapSchedule? Clipmap { get; init; }
    public int Decals { get; init; }
    public int Particles { get; init; }
    public int TexturedBillboards { get; init; }
    public int Beams { get; init; }
    public int Trails { get; init; }
    public int Overlays { get; init; }
    public int Silhouettes { get; init; }
    public int OutlinedActors { get; init; }
    public int Distortions { get; init; }
    public int TemporalOutlineDraws { get; init; }
    public int UiSprites { get; init; }
    public int UiGlyphs { get; init; }
    public int UiTextures { get; init; }
    public int UiClips { get; init; }
}
internal sealed record RendererPerfClipmapEvent(int Index, int Plane0Rebuilds, int Plane1Rebuilds);
internal sealed record RendererPerfClipmapSchedule(RendererPerfClipmapEvent Outward, RendererPerfClipmapEvent Return,
    int ColdRebuilds, int WarmupRebuilds, int MeasuredRebuilds);
internal sealed record RendererPerfPoint(Vector3 Position, Color Color, float Radius, float Intensity, LightShadow Shadow);
internal sealed record RendererPerfRecipeFrame(int Index, Vector3 Target, float Azimuth,
    Vector3 LightDirection, Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 ViewProjection, string PoseSha256);
internal sealed record RendererPerfBlockFrame(int Ordinal, int SequenceIndex, float EffectTimeSeconds);
internal sealed record RendererPerfScenePolicy(bool GpuSkinning, bool FrustumCulling, bool TerrainCastsShadows,
    SceneDebugView DebugView, Vector3 RenderOrigin);

internal interface IRendererPerfRecipe : IDisposable
{
    void Queue(Scene3D scene, int frameIndex);
    RendererPerfRecipeFacts Facts { get; }
}

/// <summary>Cold public construction and indexed inputs. Scene3D owns all uploaded resources.</summary>
internal abstract class RendererPerfRecipe : IRendererPerfRecipe
{
    internal ImmutableArray<RendererPassWorkModel> WorkModels(RendererPerfFrameSample sample)
        => RendererPerfWorkModels.ForFrame(sample, Facts.Id + "/" + Facts.Version);

    internal const int Width = 1920, Height = 1080, Period = 256;
    internal const float Dt = 1f / 60f;
    internal const uint Seed = 0x51AB_C0DE;
    static readonly RendererPerfScenePolicy ScenePolicy = new(true, true, true, SceneDebugView.None, Vector3.Zero);
    internal static readonly ImmutableArray<int> WarmupIndices = [.. Enumerable.Range(64, 192)];
    internal static readonly ImmutableArray<int> MeasuredIndices = [.. Enumerable.Range(0, Period)];
    static readonly ImmutableArray<RendererPerfBlockFrame> BlockFrames = [.. Enumerable.Range(64, 192 + Period)
        .Select(ordinal => new RendererPerfBlockFrame(ordinal, SequenceIndex(ordinal), ordinal * Dt))];
    internal static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        TypeInfoResolver = RendererPerfRecipeSettings.Resolver(),
        Converters = { new JsonStringEnumConverter() },
    };
    readonly HashSet<Scene3D> _usedScenes = [];
    readonly string _id;
    byte[] _description = [];
    bool _disposed;
    Scene3D? _scene;
    int _blockOrdinal;
    RendererPerfRecipeFrame[] _frames = [];
    protected RendererPerfRecipe(string id) => _id = id;
    public RendererPerfRecipeFacts Facts { get; private set; } = null!;
    internal virtual RendererPerfUiScene? UiScene => null;
    internal virtual IReadOnlyList<GltfMesh> Terrain => Array.Empty<GltfMesh>();
    protected virtual int Buildings => 0;
    protected virtual int Props => 0;
    protected virtual int Actors => 0;
    protected virtual int Foliage => 0;
    protected virtual int Water => 0;
    protected virtual int Effects => 0;
    protected virtual int Ui => 0;
    protected virtual object Geometry => Array.Empty<object>();
    protected virtual object Assets => Array.Empty<object>();
    protected virtual string PoseDigest(int frame) => Hash(Array.Empty<byte>());
    protected virtual float WorldExtent => 0f;

    internal static RendererPerfRecipe Create(string id, bool verifyFrozen = true)
    {
        RendererPerfRecipe recipe = id switch
        {
            "empty" => new EmptyRecipe(),
            "village" => new RendererPerfVillageScene(),
            "crowd" => new RendererPerfCrowdScene(),
            "outdoors" => new RendererPerfOutdoorsScene(),
            "effects-ui" => new RendererPerfEffectsScene(),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
        recipe.Freeze();
        try
        {
            if (verifyFrozen) recipe.VerifyFrozen();
        }
        catch
        {
            recipe.Dispose();
            throw;
        }
        return recipe;
    }

    // A recipe version is "<id>/<revision>" and the id itself may contain '/'.
    internal static string IdFromVersion(string version)
    {
        int slash = version.LastIndexOf('/');
        RendererPerfManifest.Require(slash > 0 && slash < version.Length - 1, "Identity", $"Recipe version '{version}' has no id and revision.");
        return version[..slash];
    }

    internal static string RecipePath(string id, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "recipes", id + ".json");
    internal byte[] Describe() => (byte[])_description.Clone();
    internal void VerifyFrozen()
    {
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(_description);
        RendererPerfFrozenRecipe.Verify(JsonNode.Parse(File.ReadAllBytes(RecipePath(_id)))!.AsObject(), authored,
            derived, RendererPerfFrozenRecipe.PlatformFamily);
    }
    internal RendererPerfRecipeFrame Frame(int frameIndex) => _frames[SequenceIndex(frameIndex)];
    internal static int SequenceIndex(int frameIndex) => ((frameIndex % Period) + Period) % Period;
    internal static float Phase(int frameIndex)
    {
        int index = SequenceIndex(frameIndex);
        return (index <= 128 ? index : Period - index) / 128f;
    }

    internal void Freeze()
    {
        PixelPostProcessSettings post = Settings();
        _frames = Enumerable.Range(0, Period).Select(index =>
        {
            float phase = _id == "empty" ? 0f : Phase(index);
            IsoCamera3D camera = Camera();
            camera.Target = new Vector3(phase * .25f, 0f, phase * .125f);
            camera.Azimuth += phase * .01f;
            Vector3 sun = post.LightDirection + new Vector3(phase * .02f, 0f, 0f);
            return new RendererPerfRecipeFrame(index, camera.Target, camera.Azimuth, sun,
                camera.View, camera.Projection, camera.ViewProjection, PoseDigest(index));
        }).ToArray();
        string geometry = Digest(Geometry), settings = Digest(post), assets = Digest(Assets);
        string sequence = Digest(new { Frames = _frames, BlockFrames });
        string profile = _id == "empty" ? "empty-native-1080/1" : "renderer-full-native-1080/1";
        RendererPerfRecipeSchedule schedule = Schedule;
        _description = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "khaozengine.renderer-perf-recipe/1",
            Id = _id,
            Version = "1",
            Profile = profile,
            Provenance = "public-engine-primitives/1",
            ExternalAssets = Array.Empty<string>(),
            Seed,
            Display = new { Width, Height, Samples = 1, Format = "R8G8B8A8UNorm" },
            Targets = new
            {
                InternalWidth = Width,
                InternalHeight = Height,
                Color = _id == "empty" ? "R8G8B8A8UNorm" : "R16G16B16A16Float",
                Normal = "R8G8B8A8UNorm",
                Depth = "R32Float",
                DepthStencil = "D32FloatS8UInt",
                Motion = "R16G16Float",
                Shadow = "R32Float",
                ShadowDepth = "D32FloatS8UInt"
            },
            Settings = post,
            Camera = Camera(),
            ScenePolicy.GpuSkinning,
            ScenePolicy.FrustumCulling,
            ScenePolicy.TerrainCastsShadows,
            Coverage = false,
            ScenePolicy.DebugView,
            ScenePolicy.RenderOrigin,
            Clear = post.BackgroundColor,
            Geometry,
            Assets,
            GeometrySha256 = geometry,
            SettingsSha256 = settings,
            AssetSha256 = assets,
            SequenceSha256 = sequence,
            Dt,
            Period,
            WarmupIndices,
            MeasuredIndices,
            Reset = "fresh-scene-before-warmup",
            Frames = _frames,
            BlockFrames,
            Expected = new
            {
                TerrainIntended = Terrain.Count,
                TerrainSubmitted = Terrain.Count,
                TerrainVisible = Terrain.Count,
                TerrainCulled = 0,
                BuildingComponents = Buildings,
                PropInstances = Props,
                ActorIntended = Actors,
                ActorVisible = Actors,
                ActorCulled = 0,
                Foliage,
                Water,
                Effects,
                Ui
            },
            InitialClear = 1,
            FinalBlit = 1,
            Schedule = schedule,
        }, Json);
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(_description);
        Facts = new RendererPerfRecipeFacts(_id, "1", profile,
            Terrain.Count, Buildings, Props, Actors, Digest(authored["Geometry"]), settings, assets, sequence,
            Digest(authored), schedule, RendererPerfFrozenRecipe.PlatformFamily, derived);
    }

    // Reset all renderer-owned history, jitter, atmosphere and shadow caches by admitting only a fresh scene.
    // The native session constructs it outside measured spans, then explicitly starts at warm-up index 64.
    internal void ResetBlock(Scene3D scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        if (_usedScenes.Contains(scene) || scene.CurrentFrameView.FrameIndex != 0)
            throw new InvalidOperationException("A recipe block requires a fresh Scene3D.");
        _usedScenes.Add(scene);
        _scene = scene;
        _blockOrdinal = WarmupIndices[0];
        PixelPostProcessSettings settings = Settings();
        if (Digest(settings) != Facts.SettingsSha256)
            throw new InvalidOperationException("Resolved settings differ from the frozen construction.");
        int shadowResolution = settings.Quality.Shadows.ShadowMapResolution;
        int shadowCascades = settings.Quality.Shadows.ShadowCascadeCount;
        // The atlas commits this exact settings object in the Scene3D constructor. Keep that identity
        // while copying its public runtime fields, then request its construction-only layout separately.
        ShadowSettings liveShadows = scene.Post.Quality.Shadows;
        foreach (FieldInfo field in typeof(ShadowSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(liveShadows, field.GetValue(settings.Quality.Shadows));
        settings.Quality.Shadows = liveShadows;
        foreach (FieldInfo field in typeof(PixelPostProcessSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(scene.Post, field.GetValue(settings));
        scene.RequestShadowMapLayout(shadowResolution, shadowCascades);
        scene.FrustumCulling = ScenePolicy.FrustumCulling;
        scene.UseGpuSkinning = ScenePolicy.GpuSkinning;
        scene.TerrainCastsShadows = ScenePolicy.TerrainCastsShadows;
        scene.DebugView = ScenePolicy.DebugView;
        scene.RenderOrigin = ScenePolicy.RenderOrigin;
        scene.CameraOverride = null;
        IsoCamera3D camera = Camera();
        foreach (FieldInfo field in typeof(IsoCamera3D).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(scene.Camera, field.GetValue(camera));
        scene.Camera.RenderOrigin = camera.RenderOrigin;
        Load(scene);
    }

    internal virtual void PrepareCold(Scene3D scene, IGpuDevice device, int firstFrameIndex) { }

    internal void StartBlockClock(int firstFrameIndex) => _blockOrdinal = SequenceIndex(firstFrameIndex);

    public void Queue(Scene3D scene, int frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(scene, _scene)) throw new InvalidOperationException("Reset the block before queuing it.");
        RendererPerfRecipeFrame frame = Frame(frameIndex);
        scene.Camera.Target = frame.Target;
        scene.Camera.Azimuth = frame.Azimuth;
        scene.Post.LightDirection = frame.LightDirection;
        // Simulation advances through the block while camera, sun and pose inputs repeat every period.
        scene.EffectTimeSeconds = _blockOrdinal++ * Dt;
        QueueContent(scene, frame.Index);
    }

    protected virtual RendererPerfRecipeSchedule Schedule => new RendererPerfRecipeSchedule(_id == "empty" ? 0 : 4,
            Actors > 0 ? "recomputed-current-pose" : "reuse-identical-camera-and-sun",
            Actors > 0 ? 1 : 0, Actors > 0 ? 1 : 0, Actors > 0 ? 1 : 0, 0,
            Actors > 0 ? 1 : 0, Actors > 0 ? 1 : 0, 6, _id != "empty", _id != "empty", _id != "empty");

    protected virtual void ConfigureSettings(PixelPostProcessSettings post) { }
    protected virtual void Load(Scene3D scene) { }
    protected virtual void QueueContent(Scene3D scene, int frameIndex) { }
    protected virtual IsoCamera3D Camera() => new()
    {
        AspectRatio = (float)Width / Height,
        Azimuth = .6f,
        Elevation = .65f,
        Target = Vector3.Zero,
        OrthoSize = WorldExtent * 1.7f + 10f,
        Zoom = 1f,
        Distance = 150f,
        NearPlane = .1f,
        FarPlane = 500f,
        RenderOrigin = Vector3.Zero,
    };

    protected PixelPostProcessSettings Settings()
    {
        bool active = _id != "empty";
        var post = new PixelPostProcessSettings
        {
            RenderScale = RenderScale.FixedInternal,
            RenderWidth = Width,
            RenderHeight = Height,
            MaxRenderWidth = Width,
            MaxRenderHeight = Height,
            Supersample = 1f,
            Pixelated = false,
            MipFilterFixedInternalDownscale = false,
            Quantize = false,
            Dither = false,
            Outline = false,
            Starfield = false,
            CelBands = 0,
            TransparentBackground = false,
            BackgroundColor = new Color(.02f, .03f, .06f, 1f),
            LightColor = active ? new Color(1f, .95f, .86f, 1f) : Color.Black,
            FillLightColor = Color.Black,
            AmbientColor = active ? new Color(.16f, .19f, .3f, 1f) : Color.Black,
            IndirectDiffuse = IndirectDiffuseMode.Uniform,
            AmbientIrradiance = IrradianceSH.Zero,
            LightDirection = new Vector3(-.5f, -.85f, -.35f),
        };
        post.Quality.AntiAliasing = active ? AntiAliasing.Temporal : AntiAliasing.Off;
        post.Quality.Shadows = StreamedWorldSceneContent.Shadows();
        post.Quality.Shadows.Mode = active ? ShadowMode.ShadowMap : ShadowMode.Off;
        post.Quality.Shadows.ShadowMaxDistance = 400f;
        post.Quality.Shadows.PointShadows = new PointShadowSettings
        {
            Enabled = Actors > 0,
            FaceResolution = 256,
            MaxShadowedLights = 2,
            MaxStaticRebuildsPerFrame = 1,
            MaxDynamicLightsPerFrame = 1,
            Bias = .01f,
            SlopeBias = .02f,
            Filter = PointShadowFilter.Soft,
            LightSizeMetres = .15f,
            MaxPenumbraTexels = 6f,
        };
        post.Temporal.Upscale = TemporalUpscale.Native;
        post.Hdr.Enabled = active;
        post.Bloom.Enabled = active;
        post.Atmosphere.Enabled = active;
        post.Sky.Enabled = false;
        ConfigureSettings(post);
        return post;
    }

    internal static string Digest<T>(T value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string BufferDigest<T>(T[] values) where T : unmanaged
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Recipes use little-endian buffers.");
        return Hash(MemoryMarshal.AsBytes(values.AsSpan()));
    }
    internal static object MeshFacts(GltfMesh mesh) => new
    {
        Vertices = mesh.Vertices.Length,
        Indices = mesh.Indices32.Length,
        mesh.IndexFormat,
        VertexStride = ModelVertex.SizeInBytes,
        VertexBytes = mesh.Vertices.Length * ModelVertex.SizeInBytes,
        IndexBytes = mesh.Indices32.Length * (mesh.IndexFormat == GpuIndexFormat.UInt16 ? 2 : 4),
        VertexSha256 = BufferDigest(mesh.Vertices),
        IndexSha256 = BufferDigest(mesh.Indices32)
    };
    public virtual void Dispose() { _scene = null; _usedScenes.Clear(); _disposed = true; }
    sealed class EmptyRecipe() : RendererPerfRecipe("empty");
}
