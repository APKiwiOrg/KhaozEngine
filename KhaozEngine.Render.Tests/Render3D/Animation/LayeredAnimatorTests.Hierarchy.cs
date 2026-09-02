using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    // The two gaps the #20 review left behind (#656), both about what happens AROUND a single additive joint rather
    // than on it. Every additive row in LayeredAnimatorTests.cs runs on OneBone, where world == local and there is
    // exactly one layer, so neither Skeleton.ComposeInto's parent composition nor LayeredAnimator's layer ordering is
    // observable there. These two rows make each of them observable:
    //
    //   1. an additive layer on a MID-CHAIN joint of Chain3 whose base is non-identity, read at the GRANDCHILD, so
    //      the delta has to survive `world[i] = local[i] * world[parent]` (Skeleton.cs) to land in the palette;
    //   2. TWO additive layers on one joint with DIFFERENT non-identity references, which pins the right-multiply
    //      order ComposeLocals documents (bottom layer first, so base * d1 * d2, never base * d2 * d1).
    //
    // Same class as the rest, so the helpers (Chain3, OneBone, PoseClip, AdditiveClip, Ref90X, QuatClose) are shared
    // and the AllocSensitive collection attribute on the other part covers these too.
    public partial class LayeredAnimatorTests
    {
        [Fact]
        public void Additive_OnMidChainJoint_DeltaReachesTheGrandchildThroughParentComposition()
        {
            // Chain3: 0 -> 1 -> 2. The base poses all three nodes non-trivially, so a mistake anywhere in the chain
            // moves node 2. The additive layer touches ONLY node 1 (explicit mask), with a non-identity reference
            // (90X) and a non-identity base on that same joint (90X then 90Z), which is the shape #20 was about.
            Skeleton skel = Chain3();
            Quaternion refRot = Ref90X();
            Quaternion base1Rot = Quaternion.Normalize(refRot * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
            Quaternion sampleRot = Quaternion.Normalize(refRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));

            var baseClip = new AnimationClip("base", 1f, new List<JointTrack>
            {
                PosedTrack(0, new Vector3(5f, 0f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)),
                PosedTrack(1, new Vector3(0f, 2f, 0f), base1Rot),
                PosedTrack(2, new Vector3(0f, 0f, 3f), Quaternion.Identity),
            });
            AnimationClip add = AdditiveClip("add", 1, refRot, sampleRot);

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer al = anim.AddLayer(add, LayerMode.Additive, mask: new BoneMask(new[] { 0f, 1f, 0f }));
            al.Time = 1f;

            Matrix4x4[] palette = anim.BonePalette();

            // Hand-composed expectation. The local-frame additive formula gives node 1 its rotation; the additive
            // clip keys rotation only, so node 1 keeps the base translation and unit scale.
            JointPose local0 = TrsPose(new Vector3(5f, 0f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));
            JointPose local1 = TrsPose(new Vector3(0f, 2f, 0f),
                Quaternion.Normalize(base1Rot * Quaternion.Normalize(Quaternion.Inverse(refRot) * sampleRot)));
            JointPose local2 = TrsPose(new Vector3(0f, 0f, 3f), Quaternion.Identity);

            Matrix4x4 world0 = local0.ToMatrix();
            Matrix4x4 world1 = local1.ToMatrix() * world0;
            Matrix4x4 world2 = local2.ToMatrix() * world1;

            Assert.True(MatClose(world0, palette[0]), "root world must be its own local");
            Assert.True(MatClose(world1, palette[1]), "the additive joint's world is local1 * world0");
            Assert.True(MatClose(world2, palette[2]), "the grandchild's world is local2 * world1, carrying the delta");

            // Two ways this row can go red that the OneBone rows physically cannot see.
            // (a) The delta never reaching the grandchild at all (base-only composition below node 1).
            Matrix4x4 noDeltaWorld1 = TrsPose(new Vector3(0f, 2f, 0f), base1Rot).ToMatrix() * world0;
            Assert.False(MatClose(world2, local2.ToMatrix() * noDeltaWorld1),
                "the grandchild must MOVE when an additive layer bends its parent");
            // (b) The parent composition applied on the wrong side (world[parent] * local instead of local * world[parent]).
            Matrix4x4 flipped1 = world0 * local1.ToMatrix();
            Assert.False(MatClose(world2, flipped1 * local2.ToMatrix()),
                "must be local * world[parent], not world[parent] * local");
        }

        [Fact]
        public void TwoAdditiveLayers_ComposeInLayerOrder_BaseTimesD1TimesD2()
        {
            // One joint, two additive layers, DIFFERENT non-identity references, non-commuting deltas. ComposeLocals
            // walks the stack bottom-to-top and each layer right-multiplies, so the bottom layer's delta sits nearer
            // the base: base * d1 * d2. Reversing the walk (or composing on the left) swaps the two and lands
            // somewhere visibly different, which is what the second half of this row rejects.
            Skeleton skel = OneBone();
            Quaternion x90 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
            Quaternion y90 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            Quaternion z90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

            Quaternion baseRot = z90;
            // Layer 1: reference 90X, sample 90X then 90Z, so d1 = 90Z. Layer 2: reference 90Y (a DIFFERENT
            // non-identity reference), sample 90Y then 90X, so d2 = 90X.
            Quaternion ref1 = x90, sample1 = Quaternion.Normalize(x90 * z90);
            Quaternion ref2 = y90, sample2 = Quaternion.Normalize(y90 * x90);

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(PoseClip("base", 0, Vector3.Zero, baseRot, Vector3.One), LayerMode.Override);
            AnimationLayer l1 = anim.AddLayer(AdditiveClip("add1", 0, ref1, sample1), LayerMode.Additive);
            AnimationLayer l2 = anim.AddLayer(AdditiveClip("add2", 0, ref2, sample2), LayerMode.Additive);
            l1.Time = 1f;
            l2.Time = 1f;

            Quaternion got = SampleLocals(anim, skel)[0].Rotation;

            Quaternion d1 = Quaternion.Normalize(Quaternion.Inverse(ref1) * sample1);
            Quaternion d2 = Quaternion.Normalize(Quaternion.Inverse(ref2) * sample2);
            Quaternion expected = Quaternion.Normalize(Quaternion.Normalize(baseRot * d1) * d2);
            // Stated independently: 90Z * 90Z * 90X is a 180 about Z followed by a 90 about X, which is a half turn
            // about the Y+Z diagonal, so the quaternion is (0, sin45, sin45, 0) up to sign.
            Assert.True(QuatClose(got, expected, 1e-4f), "two additive layers must compose base * d1 * d2");
            Assert.Equal(0f, MathF.Abs(got.X), 4);
            Assert.Equal(0.7071068f, MathF.Abs(got.Y), 4);
            Assert.Equal(0.7071068f, MathF.Abs(got.Z), 4);
            Assert.Equal(0f, MathF.Abs(got.W), 4);

            Quaternion swapped = Quaternion.Normalize(Quaternion.Normalize(baseRot * d2) * d1);
            Assert.False(QuatClose(got, swapped, 1e-4f), "must NOT be base * d2 * d1 (top layer nearest the base)");
        }

        // ---- helpers for the rows above ----

        // A constant translation + rotation track pair for one node, so a single clip can pose a whole chain.
        static JointTrack PosedTrack(int node, Vector3 t, Quaternion r) => new JointTrack(node)
        {
            Translation = new Vector3Track(new[] { 0f, 1f }, new[] { t, t }, InterpolationMode.Linear),
            Rotation = new QuaternionTrack(new[] { 0f, 1f }, new[] { r, r }, InterpolationMode.Linear),
        };

        static JointPose TrsPose(Vector3 t, Quaternion r) =>
            new JointPose { Translation = t, Rotation = r, Scale = Vector3.One };

        static bool MatClose(Matrix4x4 a, Matrix4x4 b, float eps = 1e-4f)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    if (MathF.Abs(a[row, col] - b[row, col]) > eps) return false;
            return true;
        }
    }
}
