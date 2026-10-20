using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CopyBufferAlignmentProofTests
{
    const string Helper = "internal static uint Bytes(uint words) => checked(words * sizeof(uint));";

    [Fact]
    public void Runtime_validation_proves_an_unchanged_offset_in_the_same_method()
        => Assert.True(Proves("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\");"));

    [Theory]
    [InlineData("")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(other, nameof(other), \"copy\", \"source\");")]
    [InlineData("if (ready) GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\");")]
    [InlineData("if (ready) { GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); }")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); offset++;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); Change(ref offset);")]
    [InlineData("try { GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); } catch { }")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); offset >>= 1;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); (offset, other) = values;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); Change(); void Change() { offset++; }")]
    [InlineData("#if DEBUG\nGpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\");\n#endif\n")]
    public void Unproven_or_bypassed_runtime_validation_is_refused(string prefix)
        => Assert.False(Proves(prefix));

    [Fact]
    public void Validation_in_another_method_is_not_a_proof()
    {
        string source = "void Validate(uint offset) { GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); } "
            + "void Copy(uint offset) { commands.CopyBuffer(source, offset, target, 0, count); }";
        Assert.False(CopyBufferAlignmentProof.Accepts("offset", source, source.IndexOf("commands.CopyBuffer", StringComparison.Ordinal), Helper));
    }

    [Fact]
    public void Validation_after_the_copy_is_not_a_proof()
    {
        string source = "void Copy(uint offset) { commands.CopyBuffer(source, offset, target, 0, count); "
            + "GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); }";
        Assert.False(CopyBufferAlignmentProof.Accepts("offset", source, source.IndexOf("commands.CopyBuffer", StringComparison.Ordinal), Helper));
    }

    [Fact]
    public void Checked_word_to_byte_conversion_proves_alignment()
        => Assert.True(CopyBufferAlignmentProof.Accepts("CoverageGpuLayout.Bytes(reservation.CurrentBase)", "", 0, Helper));

    [Theory]
    [InlineData("CoverageGpuLayout.Bytes(words) + 1", Helper)]
    [InlineData("OtherLayout.Bytes(words)", Helper)]
    [InlineData("CoverageGpuLayout.Bytes(words)", "internal static uint Bytes(uint words) => words * sizeof(uint);")]
    [InlineData("CoverageGpuLayout.Bytes(words)", "internal static uint Bytes(uint words) => checked(words * 2);")]
    [InlineData("CoverageGpuLayout.Bytes(words)", "internal static uint Bytes(uint words) => checked(words * sizeof(uint) + 1);")]
    [InlineData("CoverageGpuLayout.Bytes(words)", Helper + " internal static uint Bytes(long words) => 1;")]
    [InlineData("CoverageGpuLayout.Bytes(words)", "#if DEBUG\n" + Helper + "\n#endif")]
    public void Unproven_word_expressions_and_helper_drift_are_refused(string expression, string helper)
        => Assert.False(CopyBufferAlignmentProof.Accepts(expression, "", 0, helper));

    static bool Proves(string prefix)
    {
        string source = "void Copy(uint offset) { " + prefix + " commands.CopyBuffer(source, offset, target, 0, count); }";
        return CopyBufferAlignmentProof.Accepts("offset", source, source.IndexOf("commands.CopyBuffer", StringComparison.Ordinal), Helper);
    }
}
