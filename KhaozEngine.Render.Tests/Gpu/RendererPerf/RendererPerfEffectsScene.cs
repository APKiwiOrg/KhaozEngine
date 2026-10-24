using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The public transparent, target-outline, temporal post and display UI feature mix.</summary>
internal sealed class RendererPerfEffectsScene : RendererPerfVillageScene
{
    readonly SkinnedGltfMesh _tube = RendererPerfTubeInputs.Character.Build();
    readonly GltfMesh _overlay = MeshPrimitives.Box(1f);
    readonly SkinnedInstanceDraw[] _actors;
    readonly Matrix4x4[][] _poses;
    readonly float[] _poseAngles = Enumerable.Range(0, Period).Select(frame => .1f + Phase(frame) * .25f).ToArray();
    readonly string[] _poseDigests;
    readonly GroundDecal[] _decals;
    readonly ParticleSprite[] _particles;
    readonly DistortionSprite[] _distortions;
    readonly BillboardDraw[] _billboards;
    readonly BeamDraw[] _beams;
    readonly TrailSample[][] _trails;
    readonly Matrix4x4[] _overlays, _silhouettes;
    readonly RendererPerfTexture[] _images = Enumerable.Range(0, 4)
        .Select(i => new RendererPerfTexture(8, 8, RendererPerfUiScene.ColorPixels(i), TextureMipPolicy.Full)).ToArray();
    readonly RendererPerfPoint[] _points =
    [
        new(new(-5f, 3f, 0f), Color.White, 32f, 3f, default),
        new(new(5f, 3f, 0f), Color.White, 32f, 3f, default),
    ];
    readonly ScenePolicy _scenePolicy = new(ParticleQuality.Full, GroundDecalQuality.Full, DistortionQuality.Full, .35f);
    readonly OutlinePolicy _outline = new(new Color(1f, .7f, .2f, .9f), 3f, MeshOutlineOcclusion.SceneDepth);
    readonly SilhouettePolicy _silhouette = new(new Color(.3f, .8f, 1f, .7f), .08f, false);
    readonly TrailStyle _trailStyle = new() { Color = new Color(2f, .7f, .2f, .8f), Blend = TrailBlend.Additive, SoftEdge = .5f };
    readonly BeamStyle _beamStyle = BeamStyle.Default with { PulseSpeed = 2f, PulseAmount = .2f, ScrollSpeed = 1f };
    SkinnedMeshHandle[] _handles = [];
    Scene3D.TextureHandle[] _textures = [];
    MeshHandle _overlayHandle;
    internal override RendererPerfUiScene UiScene { get; } = new();
    internal RendererPerfEffectsScene() : base("effects-ui", 4, 64, 0)
    {
        _actors = Enumerable.Range(0, 4).Select(i => RendererPerfCrowdScene.DefaultActorPolicy with
        {
            Mesh = new SkinnedMeshHandle(i),
            Model = Matrix4x4.CreateTranslation((i - 1.5f) * 5f, 1f, 0f),
            Motion = MotionKey.From((ulong)i + 1),
        }).ToArray();
        _poses = Enumerable.Range(0, Period).Select(i =>
        {
            Matrix4x4[] pose = SkinnedPointShadowScene.BentPose(_tube, _poseAngles[i]);
            for (int bone = 0; bone < pose.Length; bone++) pose[bone] = _tube.RestPose[bone] * pose[bone];
            return pose;
        }).ToArray();
        _poseDigests = _poses.Select((pose, i) => Digest(new { Palette = pose, UiOffset = UiScene.FrameOffset(i) })).ToArray();
        _decals = Enumerable.Range(0, 32).Select(i => new GroundDecal
        {
            Shape = DecalShape.Circle,
            Center = Position(i, 8, 3f, .01f),
            Size = new Vector4(1f, 0f, 0f, 0f),
            FillColor = new Color(1.5f, .3f, .1f, .4f),
            OutlineColor = Color.White,
            EdgeThickness = .05f,
            FillFraction = 1f,
            Blend = DecalBlend.Alpha,
            YTolerance = 1f,
            MaxStep = 1f,
            Pattern = DecalFillPattern.ScrollingNoise,
            PatternSpeed = 1f,
            PatternScale = 2f,
        }).ToArray();
        _particles = Enumerable.Range(0, 256).Select(i => new ParticleSprite
        {
            Position = Position(i, 16, 1.5f, 2f),
            Size = .45f,
            Color = new Color(2f, .5f, .1f, .6f),
            Shape = ParticleShape.SoftGlow,
            Blend = BillboardBlend.Alpha,
            Seed = i / 256f,
            SoftFadeScale = 1f,
        }).ToArray();
        _distortions = Enumerable.Range(0, 64).Select(i => new DistortionSprite
        {
            Position = Position(i, 8, 3f, 2f),
            Size = 1f,
            Shape = DistortionShape.Heat,
            Strength = .2f,
            ShapeParam = .4f,
            Seed = i / 64f,
            SoftFadeScale = 1f,
        }).ToArray();
        _billboards = Enumerable.Range(0, 64).Select(i => new BillboardDraw(i % 4, Position(i, 8, 3f, 3f),
            .6f, new Vector4(0f, 0f, 1f, 1f), new Color(1f, .7f, .4f, .65f), BillboardBlend.Alpha)).ToArray();
        _beams = Enumerable.Range(0, 8).Select(i => new BeamDraw(new Vector3(-10f, 2f, i * 2f - 7f),
            new Vector3(10f, 2f, i * 2f - 7f), .2f, new Color(2f, .4f, .1f, .7f), _beamStyle)).ToArray();
        _trails = Enumerable.Range(0, 8).Select(i => Enumerable.Range(0, 8).Select(j =>
            new TrailSample(new Vector3(i * 2f - 7f, 2f + j * .1f, j - 3.5f), .25f, (j + 1) / 8f)).ToArray()).ToArray();
        _overlays = Enumerable.Range(0, 8).Select(i => Matrix4x4.CreateTranslation(Position(i, 4, 4f, 2f))).ToArray();
        _silhouettes = _actors.Select(a => a.Model).ToArray();
        for (int i = 0; i < _overlay.Vertices.Length; i++) _overlay.Vertices[i].Color = new Vector4(.2f, .7f, 1f, .3f);
    }
    static Vector3 Position(int i, int columns, float spacing, float y) =>
        new((i % columns - (columns - 1) * .5f) * spacing, y, (i / columns - (columns - 1) * .5f) * spacing);
    protected override int Actors => 4;
    protected override int Effects => 32 + 256 + 64 + 8 + 8 + 8 + 4 + 4 + 64;
    protected override int Ui => 3072;
    protected override string PoseDigest(int frame) => _poseDigests[frame];
    protected override RendererPerfRecipeSchedule Schedule => base.Schedule with
    {
        StaticPointRows = 0,
        DynamicPointRows = 0,
        StaticRebuildBudget = 0,
        DynamicUpdates = 0,
        TransientRows = 0,
        Features = new()
        {
            Decals = 32,
            Particles = 256,
            TexturedBillboards = 64,
            Beams = 8,
            Trails = 8,
            Overlays = 8,
            Silhouettes = 4,
            OutlinedActors = 4,
            Distortions = 64,
            TemporalOutlineDraws = 1,
            UiSprites = 1024,
            UiGlyphs = 2048,
            UiTextures = 4,
            UiClips = 16
        },
    };
    protected override object Geometry => new
    {
        Rigid = base.Geometry,
        Actor = new
        {
            Inputs = RendererPerfTubeInputs.Character,
            Vertices = _tube.Vertices.Length,
            Indices = _tube.Indices32.Length,
            _tube.IndexFormat,
            VertexStride = SkinnedVertex.SizeInBytes,
            VertexBytes = _tube.Vertices.Length * SkinnedVertex.SizeInBytes,
            IndexBytes = _tube.Indices32.Length * 2,
            Bones = _tube.BoneCount,
            VertexSha256 = BufferDigest(_tube.Vertices),
            IndexSha256 = BufferDigest(_tube.Indices32),
            _tube.RestPose,
            _tube.InverseBind,
            PaletteWindowBones = 128,
            PaletteSlotBytes = 8192,
            SharedPaletteBytes = 4 * 8192,
        },
        ActorDraws = _actors,
        Palettes = _poses,
        PoseAngles = _poseAngles,
        Points = _points,
        Decals = _decals,
        Particles = _particles,
        Billboards = _billboards,
        Beams = _beams,
        Trails = _trails,
        TrailStyle = _trailStyle,
        OverlayMesh = MeshFacts(_overlay),
        Overlays = _overlays,
        Silhouettes = _silhouettes,
        Silhouette = _silhouette,
        Outline = _outline,
        Distortions = _distortions,
        Scene = _scenePolicy,
        Ui = UiScene.Construction,
        Routes = new
        {
            Decals = 32,
            Particles = 256,
            TexturedBillboards = 64,
            Beams = 8,
            Trails = 8,
            Overlays = 8,
            Silhouettes = 4,
            OutlinedActors = 4,
            Distortions = 64,
            Directional = "four-cascades-current-pose",
            Points = "two-unshadowed",
            Temporal = "native-history-after-first-frame",
            Outline = "temporal-edge-before-resolve",
            Distortion = "half-resolution-field-and-display-apply",
            Post = "bloom-bright-blur-composite-tonemap-sharpen-blit",
        },
    };
    protected override object Assets => new
    {
        Terrain = base.Assets,
        Billboards = _images,
        Ui = UiScene.Assets,
    };
    protected override void ConfigureSettings(PixelPostProcessSettings post)
    {
        post.Outline = true;
        post.Quality.Shadows.PointShadows.Enabled = false;
    }
    protected override void Load(Scene3D scene)
    {
        base.Load(scene);
        _handles = Enumerable.Range(0, 4).Select(_ => scene.LoadSkinnedMesh(_tube)).ToArray();
        _textures = _images.Select(image => image.Load(scene)).ToArray();
        _overlayHandle = scene.LoadMesh(_overlay);
        scene.ParticleQuality = _scenePolicy.Particles;
        scene.DecalQuality = _scenePolicy.Decals;
        scene.DistortionQuality = _scenePolicy.Distortion;
        scene.ParticleSoftFade = _scenePolicy.SoftFade;
    }
    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        base.QueueContent(scene, frameIndex);
        for (int i = 0; i < _actors.Length; i++)
        {
            scene.DrawSkinned(_actors[i] with { Mesh = _handles[i] }, _poses[frameIndex]);
            scene.DrawSkinnedOutline(_handles[i], _poses[frameIndex], _actors[i].Model, _outline.Color, _outline.Width, _outline.Occlusion);
        }
        foreach (RendererPerfPoint p in _points) scene.AddLight(p.Position, p.Color, p.Radius, p.Intensity, p.Shadow);
        foreach (GroundDecal decal in _decals) scene.DrawGroundDecal(decal);
        scene.DrawParticles(_particles);
        scene.DrawDistortions(_distortions);
        foreach (BillboardDraw b in _billboards) scene.DrawBillboard(_textures[b.Texture], b.Position, b.Size, b.SourceUv, b.Color, b.Blend);
        foreach (BeamDraw b in _beams) scene.DrawBeam(b.A, b.B, b.Width, b.Color, b.Style);
        foreach (TrailSample[] trail in _trails) scene.DrawTrail(trail, _trailStyle);
        foreach (Matrix4x4 world in _overlays) scene.DrawOverlayMesh(_overlayHandle, world);
        foreach (Matrix4x4 world in _silhouettes)
            scene.DrawMeshSilhouette(_overlayHandle, world, _silhouette.Color, _silhouette.Width, _silhouette.CutInsideInteriors);
    }
    public override void Dispose() { UiScene.Dispose(); base.Dispose(); }
    sealed record BillboardDraw(int Texture, Vector3 Position, float Size, Vector4 SourceUv, Color Color, BillboardBlend Blend);
    sealed record BeamDraw(Vector3 A, Vector3 B, float Width, Color Color, BeamStyle Style);
    sealed record ScenePolicy(ParticleQuality Particles, GroundDecalQuality Decals, DistortionQuality Distortion, float SoftFade);
    sealed record OutlinePolicy(Color Color, float Width, MeshOutlineOcclusion Occlusion);
    sealed record SilhouettePolicy(Color Color, float Width, bool CutInsideInteriors);
}
