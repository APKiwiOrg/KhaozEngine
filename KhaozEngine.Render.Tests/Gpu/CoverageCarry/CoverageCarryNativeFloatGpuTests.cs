using System;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageCarryNativeFloatGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void F0_native_float_reports_per_actor_error_without_claiming_exactness()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        var device = context.GpuDevice;
        var errors = Enumerable.Range(0, 32).Select(actor => new CoverageCarryError(actor)).ToArray();
        foreach (bool zeroWords in new[] { false, true })
        {
            var fixture = new CoverageCarryFixture(zeroWords);
            using var carry = new CoverageCarryDevice(device, fixture, 32, nativeFloat: true);
            var previous = new uint[32][];
            var anchors = carry.Read(6);
            for (int frame = 0; frame < 4; frame++)
            {
                float weight = frame switch { 0 => 1, 1 => .375f, 2 => BitConverter.UInt32BitsToSingle(0x80000000u), _ => 1 };
                carry.Prepare(frame, weight, rotate: frame == 3);
                carry.PoisonScratch();
                using (var commands = device.Factory.CreateCommandList())
                {
                    commands.Begin(); carry.Record(commands); commands.End();
                    device.Submit(commands); device.WaitForIdle();
                }
                uint[] actual = carry.Read(3), history = carry.Read(4), work = carry.Read(2), results = carry.Read(5);
                var expectedByPose = new ModelVertex[2][];
                for (int actor = 0; actor < carry.Count; actor++)
                {
                    var input = carry.Actors[actor];
                    int pose = actor % 2;
                    var expected = expectedByPose[pose] ?? new ModelVertex[fixture.Topology.VertexCount];
                    if (expectedByPose[pose] is null)
                        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, input.Palette, input.Commands,
                            input.Anchor, new(fixture.Topology), expected, out var failure), failure.ToString());
                    uint[] current = carry.Surface(actual, actor), before = carry.Before(work, actor);
                    errors[actor].Include(expected, current);
                    Assert.Equal(frame > 0 ? previous[actor] : new uint[current.Length], carry.Surface(history, actor));
                    previous[actor] = current;
                    var status = carry.Results(results, actor, frame);
                    Assert.Equal(0u, status[6]);
                    Assert.Equal((uint)frame + 1, status[7]);
                    Assert.Equal(CoverageCarryBounds.OfWords(current), status.AsSpan(0, 6).ToArray());
                    for (int vertex = 0; vertex < fixture.Topology.VertexCount; vertex++)
                        if (fixture.Topology.Vertices[vertex].Strength == 0 || weight == 0)
                            Assert.Equal(before.AsSpan(vertex * 16, 3).ToArray(), current.AsSpan(vertex * 16, 3).ToArray());
                    expectedByPose[pose] = expected;
                }
            }
            Assert.Equal(anchors, carry.Read(6));
        }
        output.WriteLine("COVERAGE_CARRY_NATIVE_FLOAT_ERRORS_JSON=" + JsonSerializer.Serialize(new
        {
            label = CoverageCarryNativeShader.Label,
            backend = context.Backend.ToString(),
            metric = "Maximum Euclidean XYZ difference and difference/reference-length ratio. Relative error at a zero reference is undefined and counted separately. Tangent W is separate.",
            actors = errors,
        }));
    }
}
