using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests
{
    // The always-on frame-cost counter surface: aggregation (operator +) and per-frame reset semantics. Pure value
    // type, no GPU, so it is fully headless.
    public sealed class RenderFrameStatsTests
    {
        [Fact]
        public void Fresh_struct_is_all_zero()
        {
            var s = default(RenderFrameStats);
            Assert.Equal(0, s.DrawCalls);
            Assert.Equal(0, s.Instances);
            Assert.Equal(0L, s.Triangles);
            Assert.Equal(0L, s.BufferUpdateBytes);
            Assert.Equal(0, s.Quads);
            Assert.Equal(0, s.Flushes);
            Assert.Equal(0, s.TextureSwitches);
        }

        [Fact]
        public void Addition_sums_every_field()
        {
            var a = new RenderFrameStats
            {
                DrawCalls = 3, Instances = 40, Triangles = 5000, BufferUpdateBytes = 2048,
                Quads = 0, Flushes = 0, TextureSwitches = 0,
            };
            var b = new RenderFrameStats
            {
                DrawCalls = 2, Instances = 0, Triangles = 0, BufferUpdateBytes = 512,
                Quads = 120, Flushes = 4, TextureSwitches = 6,
            };

            RenderFrameStats sum = a + b;

            Assert.Equal(5, sum.DrawCalls);
            Assert.Equal(40, sum.Instances);
            Assert.Equal(5000L, sum.Triangles);
            Assert.Equal(2560L, sum.BufferUpdateBytes);
            Assert.Equal(120, sum.Quads);
            Assert.Equal(4, sum.Flushes);
            Assert.Equal(6, sum.TextureSwitches);
        }

        [Fact]
        public void Add_in_place_matches_operator()
        {
            var a = new RenderFrameStats { DrawCalls = 1, Triangles = 10 };
            var b = new RenderFrameStats { DrawCalls = 2, Triangles = 90 };

            var viaOperator = a + b;
            a.Add(b);

            Assert.Equal(viaOperator.DrawCalls, a.DrawCalls);
            Assert.Equal(viaOperator.Triangles, a.Triangles);
            Assert.Equal(3, a.DrawCalls);
            Assert.Equal(100L, a.Triangles);
        }

        [Fact]
        public void Reset_clears_a_populated_tally()
        {
            var s = new RenderFrameStats
            {
                DrawCalls = 9, Instances = 9, Triangles = 9, BufferUpdateBytes = 9,
                Quads = 9, Flushes = 9, TextureSwitches = 9,
            };

            s.Reset();

            Assert.Equal(default, s);
        }

        [Fact]
        public void Upload_helpers_partition_the_total_exactly()
        {
            // The invariant the whole split rests on: each helper bumps the total AND exactly one bucket, so the
            // four buckets always sum back to BufferUpdateBytes. Without it the split is a second, drifting truth,
            // and a reader who cannot trust it is back to guessing which stream a megabyte total came from.
            var s = default(RenderFrameStats);
            s.AddInstanceUpload(1_000);
            s.AddSkinnedUpload(20_000);
            s.AddSkinnedUpload(300);            // repeated calls accumulate, exactly like the old += did
            s.AddSkinnedUniformUpload(40);
            s.AddSpriteUpload(5);

            Assert.Equal(1_000L, s.InstanceUploadBytes);
            Assert.Equal(20_300L, s.SkinnedUploadBytes);
            Assert.Equal(40L, s.SkinnedUniformUploadBytes);
            Assert.Equal(5L, s.SpriteUploadBytes);
            Assert.Equal(21_345L, s.BufferUpdateBytes);
            Assert.Equal(s.BufferUpdateBytes, s.UploadBytesPartitioned);
        }

        [Fact]
        public void Addition_and_reset_keep_the_upload_partition_intact()
        {
            // Aggregating surfaces (a 3D scene plus a 2D HUD batch) must keep the invariant, since that sum is what
            // a HUD actually displays.
            var scene = default(RenderFrameStats);
            scene.AddInstanceUpload(4_096);
            scene.AddSkinnedUpload(65_536);
            var hud = default(RenderFrameStats);
            hud.AddSpriteUpload(2_048);

            RenderFrameStats sum = scene + hud;

            Assert.Equal(4_096L, sum.InstanceUploadBytes);
            Assert.Equal(65_536L, sum.SkinnedUploadBytes);
            Assert.Equal(2_048L, sum.SpriteUploadBytes);
            Assert.Equal(sum.BufferUpdateBytes, sum.UploadBytesPartitioned);

            sum.Reset();
            Assert.Equal(0L, sum.UploadBytesPartitioned);
            Assert.Equal(default, sum);
        }

        [Fact]
        public void Triangles_and_bytes_hold_beyond_int_range()
        {
            // A busy frame's totals can exceed a 32-bit range, so the fields are 64-bit and do not overflow.
            var s = new RenderFrameStats { Triangles = 3_000_000_000L, BufferUpdateBytes = 5_000_000_000L };
            RenderFrameStats doubled = s + s;
            Assert.Equal(6_000_000_000L, doubled.Triangles);
            Assert.Equal(10_000_000_000L, doubled.BufferUpdateBytes);
        }
    }
}
