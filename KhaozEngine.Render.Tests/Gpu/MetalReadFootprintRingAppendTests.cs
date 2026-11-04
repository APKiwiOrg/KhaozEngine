using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalReadFootprintRingAppendTests(ITestOutputHelper output)
{
    // #1696 round 2. Every draw changes both dynamic elements, as the game's uniform ring does.
    [Fact]
    public void FreshAlignedSlicesHaveConstantRecordingWorkWithoutRotationsOrHashing()
    {
        using var f = new MetalReadFootprintDrawFixture(freshSlices: true);
        f.Begin();
        f.Draw(0);
        long probes = f.List.ReadProbeStepsForTests, writes = f.List.ReadNodeWritesForTests;
        long rotations = f.List.ReadRotationsForTests, hashes = f.List.ReadHashLookupsForTests;
        long shaders = f.List.ReadShaderLookupsForTests;
        long hotHashes = 0, hotRotations = 0, hotShaders = 0;
        for (uint draw = 1; draw < 3000; draw++)
        {
            long before = f.List.ReadHashLookupsForTests;
            long rotationsBefore = f.List.ReadRotationsForTests;
            long shaderBefore = f.List.ReadShaderLookupsForTests;
            f.Draw(draw);
            if (draw % 500 != 0 && draw % 750 != 0)
            {
                hotHashes += f.List.ReadHashLookupsForTests - before;
                hotRotations += f.List.ReadRotationsForTests - rotationsBefore;
                hotShaders += f.List.ReadShaderLookupsForTests - shaderBefore;
            }
        }
        probes = f.List.ReadProbeStepsForTests - probes;
        writes = f.List.ReadNodeWritesForTests - writes;
        rotations = f.List.ReadRotationsForTests - rotations;
        hashes = f.List.ReadHashLookupsForTests - hashes;
        shaders = f.List.ReadShaderLookupsForTests - shaders;
        output.WriteLine($"2999 fresh-slice draws: probes {probes}, writes {writes}, rotations {rotations}, hash lookups {hashes}, shader lookups {shaders}");
        // Two append writes plus exact instance extensions. Allow rare mesh/static-set rebinds separately.
        Assert.InRange(probes, 1, 2999 * 12 + 256);
        Assert.InRange(writes, 1, 2999 * 12 + 256);
        Assert.InRange(rotations, 0, 12); // Only first reads of three newly bound meshes may rotate.
        Assert.Equal(0, hotRotations);
        Assert.Equal(0, hotHashes);
        Assert.Equal(0, hotShaders);
        Assert.InRange(hashes, 0, 64);
        f.List.End();
    }
}
