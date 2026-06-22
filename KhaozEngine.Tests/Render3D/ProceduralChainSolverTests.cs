using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Headless tests for the procedural chain (tentacle/cable/limb) solver: writhe generation,
    // FABRIK reach, the writhe+reach blend, and the slam envelope. Pure math, no GPU.
    public class ProceduralChainSolverTests
    {
        static readonly ChainConfig Writhe = ChainConfig.Writhe;

        static Vector3[] Solve(int bones, Vector3 forward, Vector3 up, float t, in ChainConfig cfg)
        {
            var spine = new Vector3[bones];
            ProceduralChainSolver.Solve(Vector3.Zero, forward, up, t, cfg, spine);
            return spine;
        }

        [Fact]
        public void Solve_AnchorsRootAtFirstPoint()
        {
            var root = new Vector3(3, -2, 5);
            var spine = new Vector3[8];
            ProceduralChainSolver.Solve(root, Vector3.UnitX, Vector3.UnitY, 1.3f, Writhe, spine);
            Assert.Equal(root, spine[0]);
        }

        [Fact]
        public void Solve_KeepsUniformSegmentLength()
        {
            var spine = Solve(10, Vector3.UnitZ, Vector3.UnitY, 0.9f, Writhe);
            for (int i = 1; i < spine.Length; i++)
            {
                float len = (spine[i] - spine[i - 1]).Length();
                Assert.True(MathF.Abs(len - Writhe.SegmentLength) < 1e-4f,
                    $"segment {i} length {len} != {Writhe.SegmentLength}");
            }
        }

        [Fact]
        public void Solve_IsDeterministic()
        {
            var a = Solve(12, Vector3.UnitX, Vector3.UnitY, 2.7f, Writhe);
            var b = Solve(12, Vector3.UnitX, Vector3.UnitY, 2.7f, Writhe);
            Assert.Equal(a, b);
        }

        [Fact]
        public void Solve_ZeroAmplitudeIsStraightAlongForward()
        {
            var cfg = new ChainConfig(0.5f, writheAmplitude: 0f, writheFrequency: 2f,
                segmentPhaseLag: 0.7f, coilWaveFrac: 0.6f, outOfPlaneFrac: 0.4f);
            var spine = Solve(6, Vector3.UnitX, Vector3.UnitY, 1.1f, cfg);
            for (int i = 0; i < spine.Length; i++)
            {
                var expected = Vector3.UnitX * (cfg.SegmentLength * i);
                Assert.True((spine[i] - expected).Length() < 1e-4f, $"point {i} {spine[i]} != {expected}");
            }
        }

        [Fact]
        public void Solve_NonZeroAmplitudeBendsAwayFromAxis()
        {
            var spine = Solve(10, Vector3.UnitZ, Vector3.UnitY, 1.7f, Writhe);
            // The tip should not sit on the forward axis once the limb writhes.
            float lateral = MathF.Sqrt(spine[^1].X * spine[^1].X + spine[^1].Y * spine[^1].Y);
            Assert.True(lateral > 1e-2f, $"expected lateral deviation, got {lateral}");
        }

        [Fact]
        public void Solve_PlanarWhenOutOfPlaneFracZero()
        {
            // forward=Z, up=Y -> right = cross(up,forward) = cross(Y,Z) = X. Planar writhe lives in (Z,X);
            // every point's Y component (the up axis) must stay ~0.
            var cfg = new ChainConfig(0.5f, 0.2f, 2.2f, 0.7f, 0.6f, outOfPlaneFrac: 0f);
            var spine = Solve(12, Vector3.UnitZ, Vector3.UnitY, 2.1f, cfg);
            foreach (var p in spine)
                Assert.True(MathF.Abs(p.Y) < 1e-4f, $"expected planar (Y~0), got {p}");
        }

        [Fact]
        public void Solve_OutOfPlaneFracLeavesThePlane()
        {
            var cfg = new ChainConfig(0.5f, 0.2f, 2.2f, 0.7f, 0.6f, outOfPlaneFrac: 0.6f);
            var spine = Solve(12, Vector3.UnitZ, Vector3.UnitY, 2.1f, cfg);
            float maxY = 0f;
            foreach (var p in spine) maxY = MathF.Max(maxY, MathF.Abs(p.Y));
            Assert.True(maxY > 1e-2f, $"expected out-of-plane motion, got maxY {maxY}");
        }

        // ---- FABRIK ----

        static Vector3[] StraightChain(int bones, Vector3 dir, float segLen)
        {
            var spine = new Vector3[bones];
            for (int i = 0; i < bones; i++) spine[i] = dir * (segLen * i);
            return spine;
        }

        [Fact]
        public void Fabrik_ReachesReachableTargetAndKeepsSegmentLengths()
        {
            var spine = StraightChain(5, Vector3.UnitX, 1f); // reach = 4
            var target = new Vector3(2f, 1.5f, 0f);          // dist ~2.5 < 4
            ProceduralChainSolver.Fabrik(spine, Vector3.Zero, target, segmentLength: 1f, iterations: 20);

            Assert.True((spine[^1] - target).Length() < 1e-2f, $"tip {spine[^1]} != target {target}");
            Assert.Equal(Vector3.Zero, spine[0]); // root pinned
            for (int i = 1; i < spine.Length; i++)
            {
                float len = (spine[i] - spine[i - 1]).Length();
                Assert.True(MathF.Abs(len - 1f) < 1e-3f, $"segment {i} length {len} != 1");
            }
        }

        [Fact]
        public void Fabrik_UnreachableTargetStretchesStraightToMaxReach()
        {
            var spine = StraightChain(5, Vector3.UnitY, 1f); // reach = 4
            var target = new Vector3(100f, 0f, 0f);          // far out of range
            ProceduralChainSolver.Fabrik(spine, Vector3.Zero, target, segmentLength: 1f, iterations: 20);

            var expectedTip = new Vector3(4f, 0f, 0f); // root + dir(target)*reach
            Assert.True((spine[^1] - expectedTip).Length() < 1e-2f, $"tip {spine[^1]} != {expectedTip}");
        }

        // ---- writhe + reach blend ----

        [Fact]
        public void SolveReach_ZeroWeightMatchesNaturalWrithe()
        {
            var natural = Solve(10, Vector3.UnitX, Vector3.UnitY, 1.4f, Writhe);
            var blended = new Vector3[10];
            ProceduralChainSolver.SolveReach(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1.4f,
                target: new Vector3(0f, 5f, 0f), reachWeight: 0f, Writhe, blended);

            Assert.True((blended[^1] - natural[^1]).Length() < 1e-2f,
                $"weight 0 tip {blended[^1]} should match writhe tip {natural[^1]}");
        }

        [Fact]
        public void SolveReach_FullWeightReachesTarget()
        {
            var blended = new Vector3[10];
            // reach = 9 * 0.5 = 4.5; target within range.
            var target = new Vector3(1.5f, 2f, 0.5f);
            ProceduralChainSolver.SolveReach(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 0.6f,
                target, reachWeight: 1f, Writhe, blended);

            Assert.True((blended[^1] - target).Length() < 2e-2f, $"tip {blended[^1]} != target {target}");
        }

        // ---- slam envelope ----

        [Theory]
        [InlineData(0f)]
        [InlineData(0.05f)]
        [InlineData(0.5f)]
        [InlineData(0.999f)]
        [InlineData(1.3f)]   // wraps
        [InlineData(-0.2f)]  // wraps
        public void SlamEnvelope_StaysInUnitRange(float phase)
        {
            float v = ProceduralChainSolver.SlamEnvelope(phase, 0.2f);
            Assert.InRange(v, 0f, 1f);
        }

        [Fact]
        public void SlamEnvelope_RestsAtZeroAndPeaksAtOne()
        {
            // phase 0 = full reach (out), envelope 0; mid-pull hold (phase ~0.35) = 1.
            Assert.True(ProceduralChainSolver.SlamEnvelope(0f, 0.2f) < 1e-4f);
            Assert.True(ProceduralChainSolver.SlamEnvelope(0.35f, 0.2f) > 0.999f);
        }
    }
}
