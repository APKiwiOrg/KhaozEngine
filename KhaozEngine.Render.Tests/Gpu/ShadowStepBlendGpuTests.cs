using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof that the temporal shadow cross-fade (issue #225) actually lerps the OUTGOING and INCOMING quantization
    /// steps on the real shader path (the second atlas binds, the frozen sample runs, the weight ramps). A same-session
    /// luminance invariant, not a committed grid, so it runs on every backend: a ground probe that is SHADOWED under the
    /// outgoing sun direction but LIT under the incoming one must read darkest at weight 0 (fully outgoing), brightest at
    /// the settled incoming step, and strictly BETWEEN at a mid-fade weight. A second test proves the ADAPTIVE window
    /// (issue #227): with a clamp far above the step interval, a fade still completes within one (shorter) observed
    /// interval, so the ramp spans that whole interval instead of covering only a clamp-sized sliver of it. Skipped
    /// unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class ShadowStepBlendGpuTests
    {
        const int W = 480, H = 320;
        const float Window = 0.4f;   // ShadowStepBlendSeconds

        // Shallow suns 180 deg apart in azimuth: A throws the tall caster's shadow toward +Z (over the probe), B throws
        // it toward -Z (the probe is lit). 180 deg apart guarantees the quantized fit direction steps.
        static readonly Vector3 DirA = Vector3.Normalize(new Vector3(0f, -0.5f, 0.87f));
        static readonly Vector3 DirB = Vector3.Normalize(new Vector3(0f, -0.5f, -0.87f));
        static readonly Matrix4x4 Caster = Matrix4x4.CreateScale(1.2f, 3.5f, 1.2f) * Matrix4x4.CreateTranslation(0f, 1.75f, 0f);
        static readonly Vector3 Probe = new(0f, 0f, 3f);    // ground solidly inside DirA's long +z shadow, lit under DirB
        static readonly Vector3 LitRef = new(-6.5f, 0f, 0f); // open ground, off both shadows (a stable brightness anchor)

        static ShadowSettings BlendSettings() => new()
        {
            Mode = ShadowMode.ShadowMap,
            ShadowLightQuantizeDegrees = 3f,   // quantized, so the A->B change is a discrete step the fade eases
            ShadowStepBlendSeconds = Window,   // > 0 at construction => the second atlas is reserved
        };

        [GpuFact]
        public void StepBlend_CrossFades_FromOutgoing_ToIncoming()
        {
            // Weight 0: settle at A (frame 0 renders + commits A), then step to B on frame 1 (freezes A, renders B, the
            // fade is at weight 0 so the receiver shows the frozen A shadow over the probe).
            float wFrozen = ShadowRatio(BlendSettings(), new (Vector3 dir, float t)[] { (DirA, 0f), (DirB, 0.02f) });

            // Weight ~0.5: same step, then advance EffectTime by half the window on frame 2.
            float wMid = ShadowRatio(BlendSettings(), new (Vector3 dir, float t)[] { (DirA, 0f), (DirB, 0.02f), (DirB, 0.02f + Window * 0.5f) });

            // Settled incoming: hold B from the first frame (no step, no fade), so the probe is lit under B.
            float wLit = ShadowRatio(BlendSettings(), new (Vector3 dir, float t)[] { (DirB, 0f), (DirB, 0.02f) });

            // Sanity: the two ENDPOINTS must actually differ (the probe really is shadowed under A and lit under B), or
            // the betweenness assertion below would be vacuous.
            Assert.True(wLit - wFrozen > 0.15f,
                $"the probe is not shadowed-under-A / lit-under-B enough to test a fade (frozen {wFrozen:0.###}, lit {wLit:0.###}); scene/light/probe changed?");

            // The cross-fade ramps outgoing -> incoming: mid-fade sits strictly between the frozen and lit endpoints.
            Assert.True(wMid > wFrozen + 0.04f,
                $"mid-fade ({wMid:0.###}) did not brighten past the fully-outgoing frame ({wFrozen:0.###}): the incoming step is not fading in.");
            Assert.True(wMid < wLit - 0.04f,
                $"mid-fade ({wMid:0.###}) reached the fully-incoming brightness ({wLit:0.###}): the outgoing (frozen) shadow is not contributing.");
        }

        [GpuFact]
        public void StepBlend_AdaptiveWindow_SpansAShortInterval_UnderALargeClamp()
        {
            // A LARGE clamp (1s) with a SHORT observed step interval (5 frames x 0.02s = 0.1s): the fade must run for the
            // 0.1s interval, not the 1s clamp (issue #227). So 0.1s after the step the fade has fully settled (weight 1);
            // a fixed clamp-sized window would be at weight 0.1 there, still essentially the frozen (outgoing) shadow.
            // The plan commits B (lit), steps to A (shadow), then steps back to B: at that final step the frozen set is
            // the shadowed A and the incoming set is the lit B, so the fade runs shadow -> lit. dt (0.02) is well under
            // the interval, so the per-frame bypass never engages and the quantized adaptive blend is what runs.
            const float dt = 0.02f;
            float wFrozen  = ShadowRatio(AdaptiveSettings(), Plan(dt, (DirB, 5), (DirA, 5), (DirB, 1)));   // final step, weight 0
            float wMid     = ShadowRatio(AdaptiveSettings(), Plan(dt, (DirB, 5), (DirA, 5), (DirB, 3)));   // +0.04 into the 0.10 window
            float wSettled = ShadowRatio(AdaptiveSettings(), Plan(dt, (DirB, 5), (DirA, 5), (DirB, 6)));   // +0.10: the whole window
            float wLit     = ShadowRatio(AdaptiveSettings(), Plan(dt, (DirB, 2)));                          // settled B, no fade

            // Endpoints must actually differ (probe shadowed under frozen A, lit under B), or the ramp assertions are vacuous.
            Assert.True(wLit - wFrozen > 0.15f,
                $"probe not shadowed-under-A / lit-under-B enough to test the ramp (frozen {wFrozen:0.###}, lit {wLit:0.###}).");

            // The ramp is real: mid-fade sits strictly between the frozen and lit endpoints.
            Assert.True(wMid > wFrozen + 0.04f && wMid < wLit - 0.04f,
                $"mid-fade ({wMid:0.###}) is not strictly between frozen ({wFrozen:0.###}) and lit ({wLit:0.###}).");

            // THE adaptive-window property: 0.10s after the step (one whole observed interval) the fade has settled to
            // the incoming (lit) result, and it kept progressing past mid to get there. A fixed clamp-sized (1s) window
            // would read ~frozen at +0.10 (weight 0.1), so this would fail if the window ignored the interval.
            Assert.True(wSettled > wLit - 0.05f,
                $"the fade did not complete within the 0.10s observed interval (settled {wSettled:0.###}, lit {wLit:0.###}): the window is not adapting.");
            Assert.True(wSettled > wMid + 0.04f,
                $"the fade did not keep progressing past mid within the interval (settled {wSettled:0.###}, mid {wMid:0.###}).");
        }

        static ShadowSettings AdaptiveSettings() => new()
        {
            Mode = ShadowMode.ShadowMap,
            ShadowLightQuantizeDegrees = 3f,   // quantized, so the A<->B changes are discrete steps
            ShadowStepBlendSeconds = 1f,       // a large CLAMP MAX, well above the 0.1s step interval the plan establishes
        };

        // Expand a step plan into per-frame (LightDirection, EffectTimeSeconds) samples at a fixed dt: each entry holds
        // its direction for `frames` frames (so an inter-step interval is frames*dt), with EffectTime accumulating
        // continuously across the whole plan. A direction change on an entry's first frame is the quantized step.
        static (Vector3 dir, float t)[] Plan(float dt, params (Vector3 dir, int frames)[] steps)
        {
            var seq = new List<(Vector3, float)>();
            float t = 0f;
            foreach (var (dir, frames) in steps)
                for (int i = 0; i < frames; i++)
                {
                    seq.Add((dir, t));
                    t += dt;
                }
            return seq.ToArray();
        }

        // Drive the scene over the given (LightDirection, EffectTimeSeconds) sequence (the shadow atlas is sized by the
        // construction seam), read back the LAST frame, and return the probe's luminance as a fraction of open lit
        // ground (well below 1 = the probe is in shadow).
        static float ShadowRatio(ShadowSettings shadows, (Vector3 dir, float t)[] seq)
        {
            MeshHandle floor = default, caster = default;
            int frame = 0;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.LightDirection = seq[0].dir;
                    scene.Camera.Azimuth = 0.4f;
                    scene.Camera.Elevation = 1.0f;
                    scene.Camera.Frame(new Vector3(0f, 0f, 2.5f), new Vector3(16f, 9f, 16f));
                },
                drawFrame: scene =>
                {
                    var (dir, t) = seq[Math.Min(frame, seq.Length - 1)];
                    scene.Post.LightDirection = dir;
                    scene.EffectTimeSeconds = t;
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    scene.Draw(caster, Caster, new Color(0.25f, 0.7f, 0.3f, 1f));
                    frame++;
                },
                frames: seq.Length,
                shadows: shadows);

            var cam = new IsoCamera3D { Azimuth = 0.4f, Elevation = 1.0f };
            cam.Frame(new Vector3(0f, 0f, 2.5f), new Vector3(16f, 9f, 16f));
            cam.AspectRatio = (float)W / H;
            float lit = GroundLum(rgba, cam, LitRef);
            float at = GroundLum(rgba, cam, Probe);
            return lit > 1e-3f ? at / lit : 1f;
        }

        static float GroundLum(byte[] rgba, IsoCamera3D cam, Vector3 world)
        {
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) return 0f;
            int px = (int)(p.X + 0.5f), py = (int)(p.Y + 0.5f);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                }
            if (n == 0) return 0f;
            float rf = r / (255f * n), gf = g / (255f * n), bf = b / (255f * n);
            return 0.299f * rf + 0.587f * gf + 0.114f * bf;
        }
    }
}
