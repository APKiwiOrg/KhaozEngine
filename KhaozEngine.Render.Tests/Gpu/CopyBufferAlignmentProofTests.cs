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
    [InlineData("void Copy(uint value) { uint offset = value;", "")]
    [InlineData("void Copy(ref uint value) { uint offset = value;", "value = 1;")]
    [InlineData("void Copy(uint[] offsets) { uint offset = offsets[0];", "offsets[0] = 1;")]
    [InlineData("uint Offset { get; set; } void Copy() { uint offset = Offset;", "Offset = 1;")]
    [InlineData("uint _offset; void Copy() { uint offset = _offset;", "_offset = 1;")]
    [InlineData("void Copy(uint other, uint offset, bool ready) {", "")]
    public void Runtime_validation_proves_an_independent_scalar_copy(string prefix, string after)
        => Assert.True(ProvesStorage(prefix, "offset", after));

    [Theory]
    [InlineData("void Copy(ref uint offset) {", "offset", "Mutate();")]
    [InlineData("void Copy(in uint offset) {", "offset", "Mutate();")]
    [InlineData("void Copy(out uint offset) { offset = 0;", "offset", "Mutate();")]
    [InlineData("void Copy(ref readonly uint offset) {", "offset", "Mutate();")]
    [InlineData("void Copy(uint[] offsets) { ref uint offset = ref offsets[0];", "offset", "offsets[0] = 1;")]
    [InlineData("void Copy(uint[] offsets) { ref readonly uint offset = ref offsets[0];", "offset", "offsets[0] = 1;")]
    [InlineData("void Copy(uint[] offsets) { ref var offset = ref offsets[0];", "offset", "offsets[0] = 1;")]
    [InlineData("uint offset { get; set; } void Copy() {", "offset", "Mutate();")]
    [InlineData("uint offset; void Copy() {", "offset", "Mutate();")]
    [InlineData("static uint offset; void Copy() {", "offset", "Mutate();")]
    [InlineData("void Copy(MutableOffset offset) {", "offset", "Mutate();")]
    [InlineData("void Copy(MutableOffset value) { var offset = value;", "offset", "Mutate();")]
    [InlineData("void Copy() { uint offset; offset = 0;", "offset", "")]
    [InlineData("void Copy(uint[] offsets) {", "offsets[0]", "offsets[0] = 1;")]
    [InlineData("uint this[int i] { get; set; } void Copy() {", "this[0]", "Mutate();")]
    [InlineData("uint offset; void Copy() {", "this.offset", "Mutate();")]
    [InlineData("void Copy(Holder holder) {", "holder.Offset", "Mutate();")]
    [InlineData("void Copy() {", "Offsets.Current", "Mutate();")]
    public void Aliased_or_member_storage_is_not_a_runtime_proof(string prefix, string expression, string after)
        => Assert.False(ProvesStorage(prefix, expression, after));

    [Theory]
    [InlineData("")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(other, nameof(other), \"copy\", \"source\");")]
    [InlineData("if (ready) GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\");")]
    [InlineData("if (ready) { GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); }")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); offset++;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); Change(ref offset);")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); Change(out offset);")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); uint* alias = &offset;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); offset = 1;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); Change(ref (offset));")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); Change(out (offset));")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); uint* alias = &(offset);")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); (offset)++;")]
    [InlineData("GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); ++(offset);")]
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

    static bool ProvesStorage(string prefix, string expression, string after)
    {
        string source = prefix + " GpuCopyAlignment.RequireAlignedOffset(" + expression
            + ", \"offset\", \"copy\", \"source\"); " + after + " commands.CopyBuffer(source, "
            + expression + ", target, 0, count); }";
        return CopyBufferAlignmentProof.Accepts(expression, source, source.IndexOf("commands.CopyBuffer", StringComparison.Ordinal), Helper);
    }
}
