using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    // Headless numeric tests for the layer compositor: BoneMask subtree builder, override + additive composition,
    // shortest-arc double-cover, zero/full-layer bit-identity, weight-ramp continuity, and determinism. Pose math is
    // pure CPU, so everything runs GPU-free (no device). In the AllocSensitive collection so the zero-alloc assertion
    // is not taken while the parallel-ForEach tests churn the GC on other threads.
    [Collection("AllocSensitive")]
    public class LayeredAnimatorTests
    {
        // A three-node chain: node 0 root, node 1 child of 0, node 2 child of 1 (topological, parent < child).
        // One bone per node so a composed palette reads each node's WORLD transform directly (root == local).
        static Skeleton Chain3()
        {
            var rest = new[] { JointPose.Identity, JointPose.Identity, JointPose.Identity };
            return new Skeleton(new[] { -1, 0, 1 }, rest, new[] { 0, 1, 2 }, new[] { 0, 1, 2 });
        }

        // Single-node skeleton (matches AnimationPlayerTests.OneBone) for the bit-identity vs single-clip check.
        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        // Three INDEPENDENT root nodes (no parents): the composed WORLD transform of each node equals its LOCAL pose,
        // so SampleLocals reads each node's local TRS back directly. Node 1 is still a valid subtree root over {1,2}
        // for the subtree mask builder ONLY when it uses parent links (Chain3); for composition assertions we want the
        // world==local property, so the tests that decompose the palette use this flat rig and mask by explicit weights.
        static Skeleton Flat3()
        {
            var rest = new[] { JointPose.Identity, JointPose.Identity, JointPose.Identity };
            return new Skeleton(new[] { -1, -1, -1 }, rest, new[] { 0, 1, 2 }, new[] { 0, 1, 2 });
        }

        // Clip that sets a constant TRS on one node for its whole duration.
        static AnimationClip PoseClip(string name, int node, Vector3 t, Quaternion r, Vector3 s, float duration = 1f)
        {
            var jt = new JointTrack(targetNode: node)
            {
                Translation = new Vector3Track(new[] { 0f, duration }, new[] { t, t }, InterpolationMode.Linear),
                Rotation = new QuaternionTrack(new[] { 0f, duration }, new[] { r, r }, InterpolationMode.Linear),
                Scale = new Vector3Track(new[] { 0f, duration }, new[] { s, s }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, duration, new List<JointTrack> { jt });
        }

        static AnimationClip TranslationClip(string name, int node, Vector3 value, float duration = 1f) =>
            PoseClip(name, node, value, Quaternion.Identity, Vector3.One, duration);

        static bool PoseClose(in JointPose a, in JointPose b, float eps = 1e-5f) =>
            Vector3.Distance(a.Translation, b.Translation) < eps
            && Vector3.Distance(a.Scale, b.Scale) < eps
            && QuatClose(a.Rotation, b.Rotation, eps);

        static bool QuatClose(Quaternion a, Quaternion b, float eps)
        {
            // Double-cover aware: q and -q are the same rotation.
            float d = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));
            return d > 1f - eps;
        }

        // ---- BoneMask subtree builder ----

        [Fact]
        public void Subtree_MarksRootAndAllDescendants_RestZero()
        {
            Skeleton skel = Chain3();
            BoneMask m = BoneMask.Subtree(skel, root: 1, weight: 1f);
            Assert.Equal(0f, m.Weight(0));   // root's parent, outside subtree
            Assert.Equal(1f, m.Weight(1));   // the subtree root
            Assert.Equal(1f, m.Weight(2));   // descendant
        }

        [Fact]
        public void Subtree_ByName_ResolvesNode()
        {
            Skeleton skel = Chain3();
            var names = new[] { "root", "spine", "arm" };
            BoneMask m = BoneMask.Subtree(skel, "spine", names, 0.5f);
            Assert.Equal(0f, m.Weight(0));
            Assert.Equal(0.5f, m.Weight(1));
            Assert.Equal(0.5f, m.Weight(2));
        }

        [Fact]
        public void Subtree_UnknownBoneName_Throws() =>
            Assert.Throws<ArgumentException>(() =>
                BoneMask.Subtree(Chain3(), "nope", new[] { "root", "spine", "arm" }, 1f));

        [Fact]
        public void Full_And_Empty_Constants()
        {
            Skeleton skel = Chain3();
            BoneMask full = BoneMask.Full(skel);
            BoneMask empty = BoneMask.Empty(skel);
            for (int n = 0; n < skel.NodeCount; n++)
            {
                Assert.Equal(1f, full.Weight(n));
                Assert.Equal(0f, empty.Weight(n));
            }
        }

        [Fact]
        public void Subtree_WeightClampedTo01()
        {
            BoneMask m = BoneMask.Subtree(Chain3(), 0, 5f);
            Assert.Equal(1f, m.Weight(0));
        }

        // ---- Zero / single-full-layer bit-identity (the byte-stability guarantee) ----

        [Fact]
        public void ZeroLayers_IsRestPose()
        {
            Skeleton skel = Chain3();
            var anim = new LayeredAnimator(skel);
            Matrix4x4[] got = anim.BonePalette();
            Matrix4x4[] rest = skel.ComposeRestPose();
            for (int b = 0; b < got.Length; b++) Assert.Equal(rest[b], got[b]);
        }

        [Fact]
        public void SingleFullOverrideLayer_BitIdenticalToSingleClipPath()
        {
            // The compositor with one full-weight, unmasked Override layer must produce the EXACT bytes of the
            // single-clip sample+compose path (what AnimationPlayer/AnimatedCharacter produce today).
            Skeleton skel = Chain3();
            var clip = PoseClip("run", node: 1, new Vector3(0.3f, 1.1f, -0.7f),
                Quaternion.CreateFromYawPitchRoll(0.4f, -0.2f, 0.9f), new Vector3(1.2f, 0.8f, 1f), duration: 2f);

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(clip, LayerMode.Override);   // full weight, no mask
            anim.Update(0.37f);
            anim.Update(0.51f);
            Matrix4x4[] layered = anim.BonePalette();

            // Reference: the exact single-clip path at the same playhead time (0.88s).
            JointPose[] local = AnimationSampler.SamplePose(clip, skel, 0.88f);
            var reference = new Matrix4x4[skel.BoneCount];
            skel.ComposeInto(local, reference);

            for (int b = 0; b < layered.Length; b++)
                Assert.Equal(reference[b], layered[b]);   // exact bytes, not approximate
        }

        [Fact]
        public void SingleFullOverrideLayer_MatchesAnimationPlayer_OneBone()
        {
            // Cross-check against the actual AnimationPlayer on the same one-bone rig used by AnimationPlayerTests.
            Skeleton skel = OneBone();
            var clip = TranslationClip("a", node: 0, new Vector3(3, -2, 5), duration: 100f);

            var player = new AnimationPlayer(skel);
            player.Play(clip, crossfade: 0f);
            player.Update(0.42f);
            Matrix4x4[] fromPlayer = player.BonePalette();

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(clip, LayerMode.Override);
            anim.Update(0.42f);
            Matrix4x4[] fromLayers = anim.BonePalette();

            Assert.Equal(fromPlayer[0], fromLayers[0]);
        }

        // ---- Override composition (masked bones follow the action, unmasked the base, 0.5 blends halfway) ----

        [Fact]
        public void Override_MaskedBonesFollowAction_UnmaskedFollowBase()
        {
            // Flat rig so world == local per node. Base drives all three nodes to (1,0,0). A mask that is 0 on node 0
            // and 1 on nodes 1+2 gates the action (which poses node 1 to (0,10,0), leaving node 2 at rest identity).
            Skeleton skel = Flat3();
            var baseClip = new AnimationClip("base", 1f, new List<JointTrack>
            {
                Track(0, new Vector3(1, 0, 0)), Track(1, new Vector3(1, 0, 0)), Track(2, new Vector3(1, 0, 0)),
            });
            var action = PoseClip("action", node: 1, new Vector3(0, 10, 0), Quaternion.Identity, Vector3.One);
            var mask = new BoneMask(new[] { 0f, 1f, 1f });

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            anim.AddLayer(action, LayerMode.Override, mask: mask);

            JointPose[] locals = SampleLocals(anim, skel);
            Assert.True(PoseClose(locals[0], Pose(new Vector3(1, 0, 0))));    // unmasked: base
            Assert.True(PoseClose(locals[1], Pose(new Vector3(0, 10, 0))));   // masked: action poses node 1
            // node 2 is masked full-weight, but the action clip does not pose node 2 -> it samples rest identity, which
            // the full-weight override copies in over the base's (1,0,0). So masked-but-unposed reads rest, not base.
            Assert.True(PoseClose(locals[2], JointPose.Identity));
        }

        [Fact]
        public void Override_Weight0p5_BlendsHalfway()
        {
            Skeleton skel = OneBone();
            var baseClip = TranslationClip("base", 0, new Vector3(0, 0, 0));
            var action = TranslationClip("action", 0, new Vector3(4, 0, 0));

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            anim.AddLayer(action, LayerMode.Override, weight: 0.5f);

            JointPose[] locals = SampleLocals(anim, skel);
            Assert.Equal(2f, locals[0].Translation.X, 4);   // halfway between 0 and 4
        }

        [Fact]
        public void Override_MaskWeight_MultipliesLayerWeight()
        {
            Skeleton skel = OneBone();
            var baseClip = TranslationClip("base", 0, Vector3.Zero);
            var action = TranslationClip("action", 0, new Vector3(4, 0, 0));
            // layer weight 1, mask weight 0.25 -> effective 0.25 -> X == 1.
            var mask = new BoneMask(new[] { 0.25f });

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            anim.AddLayer(action, LayerMode.Override, mask: mask);

            Assert.Equal(1f, SampleLocals(anim, skel)[0].Translation.X, 4);
        }

        [Fact]
        public void Override_Mask0Bone_ByteIdenticalToBasePose_WGuardHolds()
        {
            // The w <= 0 continue guard must leave a mask-0 node BYTE-identical to the base pose (not a lerp toward it
            // at weight 0, which could re-normalize and drift a bit). Lock it with exact Matrix4x4 equality.
            Skeleton skel = Flat3();
            var baseClip = new AnimationClip("base", 1f, new List<JointTrack>
            {
                Track(0, new Vector3(1, 0, 0)), Track(1, new Vector3(1, 0, 0)), Track(2, new Vector3(1, 0, 0)),
            });
            // Action poses node 0 (a rotation, so a naive weight-0 lerp/slerp would touch the quaternion), but the mask
            // is 0 on node 0 -> the guard must skip it entirely and keep node 0's base bytes.
            var action = PoseClip("action", node: 0, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.9f), Vector3.One);
            var mask = new BoneMask(new[] { 0f, 1f, 1f });   // node 0 masked OUT

            // Base-only palette (the exact bytes node 0 must retain).
            var baseOnly = new LayeredAnimator(skel);
            baseOnly.AddLayer(baseClip, LayerMode.Override);
            Matrix4x4[] baseBytes = baseOnly.BonePalette();

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            anim.AddLayer(action, LayerMode.Override, mask: mask);
            Matrix4x4[] withAction = anim.BonePalette();

            Assert.Equal(baseBytes[0], withAction[0]);   // exact bytes: mask-0 node untouched by the upper layer
        }

        // ---- Additive composition (delta from reference, verified against hand-computed pose) ----

        [Fact]
        public void Additive_TranslationDelta_AddsSampleMinusReference()
        {
            Skeleton skel = OneBone();
            // Base at (10,0,0). Additive clip: reference frame (t=0) is (2,0,0), current sample also (2,0,0) since it
            // is a constant clip -> delta 0 -> base unchanged. Make it a moving clip to get a non-zero delta.
            var baseClip = TranslationClip("base", 0, new Vector3(10, 0, 0));
            // additive clip: t=0 -> (2,0,0), t=1 -> (5,0,0). At time 0.5 -> (3.5,0,0). delta = 3.5 - 2 = 1.5.
            var add = new AnimationClip("add", 1f, new List<JointTrack>
            {
                new JointTrack(0)
                {
                    Translation = new Vector3Track(new[] { 0f, 1f },
                        new[] { new Vector3(2, 0, 0), new Vector3(5, 0, 0) }, InterpolationMode.Linear),
                },
            });

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer addLayer = anim.AddLayer(add, LayerMode.Additive);
            addLayer.Time = 0.5f;   // sample at (3.5,0,0), reference (2,0,0), delta 1.5

            JointPose[] locals = SampleLocals(anim, skel);
            Assert.Equal(11.5f, locals[0].Translation.X, 4);   // 10 + 1.5
        }

        [Fact]
        public void Additive_HalfWeight_ScalesDelta()
        {
            Skeleton skel = OneBone();
            var baseClip = TranslationClip("base", 0, new Vector3(10, 0, 0));
            var add = new AnimationClip("add", 1f, new List<JointTrack>
            {
                new JointTrack(0)
                {
                    Translation = new Vector3Track(new[] { 0f, 1f },
                        new[] { new Vector3(2, 0, 0), new Vector3(5, 0, 0) }, InterpolationMode.Linear),
                },
            });

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer addLayer = anim.AddLayer(add, LayerMode.Additive, weight: 0.5f);
            addLayer.Time = 0.5f;   // delta 1.5 * 0.5 = 0.75

            Assert.Equal(10.75f, SampleLocals(anim, skel)[0].Translation.X, 4);
        }

        [Fact]
        public void Additive_RotationDelta_SameAxis_Accumulates()
        {
            // Same-axis (COMMUTING) case: base 90Y, delta 90Y -> 180Y. This proves the delta accumulates but says
            // NOTHING about operand order (Y*Y commutes); the local-frame order is pinned by the X/Y test below.
            Skeleton skel = OneBone();
            Quaternion baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            Quaternion sampleRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            var baseClip = PoseClip("base", 0, Vector3.Zero, baseRot, Vector3.One);
            var add = PoseClip("add", 0, Vector3.Zero, sampleRot, Vector3.One);   // reference (t=0) == sampleRot too

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            anim.AddLayer(add, LayerMode.Additive);

            // Constant additive clip: reference == sample -> delta identity -> base unchanged.
            Assert.True(QuatClose(SampleLocals(anim, skel)[0].Rotation, baseRot, 1e-5f));

            // Now a MOVING additive rotation: reference identity at t=0, 90Y at t=1. At t=1 delta = 90Y, result 180Y.
            var addMoving = new AnimationClip("addm", 1f, new List<JointTrack>
            {
                new JointTrack(0)
                {
                    Rotation = new QuaternionTrack(new[] { 0f, 1f },
                        new[] { Quaternion.Identity, sampleRot }, InterpolationMode.Linear),
                },
            });
            var anim2 = new LayeredAnimator(skel);
            anim2.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer al = anim2.AddLayer(addMoving, LayerMode.Additive);
            al.Time = 1f;
            Quaternion expected = Quaternion.Normalize(sampleRot * baseRot);   // 180Y (Y*Y commutes, so order is moot)
            Assert.True(QuatClose(SampleLocals(anim2, skel)[0].Rotation, expected, 1e-4f));
        }

        [Fact]
        public void Additive_RotationDelta_AppliesInLocalFrame_BaseTimesDelta_NotDeltaTimesBase()
        {
            // ORDER PIN with NON-COMMUTING axes: base 90 about X, additive delta 90 about Y, full weight. The
            // local-frame convention is result = base * delta; delta * base is 120deg away for these rotations, so
            // this case distinguishes the two unambiguously. (The same-axis test above cannot - Y*Y commutes.)
            Skeleton skel = OneBone();
            Quaternion baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);   // 90 about X
            Quaternion deltaRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);   // 90 about Y

            // Additive clip: reference (t=0) identity, sample 90Y at t=1 -> delta = 90Y.
            var baseClip = PoseClip("base", 0, Vector3.Zero, baseRot, Vector3.One);
            var add = new AnimationClip("add", 1f, new List<JointTrack>
            {
                new JointTrack(0)
                {
                    Rotation = new QuaternionTrack(new[] { 0f, 1f },
                        new[] { Quaternion.Identity, deltaRot }, InterpolationMode.Linear),
                },
            });

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer al = anim.AddLayer(add, LayerMode.Additive);
            al.Time = 1f;   // full delta

            Quaternion got = SampleLocals(anim, skel)[0].Rotation;

            Quaternion localFrame = Quaternion.Normalize(baseRot * deltaRot);   // the convention we ship
            Quaternion parentFrame = Quaternion.Normalize(deltaRot * baseRot);  // the rejected order

            Assert.True(QuatClose(got, localFrame, 1e-4f), "additive rotation must compose as base * delta (local frame)");
            Assert.False(QuatClose(got, parentFrame, 1e-4f), "must NOT compose as delta * base (parent frame)");
        }

        [Fact]
        public void Additive_RotationDelta_HalfWeight_AppliesInLocalFrame()
        {
            // Half-weight order pin: at w=0.5 the applied delta is Slerp(Identity, delta, 0.5), and it must still
            // compose on the RIGHT of the base: result = base * Slerp(Identity, delta, 0.5).
            Skeleton skel = OneBone();
            Quaternion baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);   // 90 about X
            Quaternion deltaRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);   // 90 about Y

            var baseClip = PoseClip("base", 0, Vector3.Zero, baseRot, Vector3.One);
            var add = new AnimationClip("add", 1f, new List<JointTrack>
            {
                new JointTrack(0)
                {
                    Rotation = new QuaternionTrack(new[] { 0f, 1f },
                        new[] { Quaternion.Identity, deltaRot }, InterpolationMode.Linear),
                },
            });

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer al = anim.AddLayer(add, LayerMode.Additive, weight: 0.5f);
            al.Time = 1f;   // full delta from the clip, scaled to half by the layer weight

            Quaternion got = SampleLocals(anim, skel)[0].Rotation;

            Quaternion partial = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, deltaRot, 0.5f));
            Quaternion expected = Quaternion.Normalize(baseRot * partial);
            Quaternion rejected = Quaternion.Normalize(partial * baseRot);

            Assert.True(QuatClose(got, expected, 1e-4f), "half-weight additive must be base * Slerp(Identity, delta, 0.5)");
            Assert.False(QuatClose(got, rejected, 1e-4f), "half-weight must NOT be Slerp(...) * base");
        }

        // ---- Shortest-arc double-cover pin ----

        [Fact]
        public void Override_ShortestArc_NegatedQuaternionBlendsTheShortWay()
        {
            Skeleton skel = OneBone();
            // Two rotations 20deg apart about Y, but the target expressed with a NEGATED quaternion (double cover).
            // A naive nlerp without the shortest-arc negation would blend the LONG way (340deg). Slerp negates.
            Quaternion a = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 10f / 180f);
            Quaternion b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 30f / 180f);
            Quaternion bNeg = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);   // same rotation, opposite hemisphere

            var baseClip = PoseClip("base", 0, Vector3.Zero, a, Vector3.One);
            var action = PoseClip("action", 0, Vector3.Zero, bNeg, Vector3.One);

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            anim.AddLayer(action, LayerMode.Override, weight: 0.5f);

            // Halfway the SHORT way is 20deg about Y. The long way would be ~200deg.
            Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 20f / 180f);
            Assert.True(QuatClose(SampleLocals(anim, skel)[0].Rotation, expected, 1e-4f));
        }

        [Fact]
        public void Additive_ShortestArc_NegatedReferenceStillCorrect()
        {
            Skeleton skel = OneBone();
            Quaternion baseRot = Quaternion.Identity;
            Quaternion sampleRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3f);   // 60deg
            Quaternion refNeg = Quaternion.Identity;
            refNeg = new Quaternion(-refNeg.X, -refNeg.Y, -refNeg.Z, -refNeg.W);   // -identity, same rotation

            var baseClip = PoseClip("base", 0, Vector3.Zero, baseRot, Vector3.One);
            var add = new AnimationClip("add", 1f, new List<JointTrack>
            {
                new JointTrack(0)
                {
                    Rotation = new QuaternionTrack(new[] { 0f, 1f },
                        new[] { refNeg, sampleRot }, InterpolationMode.Linear),
                },
            });
            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer al = anim.AddLayer(add, LayerMode.Additive);
            al.Time = 1f;   // delta = sample * inverse(-identity) = 60Y regardless of the reference's hemisphere

            Assert.True(QuatClose(SampleLocals(anim, skel)[0].Rotation, sampleRot, 1e-4f));
        }

        // ---- Weight ramp continuity (no pose pop as a layer fades to 0 and retires) ----

        [Fact]
        public void WeightRamp_ToZero_ApproachesBaseContinuously_NoPop()
        {
            Skeleton skel = OneBone();
            var baseClip = TranslationClip("base", 0, Vector3.Zero);
            var action = TranslationClip("action", 0, new Vector3(10, 0, 0));

            var anim = new LayeredAnimator(skel);
            anim.AddLayer(baseClip, LayerMode.Override);
            AnimationLayer al = anim.AddLayer(action, LayerMode.Override);

            float prevX = SampleLocals(anim, skel)[0].Translation.X;   // weight 1 -> X == 10
            for (float w = 1f; w >= 0f; w -= 0.05f)
            {
                al.Weight = w;
                float x = SampleLocals(anim, skel)[0].Translation.X;
                Assert.True(MathF.Abs(x - prevX) <= 0.6f, $"pop at weight {w}: {prevX} -> {x}");
                prevX = x;
            }
            // At weight 0 the layer is fully faded: the pose equals the base exactly, and removing it changes nothing.
            al.Weight = 0f;
            float faded = SampleLocals(anim, skel)[0].Translation.X;
            anim.RemoveLayer(al);
            float retired = SampleLocals(anim, skel)[0].Translation.X;
            Assert.Equal(0f, faded, 5);
            Assert.Equal(faded, retired, 5);   // retiring a zero-weight layer does not pop
        }

        // ---- Determinism ----

        [Fact]
        public void Determinism_SameInputsSamePoses()
        {
            Skeleton skel = Chain3();
            Matrix4x4[] Run()
            {
                var anim = new LayeredAnimator(skel);
                anim.AddLayer(PoseClip("base", 1, new Vector3(1, 2, 3),
                    Quaternion.CreateFromYawPitchRoll(0.3f, 0.1f, -0.2f), new Vector3(1.1f, 1f, 0.9f)), LayerMode.Override);
                anim.AddLayer(PoseClip("act", 1, new Vector3(0, 5, 0),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f), Vector3.One),
                    LayerMode.Additive, mask: BoneMask.Subtree(skel, 1, 0.8f));
                for (int i = 0; i < 20; i++) anim.Update(1f / 60f);
                return anim.BonePalette();
            }
            Matrix4x4[] a = Run();
            Matrix4x4[] b = Run();
            for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);   // exact
        }

        // ---- Allocation discipline ----

        [Fact]
        public void SteadyState_UpdateAndGetBonePalette_AllocationFree()
        {
            Skeleton skel = Chain3();
            var anim = new LayeredAnimator(skel);
            anim.AddLayer(PoseClip("base", 1, new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One), LayerMode.Override);
            anim.AddLayer(PoseClip("act", 1, new Vector3(0, 1, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), Vector3.One),
                LayerMode.Additive, mask: BoneMask.Subtree(skel, 1, 1f));
            var palette = new Matrix4x4[skel.BoneCount];

            // Warm up (JIT, lazy additive-reference sample, dictionary build).
            for (int i = 0; i < 8; i++) { anim.Update(1f / 60f); anim.GetBonePalette(palette); }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 240; i++) { anim.Update(1f / 60f); anim.GetBonePalette(palette); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated == 0, $"steady-state Update+GetBonePalette allocated {allocated} bytes");
        }

        // ---- helpers ----

        static JointTrack Track(int node, Vector3 t) => new JointTrack(node)
        {
            Translation = new Vector3Track(new[] { 0f, 1f }, new[] { t, t }, InterpolationMode.Linear),
        };

        static JointPose Pose(Vector3 t) => new JointPose { Translation = t, Rotation = Quaternion.Identity, Scale = Vector3.One };

        // Compose the animator, then decompose each bone-0-aligned node back to a local pose for numeric checks.
        // Chain/OneBone use one bone per node with identity parents at the root, so bone[i] world == node[i] world;
        // for the single-node and root cases world == local, which is what these assertions read.
        static JointPose[] SampleLocals(LayeredAnimator anim, Skeleton skel)
        {
            Matrix4x4[] palette = anim.BonePalette();
            var outp = new JointPose[palette.Length];
            for (int i = 0; i < palette.Length; i++) outp[i] = JointPose.FromMatrix(palette[i]);
            return outp;
        }
    }
}
