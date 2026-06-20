using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (GPU-free) coverage of <see cref="Scene3D.CoalesceTexturedBillboards"/>: the submission-order run
    /// grouping that batches consecutive same-(texture, blend) textured billboards into one draw while keeping the
    /// host's back-to-front order intact across texture/blend changes.
    /// </summary>
    public class TexturedBillboardCoalesceTests
    {
        static Scene3D.TexturedBillboardItem Item(int tex, BillboardBlend blend) => new()
        {
            TexIndex = tex,
            Blend = blend,
            Center = Vector3.Zero,
            Size = 1f,
            SourceUv = new Vector4(0, 0, 1, 1),
            Color = new Vector4(1, 1, 1, 1),
        };

        static List<Scene3D.TexturedBillboardRun> Coalesce(params Scene3D.TexturedBillboardItem[] items)
        {
            var runs = new List<Scene3D.TexturedBillboardRun>();
            Scene3D.CoalesceTexturedBillboards(items, runs);
            return runs;
        }

        [Fact]
        public void Empty_ProducesNoRuns()
        {
            Assert.Empty(Coalesce());
        }

        [Fact]
        public void ConsecutiveSameTextureAndBlend_MergeIntoOneRun()
        {
            var runs = Coalesce(
                Item(0, BillboardBlend.Alpha),
                Item(0, BillboardBlend.Alpha),
                Item(0, BillboardBlend.Alpha));

            Assert.Single(runs);
            Assert.Equal(0, runs[0].TexIndex);
            Assert.Equal(BillboardBlend.Alpha, runs[0].Blend);
            Assert.Equal(0, runs[0].Start);
            Assert.Equal(3, runs[0].Count);
        }

        [Fact]
        public void TextureChange_StartsNewRun()
        {
            var runs = Coalesce(
                Item(0, BillboardBlend.Alpha),
                Item(1, BillboardBlend.Alpha),
                Item(1, BillboardBlend.Alpha));

            Assert.Equal(2, runs.Count);
            Assert.Equal((0, 0, 1), (runs[0].TexIndex, runs[0].Start, runs[0].Count));
            Assert.Equal((1, 1, 2), (runs[1].TexIndex, runs[1].Start, runs[1].Count));
        }

        [Fact]
        public void BlendChange_StartsNewRun_EvenSameTexture()
        {
            var runs = Coalesce(
                Item(0, BillboardBlend.Alpha),
                Item(0, BillboardBlend.Additive));

            Assert.Equal(2, runs.Count);
            Assert.Equal(BillboardBlend.Alpha, runs[0].Blend);
            Assert.Equal(BillboardBlend.Additive, runs[1].Blend);
        }

        [Fact]
        public void NonAdjacentSameTexture_DoesNotMerge_SubmissionOrderPreserved()
        {
            // tex 0, tex 1, tex 0 again: three runs, NOT two - order must be preserved for back-to-front blending.
            var runs = Coalesce(
                Item(0, BillboardBlend.Alpha),
                Item(1, BillboardBlend.Alpha),
                Item(0, BillboardBlend.Alpha));

            Assert.Equal(3, runs.Count);
            Assert.Equal(0, runs[0].TexIndex);
            Assert.Equal(1, runs[1].TexIndex);
            Assert.Equal(0, runs[2].TexIndex);
            // Every item is covered exactly once, in order.
            Assert.Equal(0, runs[0].Start);
            Assert.Equal(1, runs[1].Start);
            Assert.Equal(2, runs[2].Start);
        }

        [Fact]
        public void Runs_CoverEveryItemExactlyOnce()
        {
            var items = new[]
            {
                Item(2, BillboardBlend.Additive),
                Item(2, BillboardBlend.Additive),
                Item(3, BillboardBlend.Alpha),
                Item(3, BillboardBlend.Additive),
                Item(3, BillboardBlend.Additive),
            };
            var runs = new List<Scene3D.TexturedBillboardRun>();
            Scene3D.CoalesceTexturedBillboards(items, runs);

            int total = 0;
            int expectedStart = 0;
            foreach (var r in runs)
            {
                Assert.Equal(expectedStart, r.Start);
                expectedStart += r.Count;
                total += r.Count;
            }
            Assert.Equal(items.Length, total);
        }
    }
}
