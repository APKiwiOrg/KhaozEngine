using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSpirvProfileTests
{
    [Theory]
    [InlineData(48, 1)]
    [InlineData(96, 2)]
    public void Profile_counts_executed_helpers_and_loop_iterations_without_inclusive_double_counting(int extent, int groups)
    {
        const string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) buffer OutputBuf { uint Outputs[]; };
            uint leaf(uint value) { return value + 1u; }
            uint covExactWorker(uint value) {
                for (uint i = 0u; i < 3u; i++) value = leaf(value);
                return value;
            }
            void main() {
                uint i = gl_GlobalInvocationID.x;
                if (i >= Outputs.length()) return;
                Outputs[i] = leaf(covExactWorker(i));
            }
            """;
        // A runtime array length is outside this bounded executor. Keep the extent an input constant instead.
        string bounded = source.Replace("Outputs.length()", extent + "u", System.StringComparison.Ordinal);
        uint[] result = new uint[extent];
        var executor = new CoverageSpirvExecutor(bounded, "profile counter control", new[] { result });
        executor.RunGroups(groups);
        var profile = executor.InstructionProfile.ToArray();
        Assert.Equal(groups * 64, profile.Single(f => f.Name == "main").Calls);
        Assert.Equal(0, profile.Single(f => f.Name == "main").ExactInstructions);
        Assert.Equal(extent, profile.Single(f => f.Name == "covExactWorker").Calls);
        var leaf = profile.Single(f => f.Name == "leaf");
        Assert.Equal(4 * extent, leaf.Calls);
        // OpLabel, OpLoad, OpIAdd, OpReturnValue. Each executes once per call.
        Assert.Equal(4 * 4 * extent, leaf.Instructions);
        Assert.Equal(4 * 3 * extent, leaf.ExactInstructions);
        Assert.Equal(Enumerable.Range(4, extent).Select(i => (uint)i), result);
    }
}
