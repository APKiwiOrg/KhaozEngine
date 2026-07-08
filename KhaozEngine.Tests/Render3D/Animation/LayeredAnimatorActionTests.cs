using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    // Headless numeric tests for the one-shot action lifecycle on LayeredAnimator: fade in, play once, fade out
    // overlapping the tail, auto-retire + allocation-free slot reuse, clean early cancel, and two simultaneous masked
    // actions composing. Pose math is pure CPU, so everything runs GPU-free. In the AllocSensitive collection so the
    // zero-alloc assertion is not taken while parallel tests churn the GC on other threads.
    [Collection("AllocSensitive")]
    public class LayeredAnimatorActionTests
    {
        // Flat rig: three independent roots so each composed bone WORLD transform equals its node LOCAL pose (read back
        // by decomposing the palette). Node 1 is masked as the "upper body", node 0/2 as the "lower body".
        static Skeleton Flat3()
        {
            var rest = new[] { JointPose.Identity, JointPose.Identity, JointPose.Identity };
            return new Skeleton(new[] { -1, -1, -1 }, rest, new[] { 0, 1, 2 }, new[] { 0, 1, 2 });
        }

        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        // Constant-translation clip on one node for its whole duration.
        static AnimationClip TranslationClip(string name, int node, Vector3 value, float duration = 1f)
        {
            var jt = new JointTrack(node)
            {
                Translation = new Vector3Track(new[] { 0f, duration }, new[] { value, value }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, duration, new List<JointTrack> { jt });
        }

        static JointPose[] Locals(LayeredAnimator anim)
        {
            Matrix4x4[] palette = anim.BonePalette();
            var outp = new JointPose[palette.Length];
            for (int i = 0; i < palette.Length; i++) outp[i] = JointPose.FromMatrix(palette[i]);
            return outp;
        }

        // Set a constant locomotion base on every node (X = value) via SetBaseLocals each frame the test drives. Uses a
        // reused scratch buffer so the helper does not allocate inside an alloc-sensitive measured loop.
        [ThreadStatic] static JointPose[]? _baseScratch;
        static void SetBase(LayeredAnimator anim, Skeleton skel, float x)
        {
            if (_baseScratch is null || _baseScratch.Length != skel.NodeCount) _baseScratch = new JointPose[skel.NodeCount];
            for (int n = 0; n < skel.NodeCount; n++)
                _baseScratch[n] = new JointPose { Translation = new Vector3(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            anim.SetBaseLocals(_baseScratch);
        }

        // ---- lifecycle: fade in, sustain at 1, fade out overlapping the tail, retire ----

        [Fact]
        public void PlayAction_FadesIn_Sustains_FadesOut_AndRetires()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            // Base at X=0, action drives X=10. The composited X reads the action's effective weight * 10.
            var action = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 1f);

            const float dt = 1f / 60f;
            ActionHandle h = anim.PlayAction(action, mask: null, fadeIn: 0.2f, fadeOut: 0.2f, speed: 1f);
            Assert.True(h.IsValid);
            Assert.True(anim.HasActiveActions);

            // Frame 0 (before any Update) is weight 0: pose == base.
            SetBase(anim, skel, 0f);
            Assert.Equal(0f, Locals(anim)[0].Translation.X, 4);

            // Drive to mid fade-in (~0.1s): weight ~0.5 -> X ~5.
            float t = 0f;
            for (; t < 0.1f - 1e-4f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            float xMidIn = Locals(anim)[0].Translation.X;
            Assert.InRange(xMidIn, 3f, 7f);

            // Drive into the sustain (~0.5s): fully weighted -> X == 10.
            for (; t < 0.5f - 1e-4f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            Assert.Equal(10f, Locals(anim)[0].Translation.X, 3);
            Assert.True(anim.HasActiveActions);

            // Drive past the clip end: fade-out completes at 1.0s, then the action retires -> pose back to base.
            for (; t < 1.2f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            Assert.False(anim.HasActiveActions);
            Assert.Equal(0f, Locals(anim)[0].Translation.X, 4);
        }

        [Fact]
        public void PlayAction_FadeOut_OverlapsTheClipTail_WeightBelowOneBeforeEnd()
        {
            // The fade-out must OVERLAP the clip tail (start before the clip ends), not begin after it. With a 1s clip
            // and 0.3s fade-out, weight must already be below 1 at t=0.85s (inside the clip).
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var action = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 1f);
            anim.PlayAction(action, fadeIn: 0.1f, fadeOut: 0.3f);

            const float dt = 1f / 120f;
            for (float t = 0f; t < 0.85f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            float x = Locals(anim)[0].Translation.X;
            Assert.True(x < 9.5f, $"expected fade-out already underway inside the clip, got X={x}");
            Assert.True(x > 0.5f, "but not yet fully faded");
        }

        // ---- auto-retire frees the slot: N sequential actions reuse ONE slot, allocation-free ----

        [Fact]
        public void SequentialActions_ReuseOneSlot_AllocationFree()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var action = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 0.5f);
            const float dt = 1f / 60f;

            // Play one full action to force the slot + all lazy buffers into existence (warm-up).
            RunOneAction(anim, skel, action, dt);
            Assert.False(anim.HasActiveActions);
            int layersAfterFirst = anim.LayerCount;

            // Now play several more, each start-to-retire. The layer count must NOT grow (slot reused), and the steady
            // state must allocate nothing (PlayAction on a reused slot + the compositing frames).
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6; i++) RunOneAction(anim, skel, action, dt);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(layersAfterFirst, anim.LayerCount);   // one slot reused across all six actions
            Assert.True(allocated == 0, $"sequential actions allocated {allocated} bytes (slot not reused?)");
        }

        static void RunOneAction(LayeredAnimator anim, Skeleton skel, AnimationClip clip, float dt)
        {
            anim.PlayAction(clip, fadeIn: 0.05f, fadeOut: 0.05f);
            // A 0.5s clip retires by ~0.5s; drive 0.7s to be safe.
            for (float t = 0f; t < 0.7f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
        }

        // ---- cancel fades cleanly: no pose pop at the cancel instant ----

        [Fact]
        public void Cancel_FadesFromCurrentWeight_NoPop_ThenRetires()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var action = TranslationClip("hold", 0, new Vector3(10, 0, 0), duration: 5f);   // long clip so we cancel mid-sustain
            const float dt = 1f / 60f;

            ActionHandle h = anim.PlayAction(action, fadeIn: 0.2f, fadeOut: 0.3f);
            // Drive well into the sustain (weight 1, X == 10).
            for (float t = 0f; t < 1f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            float beforeCancel = Locals(anim)[0].Translation.X;
            Assert.Equal(10f, beforeCancel, 3);

            Assert.True(anim.Cancel(h));   // helper below wraps Cancel

            // The frame immediately after cancel must NOT pop: continuity within one small step.
            anim.Update(dt); SetBase(anim, skel, 0f);
            float justAfter = Locals(anim)[0].Translation.X;
            Assert.True(MathF.Abs(justAfter - beforeCancel) < 2f, $"pop at cancel: {beforeCancel} -> {justAfter}");

            // Monotone, pop-free decay to base over the cancel fade-out, then retire.
            float prev = justAfter;
            for (float t = 0f; t < 0.4f; t += dt)
            {
                anim.Update(dt); SetBase(anim, skel, 0f);
                float x = Locals(anim)[0].Translation.X;
                Assert.True(x <= prev + 1e-3f, $"cancel fade not monotone: {prev} -> {x}");
                Assert.True(MathF.Abs(x - prev) < 2f, "no pop mid-cancel");
                prev = x;
            }
            Assert.False(anim.HasActiveActions);
            Assert.Equal(0f, Locals(anim)[0].Translation.X, 4);
        }

        [Fact]
        public void Cancel_StaleHandle_IsNoOp()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var action = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 0.3f);
            const float dt = 1f / 60f;

            ActionHandle h = anim.PlayAction(action, fadeIn: 0.05f, fadeOut: 0.05f);
            for (float t = 0f; t < 0.6f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }   // let it retire
            Assert.False(anim.HasActiveActions);
            Assert.False(anim.Cancel(h));   // stale: already retired
            Assert.False(anim.Cancel(default));   // defaulted handle
        }

        // ---- two simultaneous actions on different masks compose ----

        [Fact]
        public void TwoSimultaneousActions_OnDifferentMasks_Compose()
        {
            Skeleton skel = Flat3();
            var anim = new LayeredAnimator(skel);
            // Action A drives node 0 to X=10, masked to node 0 only. Action B drives node 2 to X=20, masked to node 2.
            var actionA = TranslationClip("a", 0, new Vector3(10, 0, 0), duration: 2f);
            var actionB = TranslationClip("b", 2, new Vector3(20, 0, 0), duration: 2f);
            var maskA = new BoneMask(new[] { 1f, 0f, 0f });
            var maskB = new BoneMask(new[] { 0f, 0f, 1f });
            const float dt = 1f / 60f;

            anim.PlayAction(actionA, maskA, fadeIn: 0.1f, fadeOut: 0.1f);
            anim.PlayAction(actionB, maskB, fadeIn: 0.1f, fadeOut: 0.1f);

            // Base at X=1 on every node. Drive into both sustains.
            for (float t = 0f; t < 0.6f; t += dt) { anim.Update(dt); SetBase(anim, skel, 1f); }

            JointPose[] locals = Locals(anim);
            Assert.Equal(10f, locals[0].Translation.X, 2);   // node 0: action A
            Assert.Equal(1f, locals[1].Translation.X, 2);    // node 1: untouched -> base
            Assert.Equal(20f, locals[2].Translation.X, 2);   // node 2: action B
            Assert.Equal(2, ActiveActionCount(anim));
        }

        static int ActiveActionCount(LayeredAnimator anim) => anim.HasActiveActions ? CountNonZeroLayers(anim) : 0;

        static int CountNonZeroLayers(LayeredAnimator anim)
        {
            int c = 0;
            for (int i = 0; i < anim.Layers.Count; i++) if (anim.Layers[i].Weight > 0f) c++;
            return c;
        }
    }
}
