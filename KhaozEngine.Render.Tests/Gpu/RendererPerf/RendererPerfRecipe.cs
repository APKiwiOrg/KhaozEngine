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
using System.Text.Json.Serialization;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfRecipeFacts(string Id, string Version, string Profile, int TerrainChunks,
    int BuildingComponents, int PropInstances, int ActorInstances, string GeometrySha256, string SettingsSha256,
    string AssetSha256, string SequenceSha256, string RecipeSha256, RendererPerfRecipeSchedule Schedule);
internal sealed record RendererPerfRecipeSchedule(int DirectionalCascades, string DirectionalCache,
    int StaticPointRows, int DynamicPointRows, int StaticRebuildBudget, int WarmStaticRebuilds,
    int DynamicUpdates, int TransientRows, int FacesPerRow, bool Temporal, bool Atmosphere, bool Bloom);
internal sealed record RendererPerfRecipeFrame(int Index, float TimeSeconds, Vector3 Target, float Azimuth,
    Vector3 LightDirection, Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 ViewProjection, string PoseSha256);

internal interface IRendererPerfRecipe : IDisposable
{
    void Queue(Scene3D scene, int frameIndex);
    RendererPerfRecipeFacts Facts { get; }
}

/// <summary>Cold public construction and indexed inputs. Scene3D owns all uploaded resources.</summary>
internal abstract class RendererPerfRecipe : IRendererPerfRecipe
{
    internal const int Width = 1920, Height = 1080, Period = 256;
    internal const float Dt = 1f / 60f;
    internal const uint Seed = 0x51AB_C0DE;
    internal static readonly ImmutableArray<int> WarmupIndices = [.. Enumerable.Range(64, 192)];
    internal static readonly ImmutableArray<int> MeasuredIndices = [.. Enumerable.Range(0, Period)];
    internal static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };
    readonly HashSet<Scene3D> _usedScenes = [];
    readonly string _id;
    byte[] _description = [];
    bool _disposed;
    Scene3D? _scene;
    RendererPerfRecipeFrame[] _frames = [];
    protected RendererPerfRecipe(string id) => _id = id;
    public RendererPerfRecipeFacts Facts { get; private set; } = null!;
    internal virtual IReadOnlyList<GltfMesh> Terrain => Array.Empty<GltfMesh>();
    protected virtual int Buildings => 0;
    protected virtual int Props => 0;
    protected virtual int Actors => 0;
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
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
        recipe.Freeze();
        if (verifyFrozen && !File.ReadAllBytes(RecipePath(id)).AsSpan().SequenceEqual(recipe._description))
            throw new InvalidOperationException($"The {id}/1 construction differs from its frozen recipe.");
        return recipe;
    }

    internal static string RecipePath(string id, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "recipes", id + ".json");
    internal byte[] Describe() => (byte[])_description.Clone();
    internal RendererPerfRecipeFrame Frame(int frameIndex) => _frames[SequenceIndex(frameIndex)];
    internal static int SequenceIndex(int frameIndex) => ((frameIndex % Period) + Period) % Period;
    internal static float Phase(int frameIndex)
    {
        int index = SequenceIndex(frameIndex);
        return (index <= 128 ? index : Period - index) / 128f;
    }

    void Freeze()
    {
        PixelPostProcessSettings post = Settings();
        _frames = Enumerable.Range(0, Period).Select(index =>
        {
            float phase = _id == "empty" ? 0f : Phase(index);
            IsoCamera3D camera = Camera();
            camera.Target = new Vector3(phase * .25f, 0f, phase * .125f);
            camera.Azimuth += phase * .01f;
            Vector3 sun = post.LightDirection + new Vector3(phase * .02f, 0f, 0f);
            return new RendererPerfRecipeFrame(index, index * Dt, camera.Target, camera.Azimuth, sun,
                camera.View, camera.Projection, camera.ViewProjection, PoseDigest(index));
        }).ToArray();
        string geometry = Digest(Geometry), settings = Digest(post), assets = Digest(Assets), sequence = Digest(_frames);
        var schedule = new RendererPerfRecipeSchedule(_id == "empty" ? 0 : 4,
            Actors > 0 ? "recomputed-current-pose" : "reuse-identical-camera-and-sun",
            Actors > 0 ? 1 : 0, Actors > 0 ? 1 : 0, Actors > 0 ? 1 : 0, 0,
            Actors > 0 ? 1 : 0, Actors > 0 ? 1 : 0, 6, _id != "empty", _id != "empty", _id != "empty");
        _description = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "khaozengine.renderer-perf-recipe/1",
            Id = _id,
            Version = "1",
            Profile = _id == "empty" ? "renderer-empty-1080/1" : "renderer-full-native-1080/1",
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
            GpuSkinning = true,
            FrustumCulling = true,
            TerrainCastsShadows = true,
            Coverage = false,
            DebugView = SceneDebugView.None,
            Clear = new Color(.02f, .03f, .06f, 1f),
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
                Foliage = 0,
                Water = 0,
                Effects = 0,
                Ui = 0
            },
            InitialClear = 1,
            FinalBlit = 1,
            Schedule = schedule,
        }, Json);
        Facts = new RendererPerfRecipeFacts(_id, "1", _id == "empty" ? "renderer-empty-1080/1" : "renderer-full-native-1080/1",
            Terrain.Count, Buildings, Props, Actors, geometry, settings, assets, sequence, Hash(_description), schedule);
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
        PixelPostProcessSettings settings = Settings();
        // The atlas commits this exact settings object in the Scene3D constructor. Keep that identity
        // while copying its public runtime fields, then request its construction-only layout separately.
        ShadowSettings liveShadows = scene.Post.Quality.Shadows;
        foreach (FieldInfo field in typeof(ShadowSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(liveShadows, field.GetValue(settings.Quality.Shadows));
        settings.Quality.Shadows = liveShadows;
        foreach (FieldInfo field in typeof(PixelPostProcessSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(scene.Post, field.GetValue(settings));
        scene.RequestShadowMapLayout(2048, 4);
        scene.FrustumCulling = true;
        scene.UseGpuSkinning = true;
        scene.TerrainCastsShadows = true;
        scene.DebugView = SceneDebugView.None;
        scene.RenderOrigin = Vector3.Zero;
        scene.CameraOverride = null;
        IsoCamera3D camera = Camera();
        foreach (FieldInfo field in typeof(IsoCamera3D).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(scene.Camera, field.GetValue(camera));
        scene.Camera.RenderOrigin = Vector3.Zero;
        Load(scene);
    }

    public void Queue(Scene3D scene, int frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(scene, _scene)) throw new InvalidOperationException("Reset the block before queuing it.");
        RendererPerfRecipeFrame frame = Frame(frameIndex);
        scene.Camera.Target = frame.Target;
        scene.Camera.Azimuth = frame.Azimuth;
        scene.Post.LightDirection = frame.LightDirection;
        scene.EffectTimeSeconds = frame.TimeSeconds;
        QueueContent(scene, frame.Index);
    }

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
    public void Dispose() { _scene = null; _usedScenes.Clear(); _disposed = true; }
    sealed class EmptyRecipe() : RendererPerfRecipe("empty");
}
