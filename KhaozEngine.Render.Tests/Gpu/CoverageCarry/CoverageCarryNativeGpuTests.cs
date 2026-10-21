using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageCarryNativeGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void F0_carry_matches_every_reference_word_with_zero_weights_aliases_and_current_history()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        var device = context.GpuDevice;
        int comparisons = 0;
        foreach (bool zeroWords in new[] { false, true })
        {
            var fixture = new CoverageCarryFixture(zeroWords);
            using var carry = new CoverageCarryDevice(device, fixture, 32);
            Assert.Equal(32, carry.Actors.Select(a => a.Commands[0].EquipmentInstanceId).Distinct().Count());
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
                var beforeByPose = new ModelVertex[2][];
                var expectedWords = new uint[2][];
                var beforeWords = new uint[2][];
                for (int actor = 0; actor < carry.Count; actor++)
                {
                    var input = carry.Actors[actor];
                    int pose = actor % 2;
                    if (expectedByPose[pose] is null)
                    {
                        var scratch = new CoverageReferenceScratch(fixture.Topology);
                        var expected = new ModelVertex[fixture.Topology.VertexCount];
                        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, input.Palette, input.Commands,
                            input.Anchor, scratch, expected, out var failure), failure.ToString());
                        expectedByPose[pose] = expected;
                        beforeByPose[pose] = scratch.Before;
                        expectedWords[pose] = CoverageCarryReferenceTests.Words(expected);
                        beforeWords[pose] = CoverageCarryReferenceTests.Words(scratch.Before);
                    }
                    var candidate = expectedByPose[pose];
                    var before = beforeByPose[pose];
                    AssertWords(expectedWords[pose], carry.Surface(actual, actor), actor, frame, "current");
                    AssertWords(beforeWords[pose], carry.Before(work, actor), actor, frame, "before");
                    AssertWords(frame > 0 ? previous[actor] : new uint[candidate.Length * 16],
                        carry.Surface(history, actor), actor, frame, "previous");
                    previous[actor] = carry.Surface(actual, actor);
                    var status = carry.Results(results, actor, frame);
                    Assert.Equal(0u, status[6]);
                    Assert.Equal((uint)frame + 1, status[7]);
                    Assert.Equal(CoverageCarryBounds.Of(candidate), status.AsSpan(0, 6).ToArray());
                    for (int v = 0; v < candidate.Length; v++)
                        if (fixture.Topology.Vertices[v].Strength == 0 || weight == 0)
                            Assert.Equal(before[v].Position, candidate[v].Position);
                    comparisons += candidate.Length * 16;
                }
            }
            Assert.Equal(anchors, carry.Read(6));
        }
        output.WriteLine($"{CoverageCarryReference.Label}. Compared {comparisons} current words plus Before, previous history, bounds and immutable anchors on {context.Backend}.");
    }

    static void AssertWords(uint[] expected, uint[] actual, int actor, int frame, string surface)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i])
                Assert.Fail($"{surface} actor {actor} frame {frame} vertex {i / 16} field {i % 16}: {expected[i]:x8} != {actual[i]:x8}");
    }
}
