using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests;
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
        public void PlayAction_Speed2_HalvesPlayDuration_FadesAndRetiresOnRealTime()
        {
            // speed 2 halves the play duration (clip.Duration / speed), but the fades stay wall-clock: a 1s clip at
            // speed 2 plays for 0.5s real, sustains at full weight mid-clip, starts its fade-out at (0.5 - fadeOut)
            // real seconds, and retires at 0.5s real. Mirrors the speed-1 lifecycle test above, on the halved timeline.
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var action = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 1f);

            const float dt = 1f / 120f;
            const float fadeIn = 0.05f, fadeOut = 0.1f;
            const float playDuration = 0.5f;                 // 1s clip / speed 2
            const float fadeOutStart = playDuration - fadeOut;   // 0.4s real
            anim.PlayAction(action, mask: null, fadeIn: fadeIn, fadeOut: fadeOut, speed: 2f);
            Assert.True(anim.HasActiveActions);

            // Mid-clip, after fade-in and before fade-out starts (~0.25s real): sustained at full weight -> X == 10.
            float t = 0f;
            for (; t < 0.25f - 1e-4f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            Assert.Equal(10f, Locals(anim)[0].Translation.X, 3);
            Assert.True(anim.HasActiveActions);

            // Just before the fade-out start (~0.39s real): still full weight.
            for (; t < fadeOutStart - 0.01f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            Assert.Equal(10f, Locals(anim)[0].Translation.X, 3);

            // Inside the fade-out band (~0.45s real, between fadeOutStart and playDuration): weight already below 1.
            for (; t < 0.45f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            float xFading = Locals(anim)[0].Translation.X;
            Assert.True(xFading < 9.5f, $"expected fade-out underway by 0.45s real, got X={xFading}");
            Assert.True(xFading > 0.5f, "but not yet fully faded");
            Assert.True(anim.HasActiveActions);

            // Past the halved play duration (0.5s real, +margin): retired -> pose back to base.
            for (; t < 0.6f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
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
            // state must allocate nothing (PlayAction on a reused slot + the compositing frames). Retries once
            // before failing (see AllocAssert.NoPerCallAllocation) to ride out an unrelated gen-0 collision from
            // the rest of the process, per issue #284.
            AllocAssert.NoPerCallAllocation("sequential actions (slot not reused?)", () =>
            {
                for (int i = 0; i < 6; i++) RunOneAction(anim, skel, action, dt);
            });

            Assert.Equal(layersAfterFirst, anim.LayerCount);   // one slot reused across all six actions
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

        // ---- held (persistent) actions: hold:true suppresses the auto fade-out so the slot loops at weight 1 ----

        [Fact]
        public void HeldAction_SustainsPastClipDuration_LoopsAtFullWeight()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var held = TranslationClip("hold", 0, new Vector3(10, 0, 0), duration: 1f);
            const float dt = 1f / 60f;

            anim.PlayAction(held, mask: null, fadeIn: 0.1f, fadeOut: 0.1f, hold: true);

            // Drive well past the 1s clip (2.5 clips). A one-shot would have faded out and retired by ~1s; a held
            // action must still be live, at full weight, with its playhead LOOPED back within [0, duration).
            for (float t = 0f; t < 2.5f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }

            Assert.True(anim.HasActiveActions);
            Assert.Equal(10f, Locals(anim)[0].Translation.X, 3);   // sustained at full weight, not faded to base

            AnimationLayer active = ActiveLayer(anim);
            Assert.Equal(1f, active.Weight, 5);                    // held at weight 1
            Assert.True(active.Time >= 0f && active.Time < held.Duration,
                $"playhead should have wrapped (looped), got Time={active.Time} for duration {held.Duration}");
        }

        [Fact]
        public void AttackAboveHeldAction_Wins_ThenFallsBackToHold_LegsUntouched()
        {
            Skeleton skel = Flat3();
            var anim = new LayeredAnimator(skel);
            // Node 1 = "arm": a held idle pose drives it to X=5; an attack drives it to X=20. Nodes 0/2 = "legs": base.
            var heldArm = TranslationClip("armIdle", 1, new Vector3(5, 0, 0), duration: 1f);
            var attack = TranslationClip("armAttack", 1, new Vector3(20, 0, 0), duration: 0.5f);
            var armMask = new BoneMask(new[] { 0f, 1f, 0f });
            const float dt = 1f / 60f;

            // Hold the arm idle first (acquired first -> lower slot index), over a locomotion base at X=1 on every node.
            anim.PlayAction(heldArm, armMask, fadeIn: 0.1f, fadeOut: 0.1f, hold: true);
            for (float t = 0f; t < 1.4f; t += dt) { anim.Update(dt); SetBase(anim, skel, 1f); }   // past the clip: held
            JointPose[] held = Locals(anim);
            Assert.Equal(5f, held[1].Translation.X, 2);    // arm holds the idle pose
            Assert.Equal(1f, held[0].Translation.X, 2);    // legs on the locomotion base
            Assert.Equal(1f, held[2].Translation.X, 2);

            // Now swing: the attack (acquired second -> higher slot index) wins on the arm while the hold persists below.
            anim.PlayAction(attack, armMask, fadeIn: 0.05f, fadeOut: 0.05f, hold: false);
            for (float t = 0f; t < 0.2f; t += dt) { anim.Update(dt); SetBase(anim, skel, 1f); }
            Assert.Equal(20f, Locals(anim)[1].Translation.X, 1);   // attack composites over the held pose
            Assert.Equal(1f, Locals(anim)[0].Translation.X, 2);    // legs still untouched

            // Attack plays out and retires; the arm falls back to the still-held idle pose (X=5), hold still live.
            for (float t = 0f; t < 0.6f; t += dt) { anim.Update(dt); SetBase(anim, skel, 1f); }
            Assert.True(anim.HasActiveActions);                    // the hold remains after the attack retired
            Assert.Equal(5f, Locals(anim)[1].Translation.X, 2);    // seamless fallback to the held pose
            Assert.Equal(1f, Locals(anim)[0].Translation.X, 2);    // legs untouched throughout
        }

        [Fact]
        public void Cancel_HeldAction_PastClipDuration_FadesCleanly_ThenRetires()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var held = TranslationClip("hold", 0, new Vector3(10, 0, 0), duration: 1f);
            const float dt = 1f / 60f;

            ActionHandle h = anim.PlayAction(held, fadeIn: 0.1f, fadeOut: 0.2f, hold: true);
            // Drive PAST the clip duration into the held sustain (2 clips): weight 1, X=10, still live.
            for (float t = 0f; t < 2f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            float before = Locals(anim)[0].Translation.X;
            Assert.Equal(10f, before, 3);
            Assert.True(anim.HasActiveActions);

            Assert.True(anim.Cancel(h));

            // The frame immediately after cancel must NOT pop: continuity from the held weight.
            anim.Update(dt); SetBase(anim, skel, 0f);
            float justAfter = Locals(anim)[0].Translation.X;
            Assert.True(MathF.Abs(justAfter - before) < 2f, $"pop at cancel: {before} -> {justAfter}");

            // Monotone, pop-free decay to base over the cancel fade-out, then retire.
            float prev = justAfter;
            for (float t = 0f; t < 0.3f; t += dt)
            {
                anim.Update(dt); SetBase(anim, skel, 0f);
                float x = Locals(anim)[0].Translation.X;
                Assert.True(x <= prev + 1e-3f, $"cancel fade not monotone: {prev} -> {x}");
                prev = x;
            }
            Assert.False(anim.HasActiveActions);
            Assert.Equal(0f, Locals(anim)[0].Translation.X, 4);
        }

        [Fact]
        public void NoHoldAction_ByteIdenticalToHeld_UntilFadeOut_ThenNoHoldRetires()
        {
            // hold changes NOTHING until the one-shot's fade-out would begin: a hold:false and a hold:true action driven
            // identically are BIT-identical every frame through fade-in + sustain. They diverge only past fadeOutStart,
            // where the one-shot fades out and retires while the held action stays at full weight (the byte-stable
            // guarantee: the default hold:false path is unperturbed by the feature).
            Skeleton skel = OneBone();
            var noHold = new LayeredAnimator(skel);
            var held = new LayeredAnimator(skel);
            var clip = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 1f);
            const float dt = 1f / 60f;
            const float fadeOutStart = 0.8f;   // duration 1 - fadeOut 0.2

            noHold.PlayAction(clip, fadeIn: 0.1f, fadeOut: 0.2f, hold: false);
            held.PlayAction(clip, fadeIn: 0.1f, fadeOut: 0.2f, hold: true);

            // Through fade-in + sustain (strictly before fadeOutStart) the two are bit-identical.
            float t = 0f;
            for (; t < fadeOutStart - dt; t += dt)
            {
                noHold.Update(dt); SetBase(noHold, skel, 0f);
                held.Update(dt); SetBase(held, skel, 0f);
                Assert.Equal(Locals(noHold)[0].Translation.X, Locals(held)[0].Translation.X);   // exact
            }

            // Past the clip end: the one-shot has retired to base; the held action stays at full weight.
            for (; t < 1.2f; t += dt)
            {
                noHold.Update(dt); SetBase(noHold, skel, 0f);
                held.Update(dt); SetBase(held, skel, 0f);
            }
            Assert.False(noHold.HasActiveActions);
            Assert.Equal(0f, Locals(noHold)[0].Translation.X, 4);
            Assert.True(held.HasActiveActions);
            Assert.Equal(10f, Locals(held)[0].Translation.X, 3);
        }

        // ---- cancel-all: fades or retires every live action at once (e.g. before a downed transition) ----

        [Fact]
        public void CancelAllActions_Graceful_FadesEveryLiveAction_ThenRetires()
        {
            Skeleton skel = Flat3();
            var anim = new LayeredAnimator(skel);
            var actionA = TranslationClip("a", 0, new Vector3(10, 0, 0), duration: 5f);
            var actionB = TranslationClip("b", 2, new Vector3(20, 0, 0), duration: 5f);
            var maskA = new BoneMask(new[] { 1f, 0f, 0f });
            var maskB = new BoneMask(new[] { 0f, 0f, 1f });
            const float dt = 1f / 60f;

            anim.PlayAction(actionA, maskA, fadeIn: 0.1f, fadeOut: 0.2f);
            anim.PlayAction(actionB, maskB, fadeIn: 0.1f, fadeOut: 0.2f);
            for (float t = 0f; t < 0.5f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }   // both in sustain
            JointPose[] beforeCancel = Locals(anim);
            Assert.Equal(10f, beforeCancel[0].Translation.X, 2);
            Assert.Equal(20f, beforeCancel[2].Translation.X, 2);

            anim.CancelAllActions();

            // The frame right after cancel must not pop: continuity from the current weight on both actions.
            anim.Update(dt); SetBase(anim, skel, 0f);
            JointPose[] justAfter = Locals(anim);
            Assert.True(MathF.Abs(justAfter[0].Translation.X - beforeCancel[0].Translation.X) < 2f, "pop on action A");
            Assert.True(MathF.Abs(justAfter[2].Translation.X - beforeCancel[2].Translation.X) < 2f, "pop on action B");
            Assert.True(anim.HasActiveActions);

            // Drive out the cancel fade (0.2s): both retire, pose returns to base.
            for (float t = 0f; t < 0.4f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            Assert.False(anim.HasActiveActions);
            JointPose[] afterRetire = Locals(anim);
            Assert.Equal(0f, afterRetire[0].Translation.X, 4);
            Assert.Equal(0f, afterRetire[2].Translation.X, 4);
        }

        [Fact]
        public void CancelAllActions_Immediate_RetiresInstantly_SameFrame()
        {
            Skeleton skel = Flat3();
            var anim = new LayeredAnimator(skel);
            var held = TranslationClip("hold", 1, new Vector3(5, 0, 0), duration: 1f);
            var oneShot = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 5f);
            const float dt = 1f / 60f;

            anim.PlayAction(held, mask: new BoneMask(new[] { 0f, 1f, 0f }), fadeIn: 0.1f, fadeOut: 0.1f, hold: true);
            anim.PlayAction(oneShot, mask: new BoneMask(new[] { 1f, 0f, 0f }), fadeIn: 0.1f, fadeOut: 0.1f);
            for (float t = 0f; t < 0.5f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }
            Assert.True(anim.HasActiveActions);

            anim.CancelAllActions(immediate: true);

            // Immediate: no fade frame needed, everything is retired THIS instant (before the next Update even runs).
            Assert.False(anim.HasActiveActions);
            for (int i = 0; i < anim.Layers.Count; i++) Assert.Equal(0f, anim.Layers[i].Weight);

            // A subsequent Update composites nothing but base: pose reads back as pure base.
            anim.Update(dt); SetBase(anim, skel, 3f);
            JointPose[] locals = Locals(anim);
            Assert.Equal(3f, locals[0].Translation.X, 4);
            Assert.Equal(3f, locals[1].Translation.X, 4);
            Assert.Equal(3f, locals[2].Translation.X, 4);
        }

        [Fact]
        public void CancelAllActions_StaleHandleAfterCancelAll_IsNoOp()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            var action = TranslationClip("swing", 0, new Vector3(10, 0, 0), duration: 5f);
            const float dt = 1f / 60f;

            ActionHandle h = anim.PlayAction(action, fadeIn: 0.05f, fadeOut: 0.05f);
            for (float t = 0f; t < 0.2f; t += dt) { anim.Update(dt); SetBase(anim, skel, 0f); }

            anim.CancelAllActions(immediate: true);
            Assert.False(anim.HasActiveActions);
            Assert.False(anim.Cancel(h));   // the generation bumped on retire: the old handle is stale, not live
        }

        [Fact]
        public void CancelAllActions_OnEmptyAnimator_IsNoOp()
        {
            Skeleton skel = OneBone();
            var anim = new LayeredAnimator(skel);
            anim.CancelAllActions();               // graceful, nothing to do
            anim.CancelAllActions(immediate: true); // immediate, nothing to do
            Assert.False(anim.HasActiveActions);
            Assert.Equal(0, anim.LayerCount);       // never touched the layer stack
        }

        static AnimationLayer ActiveLayer(LayeredAnimator anim)
        {
            for (int i = 0; i < anim.Layers.Count; i++)
                if (anim.Layers[i].Weight > 0f) return anim.Layers[i];
            throw new InvalidOperationException("no active (weight>0) layer");
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
