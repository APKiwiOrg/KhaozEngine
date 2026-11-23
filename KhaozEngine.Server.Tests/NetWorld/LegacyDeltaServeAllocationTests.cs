using System;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.NetWorld.LegacyDeltaServeFixture;

namespace KhaozEngine.Tests.NetWorld;

[Collection("AllocSensitive")]
public class LegacyDeltaServeAllocationTests(ITestOutputHelper output)
{
    private const int WarmTicks = 24;
    private const int MeasuredTicks = 32;

    [Theory]
    [InlineData(false, 8)]
    [InlineData(false, 32)]
    [InlineData(true, 8)]
    [InlineData(true, 32)]
    public void WarmWriterFramingAndEngineSendAllocateOnlyWhenAFrameGrows(bool sharded, int sessions)
    {
        using var rig = new Workload(sharded, sessions);
        for (int i = 0; i < WarmTicks; i++) rig.Tick();

        MeasureFixedFrames(rig, "initial");
        _ = AllocationMeasurement.Measure("larger served frame", () => rig.Tick(words: 1024));
        Assert.True(rig.EngineBytes > 0);

        MeasureFixedFrames(rig, "larger");
        for (int i = 0; i < WarmTicks; i++) rig.Tick();
        _ = AllocationMeasurement.Measure("smaller served frame", () => rig.Tick());
        Assert.Equal(0, rig.EngineBytes);
    }

    [Fact]
    public void AlternatingLargeAndSmallFramesStayWarm()
    {
        using var rig = new Workload(sharded: true, sessions: 8);
        const int largeWords = 20 * 1024;
        for (int i = 0; i < WarmTicks; i++) rig.Tick();
        rig.Tick(largeWords);

        long largest = 0;
        _ = AllocationMeasurement.Measure("alternating large and small served frames", () =>
        {
            for (int i = 0; i < 8; i++)
            {
                rig.Tick(i % 2 == 0 ? 4 : largeWords);
                largest = Math.Max(largest, Math.Abs(rig.EngineBytes));
            }
        }, budget: 16L * 1024 * 1024);
        Assert.Equal(0, largest);
    }

    [Fact]
    public void SustainedSmallFramesReleaseAnOversizeOutlierBuffer()
    {
        using var rig = new Workload(sharded: true, sessions: 8);
        const int largeWords = 20 * 1024;
        for (int i = 0; i < WarmTicks; i++) rig.Tick();
        _ = AllocationMeasurement.Measure("oversize served frame", () => rig.Tick(largeWords));
        Assert.True(rig.EngineBytes > 0);

        // The first small tick cannot evict the buffer. Continued small work eventually does.
        _ = AllocationMeasurement.Measure("first two small ticks after the outlier", () =>
        {
            rig.Tick();
            long first = rig.EngineBytes;
            rig.Tick();
            Assert.Equal(0, first + rig.EngineBytes);
        });
        for (int i = 0; i < WarmTicks; i++) rig.Tick();
        MeasureFixedFrames(rig, "small after release");

        _ = AllocationMeasurement.Measure("oversize served frame after release", () => rig.Tick(largeWords));
        Assert.True(rig.EngineBytes > 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheNextTickAfterAFailedBuildOrSendUsesTheWarmBuffer(bool sharded, bool bodyFailure)
    {
        using var rig = new Workload(sharded, 1);
        for (int i = 0; i < WarmTicks; i++) rig.Tick();
        var fault = new IOException();

        Assert.Same(fault, Record.Exception(() => rig.FaultOneTick(fault, bodyFailure)));
        AllocationMeasurement.Measure("next serve after an exception", () => rig.Tick());

        Assert.True(rig.LoopbackBytes > 0);
        Assert.Equal(0, rig.EngineBytes);
    }

    [Theory]
    [Trait("Category", "Diagnostic")]
    [InlineData(8)]
    [InlineData(32)]
    public void ReportServingPhaseAllocations(int sessions)
    {
        using var rig = new Workload(sharded: true, sessions);
        for (int i = 0; i < WarmTicks; i++) rig.Tick();

        long capture = 0, projection = 0, body = 0, framing = 0, send = 0, loopback = 0;
        for (int i = 0; i < MeasuredTicks; i++)
        {
            rig.Tick();
            capture += rig.Probe[LegacyDeltaPhase.Capture];
            projection += rig.Probe[LegacyDeltaPhase.Projection];
            body += rig.Probe[LegacyDeltaPhase.Body];
            framing += rig.Probe[LegacyDeltaPhase.Framing];
            send += rig.EngineSendBytes;
            loopback += rig.LoopbackBytes;
        }

        output.WriteLine("sessions={0} ticks={1} capture={2} projection={3} body={4} framing={5} send={6} loopback={7}",
            sessions, MeasuredTicks, capture, projection, body, framing, send, loopback);
    }

    private static void MeasureFixedFrames(Workload rig, string size)
    {
        int words = size == "larger" ? 1024 : 4;
        long most = 0;
        _ = AllocationMeasurement.Measure(size + " served frame window", () =>
        {
            for (int i = 0; i < MeasuredTicks; i++)
            {
                rig.Tick(words);
                most = Math.Max(most, Math.Abs(rig.EngineBytes));
            }
        }, budget: 32L * 1024 * 1024);
        Assert.Equal(0, most);
        Assert.True(rig.LoopbackBytes > 0);
    }

    private sealed class Workload : IDisposable
    {
        private readonly LegacyDeltaServeFixture server;
        private readonly (World World, Entity Entity)[] actors = new (World, Entity)[12];
        private int tick;

        internal Workload(bool sharded, int sessions)
        {
            server = new LegacyDeltaServeFixture(sharded, sessions);
            server.Transport.Meter = true;
            server.Writer.LegacyAllocationsForTests = Probe;
            for (int i = 0; i < actors.Length; i++) actors[i] = server.Spawn(new Payload(i, 4));
        }

        internal LegacyDeltaAllocationProbe Probe { get; } = new();
        internal long LoopbackBytes => server.Transport.TransportBytes;
        internal long EngineSendBytes => Probe[LegacyDeltaPhase.Send] - LoopbackBytes;
        internal long EngineBytes => Probe[LegacyDeltaPhase.Serve] - Probe[LegacyDeltaPhase.Capture]
            - Probe[LegacyDeltaPhase.Projection] - LoopbackBytes;

        internal void FaultOneTick(Exception fault, bool bodyFailure)
        {
            if (bodyFailure)
            {
                server.Writer.BeforeLegacyBodyCompletedForTests = () =>
                {
                    server.Writer.BeforeLegacyBodyCompletedForTests = null;
                    throw fault;
                };
            }
            else
            {
                server.Transport.Observe = (_, _, _) =>
                {
                    server.Transport.Observe = null;
                    throw fault;
                };
            }

            Tick();
        }

        internal void Tick(int words = 4)
        {
            tick++;
            for (int i = 0; i < actors.Length; i++)
            {
                (World world, Entity entity) = actors[i];
                world.Set(entity, new Payload(tick + i, i == 0 ? words : 4));
            }

            Probe.Reset();
            server.Transport.TransportBytes = 0;
            server.Tick();
            foreach (Seat seat in server.Clients) seat.Drain();
        }

        public void Dispose() => server.Dispose();
    }
}
