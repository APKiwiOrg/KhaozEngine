using System;
using System.Collections.Generic;
using System.Linq;
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
    /// release included, and so does a release of allocated targets, while a reset keeps the targets. The drained path
    /// refuses while the device is recording, since its drain says nothing about an open list.
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
            const GpuTextureUsage usage = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
            Assert.All(factory.Textures, t => Assert.Equal(usage, t.Usage));
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
            int freedAtDrain = -1;
            device.OnWaitForIdle = () => freedAtDrain = factory.Textures.Count(t => t.Disposed);

            history.ReleaseTargets();

            Assert.Equal(1, device.WaitForIdleCalls);
            Assert.Equal(0, freedAtDrain);   // the drain came before the first free
            Assert.All(factory.Textures, t => Assert.True(t.Disposed));
            history.ReleaseTargets();
            Assert.Equal(1, device.WaitForIdleCalls);   // nothing left to free, so nothing to drain for
        }

        [Theory]
        [InlineData(4, 0)]   // the second pair's colour texture
        [InlineData(0, 2)]   // the first pair's previous-depth framebuffer, after its three textures
        public void A_creation_that_fails_part_way_leaves_what_it_made_releasable(int failTexture, int failFramebuffer)
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            factory.ThrowOnTextureCreate = failTexture;
            factory.ThrowOnFramebufferCreate = failFramebuffer;
            var history = new TemporalHistory();

            Assert.Throws<InvalidOperationException>(() => history.EnsureTargets(device, 96, 54, 64, 36));
            List<FakeTexture> made = factory.Textures.GetRange(0, 3);
            Assert.Equal(3, factory.Textures.Count);
            AssertNoneFreed(made, "the failed creation freed what it made before anyone released it");

            // A retry at the same sizes creates all six, because the partial set is not the targets it asked for, and
            // frees what the failed call made.
            factory.ThrowOnTextureCreate = factory.ThrowOnFramebufferCreate = 0;
            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.All(made, t => Assert.True(t.Disposed));
            Assert.Equal(9, factory.Textures.Count);

            history.ReleaseTargets();
            Assert.All(factory.Textures, t => Assert.True(t.Disposed));
            Assert.False(history.TargetsAllocated);
        }

        [Fact]
        public void A_failed_creation_released_directly_frees_what_it_made()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            factory.ThrowOnTextureCreate = 5;
            var history = new TemporalHistory();
            Assert.Throws<InvalidOperationException>(() => history.EnsureTargets(device, 96, 54, 64, 36));

            history.ReleaseTargets();

            Assert.Equal(4, factory.Textures.Count);
            Assert.All(factory.Textures, t => Assert.True(t.Disposed));
            Assert.Equal(1, device.WaitForIdleCalls);
        }

        [Fact]
        public void Without_a_queue_a_release_while_the_device_records_is_refused_and_changes_nothing()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            var history = new TemporalHistory();
            history.EnsureTargets(device, 96, 54, 64, 36);
            history.BeginResolve(3);
            history.BeginResolve(4);
            int generation = history.TargetGeneration;

            using (GpuRecording.Open(device, device.Factory.CreateCommandList(), "the window's frame list"))
            {
                var release = Assert.Throws<InvalidOperationException>(() => history.ReleaseTargets());
                Assert.Contains("the window's frame list", release.Message);
                var resize = Assert.Throws<InvalidOperationException>(
                    () => history.EnsureTargets(device, 128, 72, 64, 36));
                Assert.Contains("the window's frame list", resize.Message);
                using var other = new FakeGpuDevice();   // a device change drains the old device, which is recording
                Assert.Throws<InvalidOperationException>(() => history.EnsureTargets(other, 96, 54, 64, 36));
            }

            Assert.Equal(0, device.WaitForIdleCalls);
            Assert.Equal(6, factory.Textures.Count);
            AssertNoneFreed(factory.Textures, "a refused release freed the targets");
            Assert.True(history.TargetsAllocated);
            Assert.Equal(generation, history.TargetGeneration);
            Assert.Equal((1, 0), (history.ReadIndex, history.WriteIndex));
            Assert.Equal((96, 54, 64, 36),
                (history.DisplayWidth, history.DisplayHeight, history.InternalWidth, history.InternalHeight));

            history.ReleaseTargets();   // outside the recording the drained release goes ahead
            Assert.Equal(1, device.WaitForIdleCalls);
            Assert.All(factory.Textures, t => Assert.True(t.Disposed));
        }

        [Fact]
        public void With_a_queue_a_release_while_the_device_records_retires_and_never_drains()
        {
            using var device = new FakeGpuDevice();
            using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
            var history = new TemporalHistory();
            history.EnsureTargets(device, 96, 54, 64, 36, retired);

            using (GpuRecording.Open(device, device.Factory.CreateCommandList(), "the window's frame list"))
            {
                Assert.True(history.EnsureTargets(device, 128, 72, 64, 36, retired));
                history.ReleaseTargets(retired);
            }

            Assert.Equal(0, device.WaitForIdleCalls);
            Assert.Equal(20, retired.PendingCount);
        }

        [Fact]
        public void Every_creation_and_every_release_bumps_the_generation_once()
        {
            using var device = new FakeGpuDevice();
            var history = new TemporalHistory();
            Assert.Equal(0, history.TargetGeneration);

            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.Equal(1, history.TargetGeneration);
            Assert.False(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.Equal(1, history.TargetGeneration);

            history.ReleaseTargets();
            Assert.Equal(2, history.TargetGeneration);   // a release alone is visible to every set built over the targets
            history.ReleaseTargets();
            Assert.Equal(2, history.TargetGeneration);   // nothing was left to release
            Assert.True(history.EnsureTargets(device, 96, 54, 64, 36));
            Assert.Equal(3, history.TargetGeneration);
            Assert.True(history.EnsureTargets(device, 128, 72, 64, 36));
            Assert.Equal(4, history.TargetGeneration);   // a recreation replaces the targets in one step

            using var other = new FakeGpuDevice();       // a new device holds none of the old targets
            Assert.True(history.EnsureTargets(other, 96, 54, 64, 36));
            Assert.Equal(5, history.TargetGeneration);
            history.ReleaseTargets();
            Assert.Equal(6, history.TargetGeneration);
        }

        [Fact]
        public void A_history_release_without_a_recreation_moves_the_display_targets_generation()
        {
            using var device = new FakeGpuDevice();
            using var res = new RenderResources(device, 64, 36, hdrColor: true);
            var history = new TemporalHistory();
            using var post = new TemporalPostTargets(device);
            history.EnsureTargets(device, 96, 54, 64, 36);
            post.Ensure(res, history, 96, 54, bloomEnabled: false);
            int generation = post.Generation;

            history.ReleaseTargets();

            Assert.NotEqual(generation, post.Generation);
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
