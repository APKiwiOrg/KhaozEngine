using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The FFT ocean's two-phase frame (#423), checked headless on
    /// <see cref="OpenListTrackingGpuDevice"/> - no GPU, so it runs under a plain <c>dotnet test</c>.
    /// <para>
    /// The bug this locks out was not a wrong picture, it was a wrong ORDER: the priming pass opened, submitted
    /// and drained a command list of its own while the scene's frame list was still recording. On Direct3D11 in
    /// immediate-context mode a command list is the device's immediate context and opening one resets it, so the
    /// frame's bindings went away underneath it and the device faulted several draws later. Nothing about that is
    /// visible in an image, and it reproduces on a backend the dev machine does not have, so the invariant is
    /// asserted directly instead: while one list is recording, nobody opens another. The same shape guards the
    /// seven latent sites in
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>.
    /// </para>
    /// </summary>
    public sealed class OceanFftFramePrepareTests
    {
        const float Dt = 1f / 60f;

        static WaterSettings OceanSettings()
        {
            var settings = new WaterSettings { WaveSource = WaterWaveSource.FftOcean };
            // The smallest sea the producer will build: this test is about frame structure, not about the surface,
            // and the CPU spectrum bake is the only slow part of it.
            settings.SeaState.CascadeCount = 1;
            settings.SeaState.CascadeResolution = 32;
            return settings;
        }

        // One frame the way the water renderer drives it: prepare with nothing open, then record into the frame's
        // list, which is already recording (that is the whole point - Record must be safe there).
        static void Frame(OpenListTrackingGpuDevice device, OceanFftProducer producer, WaterSettings settings,
            float time, IGpuCommandList frameList)
        {
            producer.Prepare(settings, time, wantOcean: true);
            frameList.Begin();
            Assert.True(producer.Record(frameList));
            frameList.End();
            device.Submit(frameList);
        }

        // The harness first. Every assertion below is "the peak never reached 2", which a tracker that counted
        // nothing would also satisfy, so prove it counts: this is the shape the ocean prime had before #423, and
        // the tracker must see it.
        [Fact]
        public void The_tracker_sees_a_nested_begin_when_there_is_one()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList outer = device.Factory.CreateCommandList();
            using IGpuCommandList inner = device.Factory.CreateCommandList();

            outer.Begin();
            inner.Begin();      // what PrimeRowPass used to do from inside the water pass
            inner.End();
            outer.End();

            Assert.Equal(2, device.PeakOpenLists);
            Assert.Equal(0, device.OpenLists);
        }

        [Fact]
        public void No_second_command_list_is_opened_while_the_frame_list_is_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var producer = new OceanFftProducer(device);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            WaterSettings settings = OceanSettings();

            float time = 0f;
            for (int i = 0; i < 4; i++, time += Dt) Frame(device, producer, settings, time, frameList);

            // A re-bake and a wave-clock jump are the other two triggers, and each primes on the frame it lands on.
            settings.SeaState.WindSpeed += 3f;
            Frame(device, producer, settings, time += Dt, frameList);
            Frame(device, producer, settings, time + 5f, frameList);

            Assert.Equal(0, device.OpenLists);
            Assert.Equal(1, device.PeakOpenLists);
        }

        [Fact]
        public void Every_prime_opens_its_list_before_the_frame_records_anything()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var producer = new OceanFftProducer(device);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            WaterSettings settings = OceanSettings();

            int beforePrepare = device.Begins;
            producer.Prepare(settings, 0f, wantOcean: true);
            int afterPrepare = device.Begins;

            frameList.Begin();
            producer.Record(frameList);
            frameList.End();

            // The prime's list opened during Prepare, and Record opened nothing at all beyond the frame's own list.
            Assert.Equal(1, afterPrepare - beforePrepare);
            Assert.Equal(1, producer.LastStallCount);
            Assert.Equal(afterPrepare + 1, device.Begins);   // + the frame list itself
        }

        [Fact]
        public void The_prime_runs_once_per_trigger_and_not_on_a_steady_frame()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var producer = new OceanFftProducer(device);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            WaterSettings settings = OceanSettings();

            float time = 0f;
            Frame(device, producer, settings, time, frameList);
            Assert.Equal(1, producer.LastStallCount);      // first ocean frame: no pending rows to consume

            for (int i = 0; i < 3; i++)
            {
                Frame(device, producer, settings, time += Dt, frameList);
                Assert.Equal(0, producer.LastStallCount);  // steady state: the ping-pong carries the rows
            }

            settings.SeaState.WindSpeed += 3f;             // re-bake: the pending rows came from the old spectrum
            Frame(device, producer, settings, time += Dt, frameList);
            Assert.Equal(1, producer.LastStallCount);

            Frame(device, producer, settings, time += Dt, frameList);
            Assert.Equal(0, producer.LastStallCount);

            // A gap wider than the frame-delta clamp is a discontinuity, not a frame, so the rows are stale.
            Frame(device, producer, settings, time + 5f, frameList);
            Assert.Equal(1, producer.LastStallCount);
        }

        [Fact]
        public void Record_refuses_a_frame_that_was_never_prepared()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var producer = new OceanFftProducer(device);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            frameList.Begin();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => producer.Record(frameList));
            frameList.End();

            // Loud and specific: the failure a host gets for skipping PrepareFrame must name the fix, because the
            // alternative (preparing itself here) is the nested Begin this split exists to remove.
            Assert.Contains("PrepareFrame", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, device.PeakOpenLists);
        }

        [Fact]
        public void A_frame_no_plane_wants_the_ocean_for_records_nothing_and_costs_no_list()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var producer = new OceanFftProducer(device);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            producer.Prepare(OceanSettings(), 0f, wantOcean: false);
            int begins = device.Begins;

            frameList.Begin();
            Assert.False(producer.Record(frameList));
            frameList.End();

            Assert.False(producer.Active);
            Assert.Equal(0, producer.LastStallCount);
            Assert.Equal(begins + 1, device.Begins);        // the frame's own list, nothing else
        }
    }
}
