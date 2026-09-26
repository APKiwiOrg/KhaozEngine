using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The history's GPU targets: two display-size colour and confidence pairs, two internal-size previous
    /// depths, created in Task E1's formats, reused while the sizes hold and recreated when either changes. What they
    /// hold on the device is the figure <see cref="TemporalFormats.HistoryBytes"/> reports, and a recreation or a
    /// release with a retire queue hands the old targets to it rather than freeing them in place.</summary>
    public sealed class TemporalHistoryTargetsGpuTests
    {
        /// <summary>Six textures and four framebuffers per creation.</summary>
        const int ResourcesPerCreation = 10;

        [GpuFact]
        public void The_history_allocates_its_targets_at_the_display_and_internal_sizes_and_reuses_them()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var history = new TemporalHistory();
            try
            {
                Assert.False(history.TargetsAllocated);
                Assert.True(history.EnsureTargets(gd, 96, 54, 64, 36));
                int generation = history.TargetGeneration;
                long bytes = 0;
                for (int i = 0; i < 2; i++)
                {
                    AssertTexture(history.Color(i), 96, 54, TemporalFormats.HistoryColor);
                    AssertTexture(history.Confidence(i), 96, 54, TemporalFormats.HistoryConfidence);
                    AssertTexture(history.PreviousDepth(i), 64, 36, TemporalFormats.PreviousDepth);
                    Assert.Equal(new[] { TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence },
                        history.ResolveFramebuffer(i).Outputs.Colour);
                    Assert.Null(history.ResolveFramebuffer(i).Outputs.Depth);
                    Assert.Equal(new[] { TemporalFormats.PreviousDepth }, history.PreviousDepthFramebuffer(i).Outputs.Colour);
                    bytes += Bytes(history.Color(i)) + Bytes(history.Confidence(i)) + Bytes(history.PreviousDepth(i));
                }
                Assert.NotSame(history.Color(0), history.Color(1));
                // The six textures, sized by the staging table rather than TemporalFormats' own constants, hold what
                // the memory figure claims. That figure is what TemporalFormatsTests pins at 213 MB for Quality.
                Assert.Equal(TemporalFormats.HistoryBytes(96, 54, 64, 36), bytes);

                Assert.False(history.EnsureTargets(gd, 96, 54, 64, 36));
                Assert.Equal(generation, history.TargetGeneration);

                history.BeginResolve(5);
                history.BeginResolve(6);
                Assert.True(history.EnsureTargets(gd, 128, 72, 64, 36));
                Assert.Equal(generation + 1, history.TargetGeneration);
                Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
                AssertTexture(history.Color(0), 128, 72, TemporalFormats.HistoryColor);

                Assert.True(history.EnsureTargets(gd, 128, 72, 80, 45));
                AssertTexture(history.PreviousDepth(1), 80, 45, TemporalFormats.PreviousDepth);
            }
            finally
            {
                history.ReleaseTargets();
            }
            Assert.False(history.TargetsAllocated);
            Assert.Throws<InvalidOperationException>(() => history.Color(0));
        }

        [GpuFact]
        public void A_recreation_and_a_release_hand_the_old_targets_to_the_retire_queue()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var history = new TemporalHistory();
            GpuRetireQueue retired = GpuRetireQueue.Create(gd);
            try
            {
                Assert.True(history.EnsureTargets(gd, 96, 54, 64, 36, retired));
                Assert.Equal(0, retired.PendingCount);
                Assert.True(history.EnsureTargets(gd, 128, 72, 64, 36, retired));
                Assert.Equal(ResourcesPerCreation, retired.PendingCount);
                history.ReleaseTargets(retired);
                Assert.Equal(2 * ResourcesPerCreation, retired.PendingCount);
                Assert.False(history.TargetsAllocated);
            }
            finally
            {
                history.ReleaseTargets();
                retired.Dispose();   // outside any recording: drains once and frees what the history retired
            }
            Assert.Equal(0, retired.PendingCount);
        }

        static long Bytes(IGpuTexture texture) =>
            (long)texture.Width * texture.Height * MetalStagingLayout.BytesPerTexel(texture.Format);

        static void AssertTexture(IGpuTexture texture, uint width, uint height, GpuPixelFormat format)
        {
            Assert.Equal((width, height), (texture.Width, texture.Height));
            Assert.Equal(format, texture.Format);
            Assert.Equal(1u, texture.MipLevels);
            Assert.Equal(1u, texture.SampleCount);
        }
    }
}
