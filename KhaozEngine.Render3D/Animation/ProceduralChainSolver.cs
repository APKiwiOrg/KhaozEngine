using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Tuning for a procedural chain limb (tentacle / cable / tail). Distances are in model units,
    /// angles in radians. A zero <see cref="WritheAmplitude"/> yields a straight limb along the forward axis.</summary>
    public readonly struct ChainConfig
    {
        /// <summary>Arc step between consecutive bones. The solved spine keeps this length on every segment.</summary>
        public readonly float SegmentLength;
        /// <summary>Per-segment bend angle master (radians). Scales both writhe waves.</summary>
        public readonly float WritheAmplitude;
        /// <summary>Temporal rate fed into the writhe sines (radians/second).</summary>
        public readonly float WritheFrequency;
        /// <summary>Spatial phase lag per bone, so the writhe travels down the limb instead of bending as one.</summary>
        public readonly float SegmentPhaseLag;
        /// <summary>Mix of the second, counter-travelling writhe wave (more = more chaotic, less periodic).</summary>
        public readonly float CoilWaveFrac;
        /// <summary>0 = planar writhe in the (forward,right) plane; &gt;0 adds (forward,up) pitch for a 3D curl.</summary>
        public readonly float OutOfPlaneFrac;

        public ChainConfig(float segmentLength, float writheAmplitude, float writheFrequency,
                           float segmentPhaseLag, float coilWaveFrac, float outOfPlaneFrac)
        {
            SegmentLength = segmentLength;
            WritheAmplitude = writheAmplitude;
            WritheFrequency = writheFrequency;
            SegmentPhaseLag = segmentPhaseLag;
            CoilWaveFrac = coilWaveFrac;
            OutOfPlaneFrac = outOfPlaneFrac;
        }

        /// <summary>Lively organic idle writhe (a slow 3D curl).</summary>
        public static ChainConfig Writhe => new(0.5f, 0.18f, 2.2f, 0.7f, 0.6f, 0.35f);
        /// <summary>A calmer, lower-amplitude sway.</summary>
        public static ChainConfig Calm => new(0.5f, 0.07f, 1.1f, 0.7f, 0.4f, 0.2f);
    }

    /// <summary>Pure, deterministic procedural animator for chain limbs (tentacles, cables, tails). It produces a
    /// per-frame 3D spine (one point per bone) from a writhe model, optionally bent toward a target by a FABRIK
    /// reach, suitable for <see cref="PolylineFrames.Build"/> -&gt; <c>Scene3D.DrawSkinned</c>. No GPU or sim deps,
    /// so it is fully headless-testable. Generalizes the 2D tentacle layout shipped game-side in SpaceGame to an
    /// arbitrary 3D frame.</summary>
    public static class ProceduralChainSolver
    {
        /// <summary>Generate a writhe-only spine. <paramref name="spineOut"/>[0] is anchored at
        /// <paramref name="root"/>; each subsequent point is <see cref="ChainConfig.SegmentLength"/> further along a
        /// cumulatively bending direction that starts at <paramref name="forward"/>. The writhe bends in the
        /// (forward, right) plane (right = up x forward) plus an optional out-of-plane pitch about right.</summary>
        public static void Solve(Vector3 root, Vector3 forward, Vector3 up, float clockSeconds,
                                 in ChainConfig cfg, Span<Vector3> spineOut)
        {
            int n = spineOut.Length;
            if (n == 0) return;
            spineOut[0] = root;
            if (n == 1) return;

            Vector3 f = SafeNormalize(forward, Vector3.UnitZ);
            Vector3 upN = SafeNormalize(up, Vector3.UnitY);
            Vector3 right = Vector3.Cross(upN, f);
            if (right.LengthSquared() < 1e-8f)
            {
                // up parallel to forward: pick a well-conditioned alternate so the basis is not degenerate.
                Vector3 alt = MathF.Abs(f.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                right = Vector3.Cross(alt, f);
            }
            right = Vector3.Normalize(right);
            Vector3 upo = Vector3.Normalize(Vector3.Cross(f, right)); // re-orthogonalized roll axis

            Vector3 pos = root;
            Vector3 dir = f;
            float step = cfg.SegmentLength;
            for (int s = 1; s < n; s++)
            {
                // Two counter-travelling waves beat against each other so the cumulative bend crosses zero
                // (the limb extends, curls and flicks both ways) rather than coiling one fixed direction.
                float wave1 = MathF.Sin((clockSeconds * cfg.WritheFrequency) - (s * cfg.SegmentPhaseLag));
                float wave2 = MathF.Sin((clockSeconds * cfg.WritheFrequency * 0.57f)
                    + (s * cfg.SegmentPhaseLag * 0.8f) + 2.1f);
                float yaw = cfg.WritheAmplitude * (wave1 + (cfg.CoilWaveFrac * wave2));

                float wave3 = MathF.Sin((clockSeconds * cfg.WritheFrequency * 0.83f) - (s * cfg.SegmentPhaseLag) + 1.0f);
                float pitch = cfg.WritheAmplitude * cfg.OutOfPlaneFrac * wave3;

                dir = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(upo, yaw));
                dir = Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(right, pitch));
                dir = Vector3.Normalize(dir);
                pos += dir * step;
                spineOut[s] = pos;
            }
        }

        /// <summary>Generate a writhe spine, then bend it toward <paramref name="target"/> by
        /// <paramref name="reachWeight"/> in [0,1] (0 = the natural writhe tip, 1 = the tip pulled onto the target,
        /// clamped to the limb's reach). Use the slam/grab envelope to drive <paramref name="reachWeight"/> over
        /// time. Segment lengths are preserved by the FABRIK pass.</summary>
        public static void SolveReach(Vector3 root, Vector3 forward, Vector3 up, float clockSeconds,
                                      Vector3 target, float reachWeight, in ChainConfig cfg, Span<Vector3> spineOut)
        {
            Solve(root, forward, up, clockSeconds, cfg, spineOut);
            int n = spineOut.Length;
            if (n < 2) return;
            float w = Math.Clamp(reachWeight, 0f, 1f);
            Vector3 naturalTip = spineOut[n - 1];
            Vector3 effTarget = Vector3.Lerp(naturalTip, target, w);
            Fabrik(spineOut, root, effTarget, cfg.SegmentLength);
        }

        /// <summary>In-place FABRIK (forward-and-backward reaching inverse kinematics) over a uniform-length chain.
        /// Pins <paramref name="spine"/>[0] to <paramref name="root"/> and pulls the tip toward
        /// <paramref name="target"/> while preserving <paramref name="segmentLength"/> on every segment. If the
        /// target is out of reach the chain stretches straight toward it.</summary>
        public static void Fabrik(Span<Vector3> spine, Vector3 root, Vector3 target,
                                  float segmentLength, int iterations = 12)
        {
            int n = spine.Length;
            if (n == 0) return;
            spine[0] = root;
            if (n == 1) return;

            float reach = segmentLength * (n - 1);
            Vector3 toTarget = target - root;
            float dist = toTarget.Length();
            if (dist >= reach)
            {
                Vector3 dir = dist > 1e-6f ? toTarget / dist : Vector3.UnitX;
                for (int i = 0; i < n; i++) spine[i] = root + (dir * (segmentLength * i));
                return;
            }

            for (int iter = 0; iter < iterations; iter++)
            {
                // Backward: place the tip on the target, then walk to the root keeping segment lengths.
                spine[n - 1] = target;
                for (int i = n - 2; i >= 0; i--)
                    spine[i] = spine[i + 1] + (SafeNormalize(spine[i] - spine[i + 1], Vector3.UnitX) * segmentLength);
                // Forward: re-pin the root, then walk to the tip keeping segment lengths.
                spine[0] = root;
                for (int i = 1; i < n; i++)
                    spine[i] = spine[i - 1] + (SafeNormalize(spine[i] - spine[i - 1], Vector3.UnitX) * segmentLength);

                if ((spine[n - 1] - target).LengthSquared() < 1e-8f) break;
            }
        }

        /// <summary>Power-stroke / slam envelope over one cycle, in [0,1]. Holds at 0 (limb reached out), a snappy
        /// ramp to 1 (slam/pull), a brief hold, then a ramp back to 0. <paramref name="snap"/> is each transition's
        /// width as a fraction of the cycle (small = brief snappy moves with long holds; clamped to [0.05,0.5]).
        /// <paramref name="phase"/> wraps, so a steadily advancing phase loops the stroke. Drive a limb's
        /// reach-weight or whip with this.</summary>
        public static float SlamEnvelope(float phase, float snap)
        {
            float w = Math.Clamp(snap, 0.05f, 0.5f);
            float p = phase - MathF.Floor(phase); // wrap to [0,1)
            if (p < w) return Smooth(p / w);                 // reach -> slam
            if (p < 0.5f) return 1f;                         // hold slammed
            if (p < 0.5f + w) return Smooth(1f - ((p - 0.5f) / w)); // recover -> reach
            return 0f;                                       // hold reached
        }

        static float Smooth(float x) => x * x * (3f - (2f * x));

        static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float lenSq = v.LengthSquared();
            return lenSq < 1e-12f ? fallback : v / MathF.Sqrt(lenSq);
        }
    }
}
