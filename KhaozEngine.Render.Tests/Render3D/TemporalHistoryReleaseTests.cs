using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// How the history's targets live and die, on the device-free fake. A recreation or a release hands the old targets
    /// to the caller's retire queue, because a command list the device has not finished may still read them. Without a
    /// queue it drains the device and frees them at once. Every creation bumps the generation, the same sizes after a
    /// release included, and a reset keeps the targets.
    /// </summary>
    public sealed class TemporalHistoryReleaseTests
    {
        [Fact]
        public void A_creation_makes_six_textures_and_four_framebuffers()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            var history = new TemporalHistory();

            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36));

            Assert.Equal(6, factory.Textures.Count);
            Assert.Equal(4, factory.Framebuffers.Count);
            Assert.True(history.TargetsAllocated);
            Assert.Equal((96, 54, 64, 36),
                (history.DisplayWidth, history.DisplayHeight, history.InternalWidth, history.InternalHeight));
            history.ReleaseTargets();
        }

        [Fact]
        public void With_a_retire_queue_the_old_targets_outlive_the_call_and_die_at_the_queue_boundary()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
            var history = new TemporalHistory();

            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36, retired));
            List<FakeTexture> first = factory.Textures.GetRange(0, 6);
            Assert.True(history.EnsureTargets(device, 128, 72, 64, 36, retired));   // a resize replaces all six
            AssertNoneFreed(first, "a resize freed the old targets in place, while a submitted frame may still read them");

            List<FakeTexture> second = factory.Textures.GetRange(6, 6);
            history.ReleaseTargets(retired);
            AssertNoneFreed(second, "a release freed the targets in place, while a submitted frame may still read them");
            Assert.Equal(0, device.WaitForIdleCalls);

            retired.BeginFrame();
            Assert.All(factory.Textures, t => Assert.True(t.Disposed));
            Assert.Equal(0, device.WaitForIdleCalls);
        }

        [Fact]
        public void Without_a_queue_a_release_drains_the_device_once_and_frees_at_once()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            var history = new TemporalHistory();
            history.EnsureTargets(device, 96, 54, 64, 36);

            history.ReleaseTargets();

            Assert.Equal(1, device.WaitForIdleCalls);
            Assert.All(factory.Textures, t => Assert.True(t.Disposed));
            history.ReleaseTargets();
            Assert.Equal(1, device.WaitForIdleCalls);   // nothing left to free, so nothing to drain for
        }

        [Fact]
        public void Every_creation_bumps_the_generation_the_same_sizes_after_a_release_included()
        {
            using var device = new FakeGpuDevice();
            var history = new TemporalHistory();
            Assert.Equal(0, history.TargetGeneration);

            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.Equal(1, history.TargetGeneration);
            Assert.False(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.Equal(1, history.TargetGeneration);

            history.ReleaseTargets();
            Assert.Equal(1, history.TargetGeneration);   // a release creates nothing
            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.Equal(2, history.TargetGeneration);

            using var other = new FakeGpuDevice();       // a new device holds none of the old targets
            Assert.True(history.EnsureTargets(other, 96, 54, 64, 36));
            Assert.Equal(3, history.TargetGeneration);
            history.ReleaseTargets();
        }

        [Fact]
        public void A_reset_keeps_the_targets()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            var history = new TemporalHistory();
            history.EnsureTargets(device, 96, 54, 64, 36);
            history.MarkValidAfterFrame();

            history.Invalidate(TemporalResetReason.CameraCutRequested);

            Assert.False(history.IsValid);
            Assert.True(history.TargetsAllocated);
            Assert.Equal(1, history.TargetGeneration);
            AssertNoneFreed(factory.Textures, "a reset freed the targets");
            Assert.False(history.EnsureTargets(device, 96, 54, 64, 36));
            history.ReleaseTargets();
        }

        static void AssertNoneFreed(List<FakeTexture> textures, string because)
        {
            foreach (FakeTexture texture in textures) Assert.False(texture.Disposed, because);
        }
    }
}
