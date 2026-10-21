using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageGpuCopyAlignmentProofTests
{
    const string Helper = "internal static uint Bytes(uint words) => checked(words * sizeof(uint));";

    [Fact]
    public void Checked_word_to_byte_conversion_proves_alignment()
        => Assert.True(CoverageGpuCopyAlignmentProof.Accepts("CoverageGpuLayout.Bytes(reservation.CurrentBase)", Helper));

    [Theory]
    [InlineData("CoverageGpuLayout.Bytes(words) + 1", Helper)]
    [InlineData("OtherLayout.Bytes(words)", Helper)]
    [InlineData("CoverageGpuLayout.Bytes(words)", "internal static uint Bytes(uint words) => words * sizeof(uint);")]
    [InlineData("CoverageGpuLayout.Bytes(words)", "internal static uint Bytes(uint words) => checked(words * 2);")]
    [InlineData("CoverageGpuLayout.Bytes(words)", "internal static uint Bytes(uint words) => checked(words * sizeof(uint) + 1);")]
    [InlineData("CoverageGpuLayout.Bytes(words)", Helper + " internal static uint Bytes(long words) => 1;")]
    [InlineData("CoverageGpuLayout.Bytes(words)", "#if DEBUG\n" + Helper + "\n#endif")]
    public void Unproven_word_expressions_and_helper_drift_are_refused(string expression, string helper)
        => Assert.False(CoverageGpuCopyAlignmentProof.Accepts(expression, helper));
}
