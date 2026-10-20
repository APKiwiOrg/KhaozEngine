using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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
    [InlineData("void Copy(uint @offset) {", "offset")]
    [InlineData("void Copy(uint offset) {", "@offset")]
    [InlineData("void Copy(uint value) { var offset = value;", "offset")]
    public void Runtime_validation_uses_the_bound_uint_symbol(string prefix, string expression)
        => Assert.True(ProvesStorage(prefix, expression, ""));

    [Fact]
    public void Validation_and_copy_can_use_different_spellings_of_the_same_symbol()
        => Assert.True(Proves("GpuCopyAlignment.RequireAlignedOffset(@offset, nameof(offset), \"copy\", \"source\");"));

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
    [InlineData("uint this[int i] { get => count; set => count = value; } void Copy() {", "this[0]", "Mutate();")]
    [InlineData("uint offset; void Copy() {", "this.offset", "Mutate();")]
    [InlineData("void Copy(Holder holder) {", "holder.Offset", "Mutate();")]
    [InlineData("void Copy() {", "Offsets.Current", "Mutate();")]
    public void Aliased_or_member_storage_is_not_a_runtime_proof(string prefix, string expression, string after)
        => Assert.False(ProvesStorage(prefix, expression, after));

    [Theory]
    [InlineData("void Copy(uint offset) {", "Mutate(ref @offset);")]
    [InlineData("void Copy(uint offset) {", "Mutate(out @offset);")]
    [InlineData("void Copy(uint offset) { System.Action mutate = () => offset = 1;", "mutate();")]
    [InlineData("void Copy(uint offset) { void Mutate() { offset = 1; }", "Mutate();")]
    [InlineData("unsafe void Copy(uint offset) { uint* alias = &@offset;", "*alias = 1;")]
    public void Escaped_writes_captures_and_unsafe_aliases_are_not_a_runtime_proof(string prefix, string after)
        => Assert.False(ProvesStorage(prefix, "offset", after));

    [Theory]
    [InlineData("void Copy(uint offset) { ref uint alias = ref offset; GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); alias = 1; commands.CopyBuffer(source, offset, target, 0, count); }")]
    [InlineData("void Copy(uint offset) { GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); commands.CopyBuffer(GetSource(out offset), offset, target, 0, count); }")]
    [InlineData("void Copy(uint offset) { GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); commands.CopyBuffer(source, offset, target, 0, count); Inspect(in offset); }")]
    [InlineData("void Copy(uint offset, uint other, bool ready) { ref uint alias = ref (ready ? ref offset : ref other); GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); alias = 1; commands.CopyBuffer(source, offset, target, 0, count); }")]
    [InlineData("void Copy(uint offset) { var alias = __makeref(offset); GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); __refvalue(alias, uint) = 1; commands.CopyBuffer(source, offset, target, 0, count); }")]
    [InlineData("void Copy(uint offset) { GpuCopyAlignment.RequireAlignedOffset(offset, (offset = 1).ToString(), \"copy\", \"source\"); commands.CopyBuffer(source, offset, target, 0, count); }")]
    public void Ref_taking_and_writes_in_validation_or_copy_statements_are_not_a_runtime_proof(string source)
        => Assert.False(ProvesSource("offset", source));

    [Theory]
    [InlineData("in", RefKind.In, false)]
    [InlineData("ref readonly", RefKind.RefReadOnlyParameter, false)]
    [InlineData("in", RefKind.In, true)]
    public void Implicit_readonly_arguments_are_not_a_runtime_proof(string modifier, RefKind expected, bool nameOf)
    {
        string parameterName = nameOf ? "nameof(offset)" : "\"offset\"";
        string source = BoundSource("static void Change(" + modifier + " uint value) { "
            + "System.Runtime.CompilerServices.Unsafe.AsRef(in value) = 1; } "
            + "void Copy(uint offset) { GpuCopyAlignment.RequireAlignedOffset(offset, " + parameterName + ", \"copy\", \"source\"); "
            + "Change(offset); commands.CopyBuffer(source, offset, target, 0, count); }");
        SemanticModel model = BoundModel(source);
        InvocationExpressionSyntax call = model.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(node => node.Expression.ToString() == "Change");
        IInvocationOperation operation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(call));
        Assert.Equal(expected, Assert.Single(operation.Arguments).Parameter!.RefKind);
        InvocationExpressionSyntax asRef = model.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(node => node.Expression.ToString().EndsWith("Unsafe.AsRef", StringComparison.Ordinal));
        IInvocationOperation mutation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(asRef));
        Assert.Equal("System.Runtime.CompilerServices.Unsafe", mutation.TargetMethod.ContainingType.ToDisplayString());
        Assert.True(mutation.TargetMethod.ReturnsByRef);
        Assert.False(ProvesBoundSource(source));
    }

    [Fact]
    public void An_instance_receiver_is_not_a_runtime_proof()
    {
        string source = BoundSource("void Copy(uint offset) { "
            + "GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); "
            + "offset.ToString(); commands.CopyBuffer(source, offset, target, 0, count); }");
        SemanticModel model = BoundModel(source);
        InvocationExpressionSyntax call = model.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(node => node.Expression.ToString() == "offset.ToString");
        IInvocationOperation operation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(call));
        IParameterReferenceOperation receiver = Assert.IsAssignableFrom<IParameterReferenceOperation>(operation.Instance);
        Assert.Equal(SpecialType.System_UInt32, receiver.Parameter.Type.SpecialType);
        Assert.False(ProvesBoundSource(source));
    }

    [Theory]
    [InlineData("void Copy(uint offset) {", "uint value = offset;", "")]
    [InlineData("void Copy(uint offset) {", "uint value = offset + 4;", "")]
    [InlineData("void Copy(uint offset) {", "uint value = ~offset;", "")]
    [InlineData("void Copy(uint offset) {", "ulong value = offset;", "")]
    [InlineData("void Copy(uint offset) {", "string value = $\"{offset}\";", "")]
    [InlineData("void Copy(uint offset) {", "uint value = 0; value = offset;", "")]
    [InlineData("void Copy(uint offset) {", "uint value = 0; value += offset;", "")]
    [InlineData("uint Copy(uint offset) {", "", "return offset;")]
    [InlineData("void Copy(uint offset) { offset = 0;", "", "")]
    [InlineData("void Copy(uint offset) {", "", "offset += 4;")]
    [InlineData("void Copy(uint offset) {", "", "offset++;")]
    [InlineData("static void Change(uint value) { } void Copy(uint offset) {", "Change(offset);", "")]
    public void Value_contexts_and_writes_outside_the_validation_range_remain_provable(string prefix, string between, string after)
    {
        string source = BoundSource(prefix + " GpuCopyAlignment.RequireAlignedOffset(offset, nameof(offset), \"copy\", \"source\"); "
            + between + " commands.CopyBuffer(source, offset, target, 0, count); " + after + " }");
        BoundModel(source);
        Assert.True(ProvesBoundSource(source));
    }

    [Fact]
    public void An_unbound_call_is_not_a_runtime_proof()
    {
        string source = BoundSource("void Copy(uint offset) { "
            + "GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); "
            + "Unknown(offset); commands.CopyBuffer(source, offset, target, 0, count); }");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        Assert.Contains(CopyBufferAlignmentProof.CreateCompilation(tree).GetDiagnostics(), diagnostic => diagnostic.Id == "CS0103");
        Assert.False(CopyBufferAlignmentProof.Accepts("offset", source,
            source.IndexOf("commands.CopyBuffer", StringComparison.Ordinal), Helper));
    }

    [Theory]
    [InlineData("void Copy(ulong offset) {")]
    [InlineData("void Copy() { int offset = 0;")]
    public void Non_uint_storage_is_not_a_runtime_proof(string prefix)
        => Assert.False(ProvesStorage(prefix, "offset", ""));

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
        Assert.False(ProvesSource("offset", source));
    }

    [Fact]
    public void Validation_after_the_copy_is_not_a_proof()
    {
        string source = "void Copy(uint offset) { commands.CopyBuffer(source, offset, target, 0, count); "
            + "GpuCopyAlignment.RequireAlignedOffset(offset, \"offset\", \"copy\", \"source\"); }";
        Assert.False(ProvesSource("offset", source));
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

    static string BoundSource(string methods)
        => "class Commands { public void CopyBuffer(object source, uint offset, object target, uint destination, uint count) { } "
            + "public void CopyBuffer(object source, int offset, object target, uint destination, uint count) { } "
            + "public void CopyBuffer(object source, ulong offset, object target, uint destination, uint count) { } } "
            + "static class GpuCopyAlignment { public static void RequireAlignedOffset(ulong offset, string name, string what, string side) { } "
            + "public static void RequireAlignedOffset(int offset, string name, string what, string side) { } } "
            + "class CallSite { Commands commands = new(); object source = new(), target = new(); uint count = 4; "
            + "uint other = 4; bool ready = true; (uint, uint) values = (1, 2); "
            + "class MutableOffset { public uint Value; public static implicit operator uint(MutableOffset value) => value.Value; } "
            + "class Holder { public uint Offset; } static class Offsets { public static uint Current; } "
            + "void Mutate() { count = 1; } void Mutate(ref uint value) { value = 1; } "
            + "void Mutate(out uint value, bool unused = false) { value = 1; } "
            + "object GetSource(out uint value) { value = 1; return source; } void Inspect(in uint value) { _ = value; } "
            + "void Change(ref uint value, bool unused = false) { value = 1; } "
            + "void Change(out uint value, bool unused = false, bool alsoUnused = false) { value = 1; } "
            + methods + " }";

    static SemanticModel BoundModel(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CopyBufferAlignmentProof.CreateCompilation(tree);
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return compilation.GetSemanticModel(tree);
    }

    static bool ProvesBoundSource(string source, string expression = "offset")
    {
        BoundModel(source);
        return CopyBufferAlignmentProof.Accepts(expression, source,
            source.IndexOf("commands.CopyBuffer", StringComparison.Ordinal), Helper);
    }

    static bool Proves(string prefix)
    {
        string source = "unsafe void Copy(uint offset) {\n" + prefix + "\ncommands.CopyBuffer(source, offset, target, 0, count); }";
        return ProvesSource("offset", source);
    }

    static bool ProvesStorage(string prefix, string expression, string after)
    {
        string source = prefix + " GpuCopyAlignment.RequireAlignedOffset(" + expression
            + ", \"offset\", \"copy\", \"source\"); " + after + " commands.CopyBuffer(source, "
            + expression + ", target, 0, count); }";
        return ProvesSource(expression, source);
    }

    static bool ProvesSource(string expression, string source)
        => ProvesBoundSource(BoundSource(source), expression);
}
