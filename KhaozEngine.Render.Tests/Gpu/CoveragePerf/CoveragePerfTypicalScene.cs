using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal sealed record CoveragePerfEquipment(CoverageSourceTopology Source, GltfMesh[] Meshes)
{
    // Cold native import only. Fake hosts supply semantic equipment without importing GLB bytes.
    internal static CoveragePerfEquipment Import(CoverageCostGeometry geometry)
    {
        byte[] bytes = geometry.EquipmentGlb();
        return new(GltfLoader.LoadCoverageSource(bytes, "coverage-perf-equipment"),
            GltfLoader.LoadPartsWithMaterials(bytes, "coverage-perf-equipment").Select(p => p.Mesh).ToArray());
    }
}

/// <summary>One retained typical fixture. Construction, import and fingerprints are outside frame preparation.</summary>
internal sealed class CoveragePerfTypicalScene : IDisposable
{
    internal const int Width = 1280, Height = 720;
    internal CoverageCostGeometry Geometry { get; }
    internal Scene3D Scene { get; }
    internal CoveragePerfActor[] Actors { get; }
    internal string Fixture => CoveragePerfManifest.FixtureVersion;
    internal string Profile => CoveragePerfManifest.ProfileVersion;
    internal string FixtureFingerprint { get; }
    internal string ProfileFingerprint { get; }
    internal string SettingsJson { get; }
    internal CoverageCostTopology ConstructionTopology { get; }
    internal bool Covered { get; }
    int _pulledMain;

    internal CoveragePerfTypicalScene(IGpuDevice device, IGpuFramebuffer target, CoveragePerfBlock block,
        CoverageCostGeometry geometry, CoveragePerfEquipment equipment)
    {
        Geometry = geometry;
        Covered = block.Condition == CoveragePerfCondition.SharedOn;
        var shadows = new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowMapResolution = 1024, ShadowCascadeCount = 4 };
        Scene = new(device, target.Outputs, shadows)
        {
            UseGpuSkinning = true,
            FrustumCulling = true,
            EnableTiming = false,
            ForceTemporalForTests = true,
        };
        // Freeze the complete quality record once, including fields whose current defaults are retained.
        Scene.Post.Starfield = Scene.Post.Sky.Enabled = Scene.Post.Atmosphere.Enabled = false;
        Scene.Post.Hdr.Enabled = Scene.Post.Bloom.Enabled = false;
        Scene.Post.Outline = false;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Scene.Post.RenderScale = RenderScale.Temporal;
        Scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        Scene.Post.Temporal.UpscaleRatio = null;
        Scene.Post.LightDirection = new(.3f, -.4f, -1);
        Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        var points = Scene.Post.Quality.Shadows.PointShadows;
        points.Enabled = true; points.FaceResolution = 256; points.MaxShadowedLights = 1;
        points.MaxDynamicLightsPerFrame = 1; points.MaxStaticRebuildsPerFrame = 0;
        Scene.Camera.NearPlane = .1f;
        Scene.Camera.AspectRatio = Width / (float)Height;
        int columns = Math.Min(8, Math.Max(1, block.Actors)), rows = Math.Max(1, (block.Actors + columns - 1) / columns);
        Scene.Camera.Frame(Vector3.Zero, new((columns - 1) * 3 + 4, (rows - 1) * 1.5f + 3, 4), 1.2f);
        Actors = new CoveragePerfActor[block.Actors];
        try
        {
            for (int i = 0; i < Actors.Length; i++)
            {
                var world = Matrix4x4.CreateTranslation((i % columns - (columns - 1) / 2f) * 3,
                    (i / columns - (rows - 1) / 2f) * 1.5f, 0);
                Actors[i] = new(Scene, geometry, equipment, i, world);
            }
        }
        catch { Dispose(); throw; }
        ConstructionTopology = Actors.Length == 0 ? geometry.Topology
            : CoverageCostGeometry.Observe(Actors[0].Conformer.Topology, geometry.Commands.Length);
        Scene.CoveragePullObserverForTests = (pass, _) => { if (pass is "main" or "motion") _pulledMain++; };
        SettingsJson = JsonSerializer.Serialize(new
        {
            Profile,
            Width,
            Height,
            gpuSkinning = true,
            culling = true,
            temporal = true,
            camera = new
            {
                Scene.Camera.Azimuth,
                Scene.Camera.Elevation,
                Scene.Camera.NearPlane,
                Scene.Camera.FarPlane,
                Scene.Camera.Distance,
                Scene.Camera.OrthoSize,
                Scene.Camera.AspectRatio
            },
            post = Scene.Post,
        }, new JsonSerializerOptions { IncludeFields = true });
        ProfileFingerprint = Hash(SettingsJson);
        FixtureFingerprint = Hash(Fixture + ":" + ConstructionTopology.Fingerprint + ":" + equipment.Source.SourceSha256);
    }

    internal void Queue(int frameIndex)
    {
        _pulledMain = 0;
        foreach (var actor in Actors) actor.Queue(frameIndex, Covered);
        if (Actors.Length != 0)
        {
            // The light and its update budget are stable functions of the reset frame sequence.
            Scene.AddLight(Actors[0].World.Translation + new Vector3(0, 0, 2), Color.White, 3, 1, LightShadow.Dynamic);
        }
    }

    internal bool EffectiveRoutesValid => Scene.UseGpuSkinning && Scene.OrdinaryCpuSkinnedVerticesForTests == 0
        && (Covered ? Scene.DispatchedHostPreparationCount == Actors.Length && _pulledMain == Actors.Length * 2
            && Scene.UncoveredGpuDrawsForTests == 0
            : Scene.DispatchedHostPreparationCount == 0 && Scene.UncoveredGpuDrawsForTests == Actors.Length * 2);

    internal int MainVisibleActors()
    {
        if (Covered) return Actors.Count(a => Scene.CoverageMainVisibleForTests(a.Frame));
        // This is the production culled draw count, not a projection of intended actor positions.
        return Scene.DrawnSkinnedInstances / 2;
    }

    internal string PoseFingerprint() => Hash(string.Join(',', Actors.Select(a =>
        string.Create(CultureInfo.InvariantCulture, $"{a.Key.Value}:{a.PoseId}"))));

    internal (int Passes, int Faces, string Fingerprint) PassSchedule()
    {
        var shadows = Scene.LastShadowPassDiagnostics;
        int cascades = shadows.Rendered ? shadows.CascadeCount : 0;
        // The production point pass executes all six cells for each rendered row. No per-face skipping exists.
        int faces = (shadows.PointDynamicRenders + shadows.PointStaticRebuilds + shadows.PointTransientRowsRendered) * 6;
        int outlines = Scene.TargetOutlineGroupsRenderedForTests * 3;
        int resolve = Scene.ResolvedLastRenderForTests ? 1 : 0;
        return (cascades + faces + outlines + resolve + 1, faces,
            string.Create(CultureInfo.InvariantCulture, $"cascades={cascades},point-faces={(faces == 0 ? "none" : "0,1,2,3,4,5")},rows={faces / 6},outline={outlines},resolve={resolve},main=1"));
    }

    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose()
    {
        foreach (var actor in Actors) actor?.Dispose();
        Scene.Dispose();
    }
}

/// <summary>Distinct actor identity, commands, palette and history over shared fixture inputs.</summary>
internal sealed class CoveragePerfActor : IDisposable
{
    readonly Scene3D _scene;
    readonly SkinnedMeshHandle[] _hosts;
    readonly RigidInstanceDraw[] _equipment, _frozen;
    readonly CoverageEquipmentCommand[] _memberships;
    readonly int _index;
    internal SkinnedHostConformer Conformer { get; }
    internal CoverageCommandPose[] Commands { get; }
    internal Matrix4x4[] Palette { get; } = new Matrix4x4[2];
    internal MotionKey Key { get; }
    internal Matrix4x4 World { get; }
    internal int PoseId { get; private set; }
    internal SkinnedCoverageFrame Frame { get; private set; }
    internal bool Staged { get; private set; }
    internal int EquipmentDrawCount => _equipment.Length;

    internal CoveragePerfActor(Scene3D scene, CoverageCostGeometry geometry, CoveragePerfEquipment equipment,
        int index, Matrix4x4 world)
    {
        _scene = scene; _index = index; World = world; Key = MotionKey.From((ulong)index + 1);
        var skeleton = CoverageCostGeometry.CreateSkeleton();
        _hosts = geometry.HostMeshes(skeleton).Select(m => scene.LoadSkinnedMesh(m)).ToArray();
        Conformer = scene.CreateHostConformer(skeleton, _hosts);
        var handles = equipment.Meshes.Select(m => scene.LoadMesh(m)).ToArray();
        Commands = new CoverageCommandPose[geometry.Commands.Length];
        _equipment = handles.Select((h, part) => new RigidInstanceDraw(h, world)
        { Motion = MotionKey.From((ulong)(1000 + index * handles.Length + part)) }).ToArray();
        _frozen = new RigidInstanceDraw[handles.Length];
        _memberships = new CoverageEquipmentCommand[Commands.Length];
        var supports = new CoverageSupportSurface[Commands.Length];
        for (int c = 0; c < Commands.Length; c++)
        {
            var command = geometry.Commands[c];
            var ranges = command.InwardSupport.Select(r => new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray();
            var support = supports[c] = CoverageSupportSurface.Compile(equipment.Source, ranges, command.CoverageLocal);
            Commands[c] = new(command.Id, (ulong)(index + 1), Conformer.Topology, c, support,
                world, support.CoverageLocal * world, command.MinZ, command.MaxZ, command.CollarWidth,
                command.Clearance, command.MaxWorldRecession);
            _memberships[c] = new(command.Id, support);
        }
        Conformer.ReserveSupports(supports);
    }

    internal void Queue(int frameIndex, bool covered)
    {
        PoseId = (frameIndex + _index) % 2;
        CoverageCostGeometry.Pose(PoseId, Palette);
        Matrix4x4 equipmentWorld = Palette[0] * World;
        for (int part = 0; part < _equipment.Length; part++) _equipment[part] = _equipment[part] with { World = equipmentWorld };
        for (int c = 0; c < Commands.Length; c++) Commands[c] = Commands[c] with
        { EquipmentWorld = equipmentWorld, CoverageWorld = Commands[c].Support.CoverageLocal * equipmentWorld };
        Staged = true;
        if (covered)
        {
            Staged = _scene.TryStageCoverageEquipment(Key, Commands[0].EquipmentInstanceId, _memberships,
                _equipment, _frozen, out _);
            foreach (var draw in _frozen) _scene.Draw(draw);
            Staged &= Conformer.TryPrepare(Palette, World, Commands, Key, CoverageFallback.UncoveredCurrentPose,
                out var frame, out _);
            Frame = frame;
        }
        else foreach (var draw in _equipment) _scene.Draw(draw);
        var outline = _scene.BeginMeshOutline(Color.White, 1.25f);
        foreach (var mesh in _hosts)
        {
            var draw = new SkinnedInstanceDraw(mesh, World) { Motion = Key, Coverage = covered ? Frame : default };
            _scene.DrawSkinned(draw, Palette);
            _scene.DrawSkinnedOutline(outline, draw, Palette);
        }
    }

    public void Dispose() => Conformer.Dispose();
}
