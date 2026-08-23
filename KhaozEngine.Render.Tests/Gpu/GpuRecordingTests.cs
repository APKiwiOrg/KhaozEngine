using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The seam's open-recording register (<see cref="GpuRecording"/>), which is where the portable
    /// one-open-recording-per-device contract stopped being a paragraph and started being a refusal
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
    /// <para>
    /// Every assertion here is device-free on purpose. The fault it replaced reproduced on the deleted Veldrid
    /// Direct3D11 leg's immediate-context mode, on hardware the dev machine does not have, several draws after
    /// the call that caused it, and never in an image. A rule that can only be checked there is a rule nobody
    /// checks, which is why the register outlived the leg (#690) and these tests do too.
    /// </para>
    /// </summary>
    // A zero-allocation reading measures its neighbours too (#264), and the concurrent-open row below is exactly
    // the kind of neighbour that churns the GC, so the whole class sits off the parallel pool (#721).
    [Collection("AllocSensitive")]
    public sealed class GpuRecordingTests
    {
        [Fact]
        public void A_second_recording_on_one_device_is_refused_and_names_both_sides()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            using IGpuCommandList ownList = device.Factory.CreateCommandList();

            using (GpuRecording.Open(device, frameList, "the window's frame list"))
            {
                var ex = Assert.Throws<GpuNestedRecordingException>(
                    () => GpuRecording.Open(device, ownList, "Scene3D.LoadTexture"));

                Assert.Equal("the window's frame list", ex.Owner);
                Assert.Equal("Scene3D.LoadTexture", ex.Attempted);
                Assert.Contains("the window's frame list", ex.Message);
                Assert.Contains("Scene3D.LoadTexture", ex.Message);
            }

            // The refused list was never begun, so the count is the frame's alone. A refusal that had already
            // called Begin would have done the damage it exists to prevent.
            Assert.Equal(1, device.Begins);
            Assert.Equal(1, device.PeakOpenLists);
        }

        /// <summary>
        /// THE REFUSAL JUSTIFIES ITSELF WITH THE PORTABLE RULE, NEVER WITH ONE BACKEND'S MECHANISM. The message
        /// used to explain the rule by describing the deleted Veldrid Direct3D11 leg's immediate-context mode,
        /// which was the incumbent's mechanism and is no backend's now. A developer who reads a backend name in
        /// a portable refusal reasonably concludes the rule is that backend's, which is exactly how a seam rule
        /// decays into "the one machine I have tolerates it" (#690 retires that framing with the leg it named).
        /// What the message owes the reader is the rule and the FIX, both asserted here.
        /// </summary>
        [Fact]
        public void The_refusal_names_the_portable_rule_and_the_fix_and_no_single_backend()
        {
            string message = GpuNestedRecordingException.BuildMessage("the window's frame list", "the ocean prime");

            Assert.Contains("ONE OPEN RECORDING PER DEVICE", message);
            Assert.Contains("onPrepare", message);
            Assert.Contains("Scene3D.PrepareFrame", message);

            foreach (string backend in new[] { "Direct3D11", "Direct3D 11", "immediate-context", "Metal", "Vulkan" })
                Assert.DoesNotContain(backend, message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The pattern the engine's own renderers use everywhere: own list, sequentially, outside anyone else's
        /// recording. It must stay free, or the guard would have broken the ocean prime, the preview and the
        /// retire barrier while fixing the nesting.
        /// </summary>
        [Fact]
        public void Sequential_recordings_on_one_device_are_the_legitimate_pattern()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList a = device.Factory.CreateCommandList();
            using IGpuCommandList b = device.Factory.CreateCommandList();

            using (GpuRecording.Open(device, a, "a producer's own pass")) { }
            using (GpuRecording.Open(device, b, "the frame's list")) { }
            using (GpuRecording.Open(device, a, "the same list again next frame")) { }

            Assert.Equal(3, device.Begins);
            Assert.Equal(1, device.PeakOpenLists);
            Assert.Equal(0, device.OpenLists);
        }

        /// <summary>
        /// AND THEY DO NOT WAIT ON EACH OTHER EITHER, which is a stronger claim than the entries being separate
        /// and is the one that was false when the register shipped: a single process-wide lock was held across
        /// the backend's <c>Begin</c>, and Begin blocks by design on the engine's own backends while the GPU
        /// catches up. So device two's frame paid device one's backpressure, measured at half a second in the
        /// review's probe. Here device one is parked inside its Begin and device two must still get through.
        /// </summary>
        [Fact]
        public void A_device_stalled_inside_begin_does_not_hold_up_another_device()
        {
            using var stalled = new OpenListTrackingGpuDevice();
            using var other = new OpenListTrackingGpuDevice();
            using IGpuCommandList stalledList = stalled.Factory.CreateCommandList();
            using IGpuCommandList otherList = other.Factory.CreateCommandList();

            using var insideBegin = new ManualResetEventSlim(false);
            using var letGo = new ManualResetEventSlim(false);
            using var secondDone = new ManualResetEventSlim(false);
            stalled.BeforeBegin = () => { insideBegin.Set(); letGo.Wait(TimeSpan.FromSeconds(20)); };

            Exception? fault = null;
            Thread parked = Start(() =>
            {
                try { using (GpuRecording.Open(stalled, stalledList, "the stalled device's frame")) { } }
                catch (Exception ex) { fault = ex; }
            }, null);
            Assert.True(insideBegin.Wait(TimeSpan.FromSeconds(20)), "the first open never reached Begin");

            Exception? secondFault = null;
            Thread second = Start(() =>
            {
                try { using (GpuRecording.Open(other, otherList, "the other device's frame")) { } }
                catch (Exception ex) { secondFault = ex; }
            }, secondDone);
            Assert.True(secondDone.Wait(TimeSpan.FromSeconds(5)), "the second device waited on the first one's Begin");

            letGo.Set();
            Assert.True(parked.Join(TimeSpan.FromSeconds(20)));
            second.Join(TimeSpan.FromSeconds(5));
            Assert.Null(fault);
            Assert.Null(secondFault);
            Assert.Equal(1, stalled.Begins);
            Assert.Equal(1, other.Begins);
        }

        /// <summary>
        /// The other half of the same change: making the gate per device must not weaken the refusal. Threads race
        /// to open on ONE device, and exactly one of them may be recording at any instant, so every loser gets a
        /// refusal rather than a second open list.
        /// </summary>
        [Fact]
        public void Concurrent_opens_on_one_device_still_leave_exactly_one_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            const int Rounds = 200, Threads = 4;
            var lists = new IGpuCommandList[Threads];
            for (int i = 0; i < Threads; i++) lists[i] = device.Factory.CreateCommandList();

            var refusals = 0;
            Parallel.For(0, Threads, t =>
            {
                for (int round = 0; round < Rounds; round++)
                {
                    try
                    {
                        using (GpuRecording.Open(device, lists[t], "racer " + t.ToString(CultureInfo.InvariantCulture))) { }
                    }
                    catch (GpuNestedRecordingException)
                    {
                        Interlocked.Increment(ref refusals);
                    }
                }
            });

            Assert.Equal(1, device.PeakOpenLists);
            Assert.Equal(0, device.OpenLists);
            Assert.Equal(Threads * Rounds, device.Begins + refusals);
            for (int i = 0; i < Threads; i++) lists[i].Dispose();
        }

        /// <summary>
        /// ZERO STEADY-STATE ALLOCATION, asserted rather than reasoned about, because everything that keeps it
        /// zero is easy to break by accident: the scope is a readonly struct, the slot factory is a cached static
        /// lambda, the owner names are literals, and the lock is uncontended. Any per-call allocation at all would
        /// be tens of bytes times ten thousand, so a real regression still fails both passes of the retry below.
        /// </summary>
        [Fact]
        public void Opening_and_closing_allocates_nothing_in_the_steady_state()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList cl = device.Factory.CreateCommandList();

            for (int i = 0; i < 10_000; i++)
                using (GpuRecording.Open(device, cl, "the window's frame list")) { }

            // Retries once before failing (see AllocAssert.NoPerCallAllocation) to ride out an unrelated gen-0
            // collision from the rest of the process, per issue #284.
            AllocAssert.NoPerCallAllocation("10000 open+dispose pairs", () =>
            {
                for (int i = 0; i < 10_000; i++)
                    using (GpuRecording.Open(device, cl, "the window's frame list")) { }
            });
        }

        [Fact]
        public void Two_devices_never_see_each_others_recordings()
        {
            using var one = new OpenListTrackingGpuDevice();
            using var two = new OpenListTrackingGpuDevice();
            using IGpuCommandList a = one.Factory.CreateCommandList();
            using IGpuCommandList b = two.Factory.CreateCommandList();

            using (GpuRecording.Open(one, a, "device one's frame"))
            using (GpuRecording.Open(two, b, "device two's frame"))
            {
                Assert.Equal("device one's frame", GpuRecording.OpenOwner(one));
                Assert.Equal("device two's frame", GpuRecording.OpenOwner(two));
            }
        }

        [Fact]
        public void The_scope_ends_the_list_and_releases_the_claim()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList cl = device.Factory.CreateCommandList();

            Assert.Null(GpuRecording.OpenOwner(device));
            Assert.True(GpuRecording.CanOpen(device));

            GpuRecordingScope scope = GpuRecording.Open(device, cl, "a pass");
            Assert.Equal("a pass", GpuRecording.OpenOwner(device));
            Assert.False(GpuRecording.CanOpen(device));
            Assert.Same(cl, scope.Commands);
            Assert.Equal(1, device.OpenLists);

            scope.Dispose();
            Assert.Null(GpuRecording.OpenOwner(device));
            Assert.Equal(0, device.OpenLists);
        }

        /// <summary>
        /// A device left permanently marked as recording would refuse every later frame for a fault that already
        /// happened, turning one bad frame into a dead session. So the claim is released even when the body throws.
        /// </summary>
        [Fact]
        public void A_body_that_throws_still_releases_the_device()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList cl = device.Factory.CreateCommandList();

            Action faultingPass = () =>
            {
                using (GpuRecording.Open(device, cl, "a pass that faults"))
                    throw new PassFault();
            };
            Assert.Throws<PassFault>(faultingPass);

            Assert.Null(GpuRecording.OpenOwner(device));
            using (GpuRecording.Open(device, cl, "the next frame")) { }
            Assert.Equal(2, device.Begins);
        }

        /// <summary>
        /// A scope is a struct, so nothing stops it being disposed twice. Ending a list that is not recording is
        /// what the native backends throw on, so the second call must reach the list not at all.
        /// </summary>
        [Fact]
        public void Disposing_a_scope_twice_ends_the_list_once()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList cl = device.Factory.CreateCommandList();

            GpuRecordingScope scope = GpuRecording.Open(device, cl, "a pass");
            scope.Dispose();
            scope.Dispose();
            scope.Dispose();

            Assert.Equal(0, device.OpenLists);      // a second End would have driven this negative
            Assert.Null(GpuRecording.OpenOwner(device));

            using (GpuRecording.Open(device, cl, "the next pass")) { }
            Assert.Equal(2, device.Begins);
            Assert.Equal(0, device.OpenLists);
        }

        /// <summary>
        /// The copy hazard proper, and the reason the release is matched against the LIST rather than trusting
        /// whoever calls it. A struct copies silently, so a scope can outlive the recording it named: disposing
        /// that leftover must not end a stale list, and must not release a claim that now belongs to somebody
        /// else entirely.
        /// </summary>
        [Fact]
        public void A_stale_copy_of_a_scope_leaves_the_recording_that_followed_it_alone()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList first = device.Factory.CreateCommandList();
            using IGpuCommandList second = device.Factory.CreateCommandList();

            GpuRecordingScope scope = GpuRecording.Open(device, first, "the first pass");
            GpuRecordingScope stale = scope;        // silently, exactly as an assignment or an argument would
            scope.Dispose();                        // the first pass is over and the device is free

            using (GpuRecording.Open(device, second, "the pass that followed"))
            {
                stale.Dispose();
                Assert.Equal("the pass that followed", GpuRecording.OpenOwner(device));
                Assert.Equal(1, device.OpenLists);
            }

            Assert.Equal(0, device.OpenLists);
            Assert.Null(GpuRecording.OpenOwner(device));
        }

        /// <summary>The not-recording scope a frame loop holds on a frame it decided not to render.</summary>
        [Fact]
        public void The_default_scope_disposes_to_nothing()
        {
            GpuRecordingScope none = default;
            Assert.Null(none.Commands);
            none.Dispose();
            none.Dispose();
        }

        [Fact]
        public void Opening_needs_a_device_a_list_and_a_name()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList cl = device.Factory.CreateCommandList();

            Assert.Throws<ArgumentNullException>(() => GpuRecording.Open(null!, cl, "a pass"));
            Assert.Throws<ArgumentNullException>(() => GpuRecording.Open(device, null!, "a pass"));
            Assert.Throws<ArgumentException>(() => GpuRecording.Open(device, cl, ""));
            Assert.Equal(0, device.Begins);
        }

        /// <summary>
        /// A backend may refuse the Begin for its own reasons (the native Metal list refuses a second Begin on
        /// ITSELF, since it takes a fresh command buffer per recording). The claim is made AFTER the Begin
        /// succeeds, so that refusal must not leave the device marked as recording.
        /// </summary>
        [Fact]
        public void A_backend_that_refuses_the_begin_leaves_nothing_claimed()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var refusing = new ThrowingCommandList();

            Assert.Throws<InvalidOperationException>(() => GpuRecording.Open(device, refusing, "a pass"));
            Assert.Null(GpuRecording.OpenOwner(device));
            Assert.True(GpuRecording.CanOpen(device));
        }

        /// <summary>A background thread for the two concurrency tests, started and handed back. Raw threads rather
        /// than tasks because what is being asked is whether a thread PARKS, and a parked pool thread is a
        /// different question.</summary>
        static Thread Start(Action body, ManualResetEventSlim? done)
        {
            var t = new Thread(() =>
            {
                try { body(); }
                finally { done?.Set(); }
            })
            { IsBackground = true };
            t.Start();
            return t;
        }

        /// <summary>Whatever went wrong inside a pass. Its own type so it cannot be confused with the register's
        /// own <see cref="GpuNestedRecordingException"/>, which derives from <see cref="InvalidOperationException"/>.
        /// </summary>
        sealed class PassFault : Exception { }

        /// <summary>A list whose Begin refuses, standing in for the native Metal backend's own refusal. Drops
        /// everything else, since nothing past the Begin is ever reached.</summary>
        sealed class ThrowingCommandList : IGpuCommandList
        {
            public void Begin() => throw new InvalidOperationException("this backend refuses the Begin.");
            public void End() { }
            public void SetFramebuffer(IGpuFramebuffer fb) { }
            public void ClearColorTarget(uint index, KhaozEngine.Primitives.Color rgba) { }
            public void ClearDepthStencil(float depth) { }
            public void SetPipeline(IGpuPipeline p) { }
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { }
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
            public void SetVertexBuffer(uint slot, IGpuBuffer b) { }
            public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) { }
            public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { }
            public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { }
            public void SetFullScissorRects() { }
            public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { }
            public void Draw(uint vertexCount) { }
            public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
            public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { }
            public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
            public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) { }
            public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
                IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height) { }
            public void GenerateMipmaps(IGpuTexture texture) { }
            public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
            public void SetComputePipeline(IGpuComputePipeline p) { }
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
            public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
            public void Dispose() { }
        }
    }
}
