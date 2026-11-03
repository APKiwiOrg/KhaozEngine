using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Each private shader gate renders the same deterministic scene twice through the real Scene3D path on a real device,
/// switch off and switch on, and every main pass attachment plus the resolved picture must match bit for bit on every
/// frame. Each scene also proves it reaches the gated path, and that a real change to the picture does show in the
/// comparison, because a scene that never reached a gate would pass while proving nothing. The gated paths cannot show in
/// the output by design, so a broken uniform would also pass here. <c>ShaderGateProbeTests</c>
/// holds the uploaded values and the shader conditions. Comparing the off state with the released shader needs another
/// build of the released tree and is not done here.
/// </summary>
public sealed partial class ShaderGatePixelIdentityGpuTests(ITestOutputHelper output)
{
    const int W = 256, H = 144, Frames = 6;
    const int Bands = 4;

    sealed record Attachments(byte[] Hdr, byte[] Normal, byte[] Depth, byte[] Motion, byte[] Visibility, byte[] Display);

    sealed class Stage : IDisposable
    {
        readonly GpuDeviceContext _gpu;
        readonly IGpuTexture _display;
        readonly IGpuFramebuffer _target;
        readonly IGpuCommandList _commands;
        int _frame;

        internal Stage(ShaderGateProbe gates, int cascades)
        {
            _gpu = GpuDeviceContext.CreateHeadless();
            IGpuResourceFactory f = _gpu.GpuDevice.Factory;
            _display = f.CreateTexture(GpuTextureDescription.Texture2D(W, H, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = f.CreateFramebuffer(null, _display);
            _commands = f.CreateCommandList();
            Scene = new Scene3D(_gpu.GpuDevice, _target.Outputs,
                new ShadowSettings { ShadowCascadeCount = cascades, ShadowMapResolution = 1024 }, requestLitVisibility: true);
            Scene.ForceTemporalForTests = true;
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Post.RenderScale = RenderScale.MatchViewport;
            Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            Scene.Post.Hdr.Enabled = true;
            Scene.Post.Starfield = false;
            Scene.Post.Sky.Enabled = false;
            Scene.Post.Background = BackgroundMode.Solid;
            Scene.Post.BackgroundColor = new Color(0.1f, 0.05f, 0.15f, 1f);
            Scene.Post.Outline = false;
            Scene.Post.CelBands = Bands;
            Scene.Post.AmbientColor = new Color(0.05f, 0.05f, 0.05f, 1f);
            Scene.Post.FillLightColor = Color.Black;
            Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            Scene.Post.Quality.Shadows.PointShadows.Filter = PointShadowFilter.Soft;
            Scene.ProbeShaderGates = gates;
        }

        internal Scene3D Scene { get; }
        internal IGpuDevice Device => _gpu.GpuDevice;

        internal Attachments Frame(Action<Scene3D, int> draw)
        {
            IGpuDevice device = _gpu.GpuDevice;
            int n = _frame++;
            Scene.Begin();
            Scene.EffectTimeSeconds = n / 60f;
            draw(Scene, n);
            Scene.PrepareFrame(W, H);
            using (GpuRecording.Open(device, _commands, nameof(ShaderGatePixelIdentityGpuTests)))
                Scene.RenderInternal(_commands, W, H, _target);
            device.Submit(_commands);
            device.WaitForIdle();
            device.Present();
            return new(Raw(device, Scene.TemporalPostTargetsForTests!.OpaqueColor), Raw(device, Scene.ResolvedNormalTarget),
                Raw(device, Scene.ResolvedDepthTarget), Raw(device, Scene.MotionTargetForTests!),
                Raw(device, Scene.ResolvedVisibilityTarget!), Raw(device, _display));
        }

        public void Dispose()
        {
            Scene.Dispose();
            _commands.Dispose();
            _target.Dispose();
            _display.Dispose();
            _gpu.Dispose();
        }
    }

    // The stored bytes. Any conversion could fold distinct bit patterns together.
    static byte[] Raw(IGpuDevice device, IGpuTexture source)
    {
        int texel = source.Format switch
        {
            GpuPixelFormat.R16G16B16A16Float => 8,
            GpuPixelFormat.R8G8B8A8UNorm or GpuPixelFormat.R32Float or GpuPixelFormat.R16G16Float => 4,
            GpuPixelFormat.R8UNorm => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Format, "no raw layout for this attachment"),
        };
        using IGpuTexture staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            source.Width, source.Height, source.Format, GpuTextureUsage.Staging));
        using (IGpuCommandList commands = device.Factory.CreateCommandList())
        {
            using (GpuRecording.Open(device, commands, nameof(Raw))) commands.CopyTexture(source, staging);
            device.Submit(commands);
            device.WaitForIdle();
        }
        int row = (int)source.Width * texel;
        var bytes = new byte[row * (int)source.Height];
        MappedData map = device.Map(staging, GpuMapMode.Read);
        unsafe
        {
            for (int y = 0; y < source.Height; y++)
                new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, row).CopyTo(bytes.AsSpan(y * row));
        }
        device.Unmap(staging);
        return bytes;
    }

    static readonly (string Name, Func<Attachments, byte[]> Read)[] Parts =
    [
        ("hdr", a => a.Hdr), ("normal", a => a.Normal), ("sceneDepth", a => a.Depth),
        ("motion", a => a.Motion), ("keyVisibility", a => a.Visibility), ("display", a => a.Display),
    ];

    static int Differing(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int differing = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) differing++;
        return differing;
    }

    // Logs every part before judging, so one run says everything it can. Off twice comes first: a difference between
    // two off runs is the scene's own nondeterminism, not the gate's.
    void AssertIdentical(string gate, IReadOnlyList<Attachments> off, IReadOnlyList<Attachments> offAgain, IReadOnlyList<Attachments> on)
    {
        Assert.Equal(Frames, off.Count);
        Assert.Equal(Frames, offAgain.Count);
        Assert.Equal(Frames, on.Count);
        int repeatDifferences = Compare(gate + " off vs off", off, offAgain);
        int gateDifferences = Compare(gate + " off vs on", off, on);
        Assert.Equal(0, repeatDifferences);
        Assert.Equal(0, gateDifferences);
    }

    int Compare(string what, IReadOnlyList<Attachments> a, IReadOnlyList<Attachments> b)
    {
        int total = 0;
        foreach ((string name, Func<Attachments, byte[]> read) in Parts)
        {
            int[] differing = Enumerable.Range(0, Frames).Select(frame => Differing(read(a[frame]), read(b[frame]))).ToArray();
            output.WriteLine($"{what} {name}: {read(a[0]).Length} bytes a frame, differing bytes per frame {string.Join(",", differing)}");
            total += differing.Sum();
        }
        return total;
    }

    // ---- Point shadows ----------------------------------------------------------------------------------------

    static readonly Vector3 LampAt = new(0f, 0.6f, 0f);
    const float LampRadius = 9f;

    static Action<Scene3D, int> PointScene(Scene3D scene, bool casters)
    {
        MeshHandle plane = scene.LoadMesh(MeshPrimitives.Plane());
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box());
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f));
        scene.Post.LightColor = new Color(0.2f, 0.2f, 0.2f, 1f);
        scene.Camera.Azimuth = 0.4f;
        scene.Camera.Elevation = 1.1f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.Target = Vector3.Zero;
        scene.Camera.OrthoSize = 12f;
        return (s, n) =>
        {
            s.Draw(plane, Matrix4x4.CreateScale(24f, 1f, 24f), new Color(0.7f, 0.7f, 0.7f, 1f), Material.Shiny(0.5f, 16f), castsShadows: false);
            if (casters)
            {
                // Lit, shadowing and back faces all inside the light's radius. The second moves, for the transient row.
                s.Draw(box, Matrix4x4.CreateScale(1f, 2f, 1f) * Matrix4x4.CreateTranslation(2f, 1f, 0f), new Color(0.8f, 0.3f, 0.2f, 1f), Material.Shiny(0.5f, 16f));
                s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(-1.5f + 0.1f * n, 0.5f, 1.5f)) { Motion = MotionKey.From(5) });
            }
            s.AddLight(LampAt, new Color(1f, 0.8f, 0.6f, 1f), LampRadius, 3f, LightShadow.Static(1));
            s.AddLight(new Vector3(-4f, 1f, -2f), new Color(0.4f, 0.6f, 1f, 1f), 6f, 2f, LightShadow.Dynamic);
        };
    }

    // An estimate from the stored 8-bit normal and depth, for the static lamp alone. It replays the shader's banded N dot L
    // in C#, outside any timed shading. The second light only adds to the real count.
    static (int Reached, int Zero) CountZeroNdl(Scene3D scene, Attachments frame)
    {
        Assert.True(Matrix4x4.Invert(scene.CurrentFrameView.ViewProjection, out Matrix4x4 inverse));
        int reached = 0, zero = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float depth = BitConverter.ToSingle(frame.Depth, (y * W + x) * 4);
                if (depth >= 1f) continue;
                int at = (y * W + x) * 4;
                if (frame.Normal[at] == 0 && frame.Normal[at + 1] == 0 && frame.Normal[at + 2] == 0) continue;
                var normal = new Vector3(frame.Normal[at], frame.Normal[at + 1], frame.Normal[at + 2]) / 255f * 2f - Vector3.One;
                Vector4 world = Vector4.Transform(new Vector4((x + 0.5f) / W * 2f - 1f, 1f - (y + 0.5f) / H * 2f, depth, 1f), inverse);
                Vector3 toLight = LampAt - new Vector3(world.X, world.Y, world.Z) / world.W;
                float distance = toLight.Length();
                if (distance >= LampRadius) continue;
                reached++;
                float ndl = MathF.Max(Vector3.Dot(Vector3.Normalize(normal), toLight / distance), 0f);
                if (MathF.Floor(ndl * Bands + 0.5f) == 0f) zero++;
            }
        return (reached, zero);
    }

    [GpuFact]
    public void Point_shadow_gate_is_bit_identical_where_banded_ndl_is_exactly_zero()
    {
        const ShaderGateProbe Gate = ShaderGateProbe.PointShadowZeroNdl;
        List<Attachments> Run(ShaderGateProbe gates, bool casters, out (int Reached, int Zero) count, out int shadowed)
        {
            using var stage = new Stage(gates, 4);
            Action<Scene3D, int> draw = PointScene(stage.Scene, casters);
            var frames = new List<Attachments>();
            for (int i = 0; i < Frames; i++) frames.Add(stage.Frame(draw));
            count = CountZeroNdl(stage.Scene, frames[^1]);
            shadowed = stage.Scene.LastShadowPassDiagnostics.PointShadowedLights;
            return frames;
        }

        List<Attachments> off = Run(ShaderGateProbe.None, true, out var count, out int shadowed);
        List<Attachments> offAgain = Run(ShaderGateProbe.None, true, out _, out _);
        List<Attachments> on = Run(Gate, true, out _, out _);
        List<Attachments> bare = Run(ShaderGateProbe.None, false, out _, out _);

        output.WriteLine($"G1 lamp reached {count.Reached} of {W * H} fragments, about {count.Zero} with banded ndl exactly 0, shadowed lights {shadowed}");
        Assert.True(shadowed >= 1, "the gate needs a shadowed light");
        Assert.True(count.Zero > 500, $"only about {count.Zero} fragments took the gated path");
        Assert.True(count.Reached - count.Zero > 500, "the lamp must also contribute, so a wrong skip would show");
        int moved = Differing(off[^1].Hdr, bare[^1].Hdr);
        output.WriteLine($"G1 control: removing the casters changes {moved} HDR bytes");
        Assert.True(moved > 1000, "the comparison must see a real change in the shadowed picture");
        AssertIdentical("G1", off, offAgain, on);
    }

    // ---- Underwater march -------------------------------------------------------------------------------------

    const float BedY = -3f;

    static Action<Scene3D, int> UnderwaterScene(Scene3D scene, bool caster)
    {
        MeshHandle plane = scene.LoadMesh(MeshPrimitives.Plane());
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box());
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(0f, -1f, 0.6f));
        scene.Post.LightColor = Color.White;
        scene.Post.Quality.Shadows.ShadowStrength = 1f;
        scene.Post.Quality.Shadows.ShadowNearDistance = 20f;
        scene.Post.Quality.Shadows.ShadowNormalOffset = 0.5f;
        scene.Post.Quality.Shadows.ShadowConstantBias = 0.00005f;
        scene.Post.Quality.Shadows.ShadowSlopeBias = 0.00005f;
        scene.Post.Water = new WaterSettings
        {
            UnderwaterSunlight = true,
            UnderwaterPenumbraPerMetre = 0.03f,
            AbsorptionPerMetre = new Color(0.6f, 0.3f, 0.15f, 0f),
            Opacity = 0f,
            GlintStrength = 0f,
            FoamStrength = 0f,
            SwellAmplitude = 0f,
            SwellSteepness = 0f,
            NormalStrength = 0f,
            SurfStrength = 0f,
            ShoalingStrength = 0f,
            GridMode = WaterGridMode.Clipmap,
        };
        scene.CameraOverride = new FlyCamera3D
        {
            Position = new Vector3(0f, 10f, 1.5f),
            Yaw = MathF.PI,
            Pitch = -MathF.PI / 2f,
            FieldOfView = 0.7f,
            AspectRatio = (float)W / H,
            NearPlane = 0.1f,
            FarPlane = 25f,
        };
        return (s, n) =>
        {
            s.Draw(plane, Matrix4x4.CreateScale(12f, 1f, 12f) * Matrix4x4.CreateTranslation(0f, BedY, 0f),
                new Color(0.65f, 0.65f, 0.65f, 1f), Material.None, castsShadows: false);
            // Near the surface, so the march reaches zero in an early slice and later slices are the ones skipped.
            if (caster) s.DrawShadowOnly(box, Matrix4x4.CreateScale(1.8f, 0.2f, 3f) * Matrix4x4.CreateTranslation(0f, -0.6f, 0f));
            s.DrawWater(new WaterPlane(0f, 0f, 0f, 6f, floorY: BedY - 0.1f));
        };
    }

    [GpuFact]
    public void Underwater_march_gate_is_bit_identical_where_the_running_visibility_reaches_exact_zero()
    {
        const ShaderGateProbe Gate = ShaderGateProbe.UnderwaterMarchZero;
        List<Attachments> Run(ShaderGateProbe gates, bool caster)
        {
            using var stage = new Stage(gates, 4);
            Action<Scene3D, int> draw = UnderwaterScene(stage.Scene, caster);
            var frames = new List<Attachments>();
            for (int i = 0; i < Frames; i++) frames.Add(stage.Frame(draw));
            return frames;
        }

        List<Attachments> off = Run(ShaderGateProbe.None, true);
        List<Attachments> offAgain = Run(ShaderGateProbe.None, true);
        List<Attachments> on = Run(Gate, true);
        List<Attachments> bare = Run(ShaderGateProbe.None, false);

        // The caster is below the surface, so the entry filter stays lit and a stored zero is the march's own result.
        int zero = off[^1].Visibility.Count(v => v == 0);
        int lit = off[^1].Visibility.Count(v => v == 255);
        int bareZero = bare[^1].Visibility.Count(v => v == 0);
        output.WriteLine($"G2 key visibility: {zero} of {W * H} fragments stored 0, {lit} stored 255, without the caster {bareZero} stored 0, so about {zero - bareZero} reached the march's zero");
        Assert.True(lit > 500, "the bed must also be lit, so a wrong early exit would show");
        Assert.True(zero > bareZero + 500, "the zero must come from the submerged caster");
        AssertIdentical("G2", off, offAgain, on);
    }

    // ---- Rigid reuse ------------------------------------------------------------------------------------------

    static readonly Vector3 OriginStep = new(64f, 0f, -32f);
    const int StaticKeyed = 12;

    static Action<Scene3D, int> RigidScene(Scene3D scene, bool moves)
    {
        MeshHandle plane = scene.LoadMesh(MeshPrimitives.Plane());
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box());
        scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f));
        scene.Post.LightColor = Color.White;
        scene.Camera.Azimuth = 0.4f;
        scene.Camera.Elevation = 0.9f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.OrthoSize = 10f;
        scene.FrustumCulling = false;   // Every queued instance keeps its slot, so the slot counts below are exact.
        return (s, n) =>
        {
            // The camera keeps moving, so unchanged transforms still carry camera motion through PrevViewProj. A render
            // origin step partway exercises the reduction both matrices go through.
            s.Camera.Target = new Vector3(0.2f * n, 0f, -0.15f * n);
            if (n == 3) s.RenderOrigin = OriginStep;
            s.Draw(plane, Matrix4x4.CreateScale(40f, 1f, 40f));
            s.Draw(box, Matrix4x4.CreateTranslation(-6f, 0.5f, -5f));
            for (int i = 0; i < StaticKeyed; i++)
                s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateRotationY(0.3f * i) * Matrix4x4.CreateTranslation(-5f + i, 0.5f, i % 3 * 2f)) { Motion = MotionKey.From((ulong)(100 + i)) });
            float x = moves ? 0.4f * n : 0f;
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(x, 1.5f, 4f)) { Motion = MotionKey.From(99) });
        };
    }

    [GpuFact]
    public void Rigid_reuse_gate_is_bit_identical_for_static_and_moving_instances_under_a_moving_camera()
    {
        const ShaderGateProbe Gate = ShaderGateProbe.RigidWorldReuse;
        int boxVertices = MeshPrimitives.Box().Vertices.Length;
        int planeVertices = MeshPrimitives.Plane().Vertices.Length;
        List<Attachments> Run(ShaderGateProbe gates, bool moves, List<(int Own, int Previous)> slots)
        {
            using var stage = new Stage(gates, 4);
            Action<Scene3D, int> draw = RigidScene(stage.Scene, moves);
            var frames = new List<Attachments>();
            for (int i = 0; i < Frames; i++)
            {
                frames.Add(stage.Frame(draw));
                float[] motion = stage.Scene.MotionSlotsForTests.ToArray();
                slots.Add((motion.Count(s => s < 0f), motion.Count(s => s >= 0f)));
            }
            return frames;
        }

        var offSlots = new List<(int Own, int Previous)>();
        var onSlots = new List<(int Own, int Previous)>();
        List<Attachments> off = Run(ShaderGateProbe.None, true, offSlots);
        List<Attachments> offAgain = Run(ShaderGateProbe.None, true, []);
        List<Attachments> on = Run(Gate, true, onSlots);
        List<Attachments> still = Run(ShaderGateProbe.None, false, []);

        output.WriteLine($"G3 slots (own, previous) per frame, off {string.Join(" ", offSlots)}, on {string.Join(" ", onSlots)}");
        int keyedReused = onSlots[^1].Own - offSlots[^1].Own;
        int vertices = (onSlots[^1].Own - 1) * boxVertices + planeVertices;
        output.WriteLine($"G3 on the last frame before culling: {keyedReused} keyed instances newly reuse the world position, {onSlots[^1].Own} instances and {vertices} vertices take the path");
        // A settled frame: the static keyed instances qualify, the moving one never does.
        Assert.Equal(StaticKeyed, keyedReused);
        Assert.Equal(1, onSlots[^1].Previous);
        Assert.Equal(StaticKeyed + 1, offSlots[^1].Previous);
        int moved = Differing(off[^1].Motion, still[^1].Motion);
        output.WriteLine($"G3 control: stopping the moving instance changes {moved} motion bytes");
        Assert.True(moved > 100, "the comparison must see a real change in the motion target");
        AssertIdentical("G3", off, offAgain, on);
    }
}
