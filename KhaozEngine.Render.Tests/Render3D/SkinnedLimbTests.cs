using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Headless tests for the turn-key SkinnedLimb convenience component. The whole motion pipeline
    // (ProceduralChainSolver -> PolylineFrames -> bones) runs GPU-free via SkinnedLimb.CreateHeadless,
    // so no device is needed; only the tube upload + Draw are GPU-bound (covered in Gpu/SkinnedLimbGpuTests).
    // In the AllocSensitive collection: its zero-alloc assertion must not run while the parallel-ForEach tests
    // churn the GC on other threads.
    [Collection("AllocSensitive")]
    public class SkinnedLimbTests
    {
        static readonly ChainConfig Writhe = ChainConfig.Writhe;
        static ChainConfig Straight => new(0.5f, writheAmplitude: 0f, writheFrequency: 2f,
            segmentPhaseLag: 0.7f, coilWaveFrac: 0.6f, outOfPlaneFrac: 0.4f);

        [Fact]
        public void Bones_HaveOneEntryPerBone()
        {
            var limb = SkinnedLimb.CreateHeadless(boneCount: 8, Writhe);
            Assert.Equal(8, limb.BoneCount);
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 1.0f);
            Assert.Equal(8, limb.Bones.Length);
            Assert.Equal(8, limb.Spine.Length);
        }

        [Fact]
        public void Update_BonesTrackTheSpine_TranslationsSitAtSpinePoints()
        {
            var limb = SkinnedLimb.CreateHeadless(10, Writhe);
            limb.Update(new Vector3(1, 2, 3), Vector3.UnitX, Vector3.UnitY, 0.9f);
            for (int i = 0; i < limb.BoneCount; i++)
                Assert.True(Vector3.Distance(limb.Bones[i].Translation, limb.Spine[i]) < 1e-4f,
                    $"bone {i} translation {limb.Bones[i].Translation} should sit at spine point {limb.Spine[i]}");
        }

        [Fact]
        public void Update_BonesMatchSolverPlusPolylineFramesReference()
        {
            // The limb's bones must equal the hand-wired ProceduralChainSolver -> PolylineFrames output exactly.
            var limb = SkinnedLimb.CreateHeadless(9, Writhe, Axis.Z);
            var root = new Vector3(0.5f, -1f, 2f);
            limb.Update(root, Vector3.UnitZ, Vector3.UnitY, 1.7f);

            var spine = new Vector3[9];
            ProceduralChainSolver.Solve(root, Vector3.UnitZ, Vector3.UnitY, 1.7f, Writhe, spine);
            var frames = PolylineFrames.Build(spine, Axis.Z, Vector3.UnitY);

            for (int i = 0; i < 9; i++)
            {
                Assert.True(Vector3.Distance(limb.Spine[i], spine[i]) < 1e-5f, $"spine {i} mismatch");
                Assert.True(MatClose(limb.Bones[i], frames[i]), $"bone {i} mismatch");
            }
        }

        [Fact]
        public void StraightConfig_BonesMarchAlongForward()
        {
            var limb = SkinnedLimb.CreateHeadless(6, Straight, Axis.Z);
            limb.Update(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1.1f);
            // Zero writhe => spine is a straight line along forward (+X), step = SegmentLength.
            for (int i = 0; i < limb.BoneCount; i++)
            {
                var expected = Vector3.UnitX * (Straight.SegmentLength * i);
                Assert.True(Vector3.Distance(limb.Bones[i].Translation, expected) < 1e-4f,
                    $"bone {i} {limb.Bones[i].Translation} != {expected}");
            }
        }

        [Fact]
        public void ReachUpdate_FullWeight_TipBoneNearTarget()
        {
            var limb = SkinnedLimb.CreateHeadless(10, Writhe, Axis.Z);
            var target = new Vector3(1.5f, 2f, 0.5f); // within reach (9 * 0.5 = 4.5)
            limb.Update(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 0.6f, target, reachWeight: 1f);
            Vector3 tip = limb.Spine[limb.BoneCount - 1];
            Assert.True(Vector3.Distance(tip, target) < 2e-2f, $"tip {tip} should reach target {target}");
        }

        [Fact]
        public void ReachUpdate_ZeroWeight_MatchesWritheTip()
        {
            var reach = SkinnedLimb.CreateHeadless(10, Writhe, Axis.Z);
            var writhe = SkinnedLimb.CreateHeadless(10, Writhe, Axis.Z);
            reach.Update(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1.4f, new Vector3(0, 5, 0), reachWeight: 0f);
            writhe.Update(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1.4f);
            Assert.True(Vector3.Distance(reach.Spine[9], writhe.Spine[9]) < 1e-2f,
                "reachWeight 0 tip should match the writhe-only tip");
        }

        [Fact]
        public void Update_IsDeterministic()
        {
            var a = SkinnedLimb.CreateHeadless(12, Writhe);
            var b = SkinnedLimb.CreateHeadless(12, Writhe);
            a.Update(new Vector3(1, 0, 0), Vector3.UnitZ, Vector3.UnitY, 2.7f);
            b.Update(new Vector3(1, 0, 0), Vector3.UnitZ, Vector3.UnitY, 2.7f);
            for (int i = 0; i < 12; i++)
            {
                Assert.Equal(a.Spine[i], b.Spine[i]);
                Assert.Equal(a.Bones[i], b.Bones[i]);
            }
        }

        [Fact]
        public void Update_ReusesBuffers_NoPerFrameAllocation()
        {
            var limb = SkinnedLimb.CreateHeadless(16, Writhe);
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 0.1f);

            // The motion path must not allocate: drive many frames and assert the GC allocated byte count is flat.
            // Retries once before failing (see AllocAssert.NoPerCallAllocation) to ride out an unrelated gen-0
            // collision from the rest of the process, per issue #284.
            AllocAssert.NoPerCallAllocation("Update over 400 calls", () =>
            {
                for (int f = 0; f < 200; f++)
                {
                    float t = f * 0.016f;
                    limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, t);
                    limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, t, new Vector3(1, 1, 1), 0.5f);
                }
            });
        }

        [Fact]
        public void Update_BacksOntoTheSameStorageAcrossFrames()
        {
            // Defensive: the spans should keep pointing at the same backing buffers frame to frame (proof the
            // limb is mutating in place, not handing back fresh arrays).
            var limb = SkinnedLimb.CreateHeadless(5, Writhe);
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 0.2f);
            var b0 = limb.Bones[0];
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 0.9f);
            Assert.NotEqual(b0, limb.Bones[0]); // content changed in place
            Assert.Equal(5, limb.Bones.Length);
        }

        [Fact]
        public void RuntimeConfigChange_RetunesMotion()
        {
            var limb = SkinnedLimb.CreateHeadless(8, Straight, Axis.Z);
            limb.Update(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1.0f);
            float straightLateral = Lateral(limb.Spine[7]);
            limb.Config = Writhe; // retune to a lively writhe
            limb.Update(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1.0f);
            float writheLateral = Lateral(limb.Spine[7]);
            Assert.True(straightLateral < 1e-3f, $"straight config tip should be on-axis, got {straightLateral}");
            Assert.True(writheLateral > 1e-2f, $"writhe config tip should bend off-axis, got {writheLateral}");
        }

        [Fact]
        public void Headless_Draw_IsNoOp_AndDisposeIsSafe()
        {
            var limb = SkinnedLimb.CreateHeadless(6, Writhe);
            Assert.Equal(0, limb.Handle.Generation); // no GPU mesh
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 0.5f);
            limb.Dispose();
            limb.Dispose(); // idempotent
        }

        [Fact]
        public void Update_AfterDispose_Throws()
        {
            var limb = SkinnedLimb.CreateHeadless(6, Writhe);
            limb.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 0f));
        }

        [Fact]
        public void Ctor_RejectsZeroBones()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SkinnedLimb.CreateHeadless(0, Writhe));
        }

        static float Lateral(Vector3 p) => MathF.Sqrt(p.Y * p.Y + p.Z * p.Z); // deviation off the +X forward axis
        static bool MatClose(Matrix4x4 a, Matrix4x4 b)
        {
            float d = 0;
            d += MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13) + MathF.Abs(a.M14 - b.M14);
            d += MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M24 - b.M24);
            d += MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) + MathF.Abs(a.M34 - b.M34);
            d += MathF.Abs(a.M41 - b.M41) + MathF.Abs(a.M42 - b.M42) + MathF.Abs(a.M43 - b.M43) + MathF.Abs(a.M44 - b.M44);
            return d < 1e-4f;
        }
    }
}
