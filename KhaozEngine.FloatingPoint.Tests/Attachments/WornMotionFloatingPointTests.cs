using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Determinism;
using KhaozEngine.Render3D;
using KhaozEngine.Simulation;
using KhaozEngine.Tests.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class WornMotionFloatingPointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Direct_and_batched_motion_agree_at_the_teleport_boundary_under_changed_rounding(bool hostileScheduler)
    {
        if (!DeterministicFp.IsSupported) return;
        var skeleton = new Skeleton([-1], [JointPose.Identity], [0], [0]);
        var joint = new ChainJointRest(-Vector3.UnitY, Quaternion.Identity, Vector3.One);
        var settings = new ChainSettings(8f, 3f, 1f, .85f, MathF.PI * .65f, .02f);
        var rig = new ChainRig(1, [new ChainRigChain(0, [joint, joint], -Vector3.UnitY, settings)], []);
        var options = new ChainSimulationOptions { Gravity = 9.81f * Vector3.UnitX };
        var direct = new WornMotion(new object(), skeleton, [], rig, [0], [], options);
        var batched = new WornMotion(new object(), skeleton, [], rig, [0], [], options);
        using var workers = new WorkerJobScheduler(1);
        var batch = new WornMotionBatch(hostileScheduler ? new TowardZeroScheduler() : workers);
        Matrix4x4[] palette = [Matrix4x4.Identity];
        const float delta = 1f / 60;
        var rest = direct.JointsFromAnchor.ToArray();
        for (int frame = 0; frame < 12; frame++)
        {
            direct.Step(skeleton, palette, Matrix4x4.Identity, delta);
            batched.Step(skeleton, palette, Matrix4x4.Identity, delta);
        }
        Assert.False(direct.JointsFromAnchor.SequenceEqual(rest));

        // Nearest rounds its squared distance just above the default threshold of 4. Toward zero rounds to 4.
        Matrix4x4 model = Matrix4x4.CreateTranslation(2f, 3f / 134217728f, 0f);
        batch.Add(batched, skeleton, palette, model, delta);
        int afterDirect, afterBatch;
        try
        {
            FpRounding.SetTowardZero();
            direct.Step(skeleton, palette, model, delta);
            afterDirect = FpRounding.Get();
            batch.Step();
            afterBatch = FpRounding.Get();
        }
        finally
        {
            FpRounding.SetToNearest();
        }

        Assert.Equal(FpRounding.TowardZero, afterDirect);
        Assert.Equal(FpRounding.TowardZero, afterBatch);
        AssertBits(rest, direct.JointsFromAnchor);
        AssertBits(direct.JointsFromAnchor, batched.JointsFromAnchor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cloth_direct_and_batched_match_at_non_tick_poses_under_changed_rounding(bool reuseMaterialFactor)
    {
        if (!DeterministicFp.IsSupported) return;
        using var fixture = new WornClothFloatingPointFixture();
        var direct = fixture.Set.CreateMotion(null);
        var batched = fixture.Set.CreateMotion(null);
        var directSlot = new WornClothSlot(reuseMaterialFactor);
        var batchSlot = new WornClothSlot(reuseMaterialFactor);
        directSlot.Prepare(fixture.Set.ClothSurface(1)!);
        batchSlot.Prepare(fixture.Set.ClothSurface(1)!);
        var batch = new WornMotionBatch(new TowardZeroScheduler());
        var clean = fixture.Host.ComposeRestPose();
        var pose = (Matrix4x4[])clean.Clone();
        var identity = Matrix4x4.Identity;
        var delta = 1f / 144;
        var directSelection = new WornClothSelection(directSlot, 0, 1);
        var batchSelection = new WornClothSelection(batchSlot, 0, 1);
        bool solved = false;
        for (int frame = 0; frame < 12; frame++)
        {
            pose[0].M43 = frame * .0003f;
            batch.Add(batched, fixture.Host, pose, identity, delta, MotionLevel.Simulated, batchSelection);
            int afterDirect;
            int afterBatch;
            try
            {
                FpRounding.SetTowardZero();
                direct.Step(fixture.Host, pose, identity, delta, MotionLevel.Simulated, directSelection);
                afterDirect = FpRounding.Get();
                batch.Step();
                afterBatch = FpRounding.Get();
            }
            finally
            {
                FpRounding.SetToNearest();
            }
            batch.Clear();
            Assert.Equal(FpRounding.TowardZero, afterDirect);
            Assert.Equal(FpRounding.TowardZero, afterBatch);
            Assert.Equal(direct.ClothStatus, batched.ClothStatus);
            Assert.True(direct.TryGetClothPresentation(0, 1, out var expected));
            Assert.True(batched.TryGetClothPresentation(0, 1, out var actual));
            Assert.True(MemoryMarshal.AsBytes(expected.Nodes).SequenceEqual(MemoryMarshal.AsBytes(actual.Nodes)));
            solved |= direct.ClothStatus.Substeps > 0 && direct.ClothStatus.Solve.Status == ClothStepStatus.Accepted;
        }
        Assert.True(solved);
    }

    private static void AssertBits(ReadOnlySpan<Matrix4x4> expected, ReadOnlySpan<Matrix4x4> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)));
    }

    private sealed class TowardZeroScheduler : IJobScheduler
    {
        public void For(int count, Action<int> body)
        {
            int previous = FpRounding.Get();
            try
            {
                FpRounding.SetTowardZero();
                for (int index = 0; index < count; index++) body(index);
            }
            finally
            {
                FpPoke.FeSetRound(previous);
            }
        }
    }
}
