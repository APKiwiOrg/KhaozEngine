using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Native sequence source. Execution and backend evidence belong to the native acceptance task.</summary>
public sealed class CoverageTemporalGpuTests
{
    [GpuFact]
    public void Point_projection_arithmetic_refusal_presents_the_current_pose_in_every_pass()
    {
        using var scene = new CoverageGpuScene();
        scene.AssertArithmeticRefusal();
    }

    [GpuFact]
    public void Failed_equipment_replacement_rolls_back_the_rendered_native_identity()
    {
        using var scene = new CoverageGpuScene();
        CoverageGpuBindingProof.AssertRollback(scene);
    }

    [GpuTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Transitions_leave_no_original_skin_history(bool split, bool upscale)
    {
        using var scene = new CoverageGpuScene();
        scene.AssertTransitions(split, upscale);
    }

    [GpuTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Final_motion_and_local_transition_mask_survive_equip_refusal_and_unequip(bool split, bool upscale)
    {
        using var fixture = new TemporalFixture(96, 72, s =>
        {
            s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Post.Temporal.Upscale = upscale ? TemporalUpscale.Performance : TemporalUpscale.Native;
            s.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
            s.FrustumCulling = false;
            s.Camera.Frame(Vector3.Zero, new Vector3(4, 3, 4));
        });
        using var actor = new Actor(fixture.Scene);
        fixture.Frame((s, _) => actor.Draw(1, 1, Matrix4x4.Identity));
        Assert.Contains((byte)255, Mask(fixture));
        Assert.Contains((byte)0, Mask(fixture));

        fixture.Scene.RenderOrigin = new(128, 0, 0);
        fixture.Frame((s, _) => actor.Draw(1, .5f, Matrix4x4.CreateTranslation(.04f, 0, 0)));
        Assert.All(Mask(fixture), value => Assert.Equal((byte)0, value));
        var motion = fixture.Scene.ReadMotionTargetForTests();
        Assert.Contains(motion.Motion, value => !MotionMath.IsBackground(value) && value.LengthSquared() > 1e-8f);

        fixture.Frame((s, _) => actor.Draw(2, 1, Matrix4x4.Identity));
        Assert.Contains((byte)255, Mask(fixture));
        AssertRejectedConfidence(fixture);
        fixture.Frame((s, _) => actor.Draw(2, 1, Matrix4x4.Identity, submitEquipment: false));
        Assert.Equal(CoveragePresentation.UncoveredFallback, fixture.Scene.GetCoverageOutcome(actor.Frame).Presentation);
        Assert.Contains((byte)255, Mask(fixture));
        fixture.Frame((s, _) => actor.Draw(2, 1, Matrix4x4.Identity));
        Assert.Contains((byte)255, Mask(fixture));
        fixture.Frame((s, _) => actor.DrawUncovered());
        Assert.Contains((byte)255, Mask(fixture));
        AssertRejectedConfidence(fixture);
    }

    static void AssertRejectedConfidence(TemporalFixture fixture)
    {
        byte[] mask = Mask(fixture);
        IGpuTexture target = fixture.Scene.CoverageTransitionForTests.Texture!;
        var history = fixture.Scene.TemporalHistory;
        float[] state = TemporalTextureIo.Read(fixture.Device, history.Confidence(history.WriteIndex));
        int rejected = 0;
        for (int y = 0; y < fixture.DisplayHeight; y++)
            for (int x = 0; x < fixture.DisplayWidth; x++)
            {
                int mx = Math.Clamp((int)((x + .5f) * target.Width / fixture.DisplayWidth
                    + fixture.Scene.CurrentFrameView.JitterPixels.X), 0, (int)target.Width - 1);
                int my = Math.Clamp((int)((y + .5f) * target.Height / fixture.DisplayHeight
                    + fixture.Scene.CurrentFrameView.JitterPixels.Y), 0, (int)target.Height - 1);
                if (mask[my * (int)target.Width + mx] == 0) continue;
                Assert.InRange(state[(y * fixture.DisplayWidth + x) * 2], 0, 1f / 15f + .001f);
                rejected++;
            }
        Assert.True(rejected > 0);
    }

    static byte[] Mask(TemporalFixture fixture)
    {
        IGpuTexture texture = fixture.Scene.CoverageTransitionForTests.Texture!;
        IGpuDevice device = fixture.Device;
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(texture.Width,
            texture.Height, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "CoverageTemporal.ReadMask")) commands.CopyTexture(texture, staging);
        device.Submit(commands); device.WaitForIdle();
        var map = device.Map(staging, GpuMapMode.Read);
        byte[] pixels = new byte[texture.Width * texture.Height];
        try
        {
            for (int y = 0; y < texture.Height; y++)
                Marshal.Copy(map.Data + (int)(y * map.RowPitch), pixels, y * (int)texture.Width, (int)texture.Width);
        }
        finally { device.Unmap(staging); }
        return pixels;
    }

    sealed class Actor : IDisposable
    {
        readonly Scene3D scene;
        readonly CoverageFixture data = CoverageFixture.CurvedPanel(seams: false);
        readonly SkinnedHostConformer conformer;
        readonly SkinnedMeshHandle host;
        readonly MeshHandle rigid;
        readonly CoverageSupportSurface support;
        readonly Matrix4x4[] palette = CoverageFixture.Pose;
        readonly RigidInstanceDraw[] equipment = new RigidInstanceDraw[1];
        internal SkinnedCoverageFrame Frame;

        internal Actor(Scene3D scene)
        {
            this.scene = scene;
            var skeleton = new Skeleton([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1]);
            uint[] indices = data.Source.Triangles.SelectMany(t => new[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray();
            var mesh = new SkinnedGltfMesh(data.Source.SkinVertices.ToArray(), indices,
                [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity], skeleton);
            var covered = new SkinnedGltfMesh(mesh.Vertices, indices, mesh.InverseBind, mesh.RestPose, skeleton)
            { Coverage = HostCoveragePart.Create(data.Map, data.Source, 0, mesh) };
            host = scene.LoadSkinnedMesh(covered);
            conformer = scene.CreateHostConformer(skeleton, [host]);
            support = CoverageCommands.Plate(data);
            rigid = scene.LoadMesh(new GltfMesh(data.RigidPositions.Select(p =>
                new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), data.RigidIndices));
        }

        internal void Draw(ulong instance, float weight, Matrix4x4 world, bool submitEquipment = true)
        {
            var commands = CoverageCommands.Poses(data, conformer.Topology, support, world)
                .Select(c => c with { EquipmentInstanceId = instance, TransitionWeight = weight }).ToArray();
            Assert.True(scene.TryStageCoverageEquipment(MotionKey.From(10), 1, instance, support,
                [new RigidInstanceDraw(rigid, world)], equipment, out var failure), failure.ToString());
            Assert.True(conformer.TryPrepare(palette, world, commands, MotionKey.From(10),
                CoverageFallback.UncoveredCurrentPose, out Frame, out failure), failure.ToString());
            scene.DrawSkinned(new SkinnedInstanceDraw(host, world) { Coverage = Frame, Motion = MotionKey.From(10) }, palette);
            if (submitEquipment) scene.Draw(equipment[0]);
        }

        internal void DrawUncovered() => scene.DrawSkinned(new SkinnedInstanceDraw(host, Matrix4x4.Identity)
        { Motion = MotionKey.From(10) }, palette);
        public void Dispose() => conformer.Dispose();
    }
}
