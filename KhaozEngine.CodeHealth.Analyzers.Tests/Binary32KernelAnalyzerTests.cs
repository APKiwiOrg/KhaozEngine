using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.CodeHealth.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.CodeHealth;

public class Binary32KernelAnalyzerTests
{
    private const string Attributes = """
        namespace Marks
        {
            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class Binary32KernelAttribute : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
            public sealed class Binary32ArithmeticAttribute : System.Attribute { }
        }
        """;

    private static string Source(string members) => """
        using System;
        using System.Collections.Generic;
        using System.Numerics;
        using System.Runtime.Intrinsics;
        using Marks;
        namespace Fixture
        {
            class C
            {
        """ + members + """
            }
            [Binary32Arithmetic] static class Home
            {
                internal static float Add(float a, float b) => a + b;
                internal static float Div(float a, float b) => a / b;
            }
        }
        """ + Attributes;

    private static Task<ImmutableArray<Diagnostic>> Analyze(string members) =>
        KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.RunAnalyzer(
            new Binary32KernelAnalyzer(), "KEB32", ("/repo/Kernel.cs", Source(members)));

    [Theory]
    [InlineData("[Binary32Kernel] static float F(float a, float b) => a + b;")]
    [InlineData("[Binary32Kernel] static double F(double a, double b) => a - b;")]
    [InlineData("[Binary32Kernel] static decimal F(decimal a, decimal b) => a * b;")]
    [InlineData("[Binary32Kernel] static float F(float a, float b) => a / b;")]
    [InlineData("[Binary32Kernel] static float F(float a, float b) => a % b;")]
    [InlineData("[Binary32Kernel] static bool F(float a) => a < 0;")]
    [InlineData("[Binary32Kernel] static bool F(double a, double b) => a >= b;")]
    [InlineData("[Binary32Kernel] static bool F(decimal a, decimal b) => a == b;")]
    [InlineData("[Binary32Kernel] static bool F(float a, float b) => a != b;")]
    [InlineData("[Binary32Kernel] static float F(float a) => -a;")]
    [InlineData("[Binary32Kernel] static double F(double a) => +a;")]
    [InlineData("[Binary32Kernel] static float F(float a) => ++a;")]
    [InlineData("[Binary32Kernel] static double F(double a) => a--;")]
    [InlineData("[Binary32Kernel] static decimal F(decimal a) { a += 1m; return a; }")]
    [InlineData("[Binary32Kernel] static float F(float a, float b) { a *= b; return a; }")]
    [InlineData("[Binary32Kernel] static float? F(float? a, float? b) => a + b;")]
    [InlineData("[Binary32Kernel] static float F() => 1f + 2f;")]
    public async Task Raw_floating_operators_require_the_arithmetic_home(string members)
    {
        Assert.Equal("KEB32001", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float F(double a) => (float)a;")]
    [InlineData("[Binary32Kernel] static double F(float a) => a;")]
    [InlineData("[Binary32Kernel] static float F(int k) => Home.Div(k, 12f);")]
    [InlineData("[Binary32Kernel] static int F(float a) => (int)a;")]
    [InlineData("[Binary32Kernel] static decimal F(int a) => a;")]
    [InlineData("[Binary32Kernel] static long F(decimal a) => (long)a;")]
    [InlineData("[Binary32Kernel] static float F(decimal a) => (float)a;")]
    [InlineData("[Binary32Kernel] static float? F(int? a) => a;")]
    [InlineData("[Binary32Kernel] static double? F(float? a) => a;")]
    [InlineData("[Binary32Kernel] static int? F(float? a) => (int?)a;")]
    public async Task Nonconstant_numeric_conversions_require_the_arithmetic_home(string members)
    {
        Assert.Equal("KEB32002", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float F(float a) => MathF.Sqrt(a);")]
    [InlineData("[Binary32Kernel] static float F(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);")]
    [InlineData("[Binary32Kernel] static double F(double a) => Math.Sqrt(a);")]
    [InlineData("[Binary32Kernel] static decimal F(decimal a) => Math.Abs(a);")]
    [InlineData("[Binary32Kernel] static bool F(float a) => float.IsFinite(a);")]
    [InlineData("[Binary32Kernel] static bool F(double a) => double.IsNaN(a);")]
    [InlineData("[Binary32Kernel] static int F(float a) => BitConverter.SingleToInt32Bits(a);")]
    [InlineData("[Binary32Kernel] static Vector3 F(Vector3 a, Vector3 b) => a - b;")]
    [InlineData("[Binary32Kernel] static Vector3 F(Vector3 a) => -a;")]
    [InlineData("[Binary32Kernel] static Vector3 F(Vector3 a, Vector3 b) { a += b; return a; }")]
    [InlineData("[Binary32Kernel] static Matrix4x4 F(Matrix4x4 a, Matrix4x4 b) => a * b;")]
    [InlineData("[Binary32Kernel] static float F(Vector3 a) => a.Length();")]
    [InlineData("[Binary32Kernel] static Vector128<float> F(Vector128<float> a, Vector128<float> b) => Vector128.Add(a, b);")]
    [InlineData("[Binary32Kernel] static Vector128<float> F(Vector128<float> a, Vector128<float> b) => a + b;")]
    public async Task Platform_floating_and_vector_APIs_require_the_arithmetic_home(string members)
    {
        Assert.Equal("KEB32003", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("static float G(float a) => a; [Binary32Kernel] static float F(float a) => G(a);")]
    [InlineData("static T G<T>(T a) => a; [Binary32Kernel] static float F(float a) => G(a);")]
    [InlineData("static float P => 1f; [Binary32Kernel] static float F() => P;")]
    [InlineData("static float P { get { return 1f; } } [Binary32Kernel] static float F() => P;")]
    [InlineData("class Other { public float this[int i] => 1f; } [Binary32Kernel] static float F(Other a) => a[0];")]
    [InlineData("class Other { public float P { get => 1f; set { } } } [Binary32Kernel] static void F(Other a) { a.P = 1f; }")]
    [InlineData("float P { get => 1f; [Binary32Kernel] set { } } [Binary32Kernel] float F() => P;")]
    [InlineData("float P { [Binary32Kernel] get => 1f; set { } } [Binary32Kernel] void F() { P = 1f; }")]
    [InlineData("static float CopyTo(float a) => a; [Binary32Kernel] static float F(float a) => CopyTo(a);")]
    [InlineData("static float AsSpan(float a) => a; [Binary32Kernel] static float F(float a) => AsSpan(a);")]
    [InlineData("[Binary32Kernel] static float F(float a) { float L(float x) => G(x); return L(a); } static float G(float a) => a;")]
    public async Task Unmarked_calls_and_source_computed_properties_break_the_closed_call_graph(string members)
    {
        Assert.Equal("KEB32004", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static int F(int i) => (i * 3) + Math.Max(i, 2);")]
    [InlineData("[Binary32Kernel] static long F(int i) => (long)i;")]
    [InlineData("[Binary32Kernel] static byte F(long i) => (byte)i;")]
    [InlineData("[Binary32Kernel] static int F(int i) { i++; i *= 3; return -i; }")]
    [InlineData("[Binary32Kernel] static bool F(bool a, bool b) => !a && b || a == b;")]
    [InlineData("[Binary32Kernel] static float F(float a, bool c) => c ? a : 0f;")]
    [InlineData("[Binary32Kernel] static float F() { float x = 1; return x; }")]
    [InlineData("[Binary32Kernel] static float F() => (float)1.5;")]
    [InlineData("[Binary32Kernel] static decimal F() => 1;")]
    [InlineData("[Binary32Kernel] static int F() => (int)1.5f;")]
    [InlineData("[Binary32Kernel] static double F() { const float v = 2f; return v; }")]
    [InlineData("[Binary32Kernel] static object F(float a) => a;")]
    [InlineData("[Binary32Kernel] static float F(object a) => (float)a;")]
    [InlineData("[Binary32Kernel] static float? F(float a) => a;")]
    [InlineData("[Binary32Kernel] static float F(float? a) => (float)a;")]
    [InlineData("[Binary32Kernel] static float? F() => 1;")]
    [InlineData("[Binary32Kernel] static float F(float a) => Home.Add(a, a);")]
    [InlineData("[Binary32Kernel] static T G<T>(T a) => a; [Binary32Kernel] static float F(float a) => G(a);")]
    [InlineData("[Binary32Kernel] class K { public static float G(float a) => a; } [Binary32Kernel] static float F(float a) => K.G(a);")]
    [InlineData("[Binary32Arithmetic] class Trusted { public static float G(float a) => MathF.Sqrt(a) + (float)Math.Sqrt(a); } [Binary32Kernel] static float F(float a) => Trusted.G(a);")]
    [InlineData("[Binary32Arithmetic] class Trusted { public static float G(float a) => H(a); static float H(float a) => a * a; } [Binary32Kernel] static float F(float a) => Trusted.G(a);")]
    [InlineData("[Binary32Kernel, Binary32Arithmetic] class Trusted { public float G(float a) => a * a; }")]
    [InlineData("[Binary32Kernel] static Vector3 F(float a) => new Vector3(a, a, a);")]
    [InlineData("[Binary32Kernel] static object F() => new object();")]
    [InlineData("[Binary32Kernel] static void F() => throw new ArgumentException(\"bad input\");")]
    [InlineData("float field; [Binary32Kernel] float F() => field;")]
    [InlineData("float P { get; set; } [Binary32Kernel] float F() => P;")]
    [InlineData("float P { get; set; } [Binary32Kernel] void F(float a) { P = a; }")]
    [InlineData("float P { get; } [Binary32Kernel] float F() => P;")]
    [InlineData("float P { [Binary32Kernel] get => 1f; } [Binary32Kernel] float F() => P;")]
    [InlineData("float P { get => 1f; [Binary32Kernel] set { } } [Binary32Kernel] void F() { P = 1f; }")]
    [InlineData("[Binary32Kernel] static float F(float[] a) => a[0];")]
    [InlineData("[Binary32Kernel] static float F(Span<float> a) => a[0];")]
    [InlineData("[Binary32Kernel] static float F(ReadOnlySpan<float> a) => a[0];")]
    [InlineData("[Binary32Kernel] static float F(IReadOnlyList<float> a) => a[0];")]
    [InlineData("[Binary32Kernel] static Span<float> F(float[] a) => a.AsSpan();")]
    [InlineData("[Binary32Kernel] static Span<float> F(Span<float> a) => a.Slice(1);")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<float> F(ReadOnlySpan<float> a) => a.Slice(1);")]
    [InlineData("[Binary32Kernel] static void F(Span<float> a, Span<float> b) => a.CopyTo(b);")]
    [InlineData("[Binary32Kernel] static bool F(ReadOnlySpan<float> a, Span<float> b) => a.TryCopyTo(b);")]
    [InlineData("[Binary32Kernel] static void F(Span<float> a) => a.Clear();")]
    [InlineData("[Binary32Kernel] static void F(Span<float> a) => a.Fill(1f);")]
    [InlineData("[Binary32Kernel] static bool F(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => a.Overlaps(b);")]
    [InlineData("[Binary32Kernel] static bool F(object a, object b) => ReferenceEquals(a, b);")]
    [InlineData("[Binary32Kernel] static void F(object a) => ArgumentNullException.ThrowIfNull(a);")]
    [InlineData("[Binary32Kernel] static void F(float[] a) => Array.Clear(a);")]
    [InlineData("[Binary32Kernel] static void F(float[] a) => Array.Fill(a, 1f);")]
    [InlineData("[Binary32Kernel] static void F(float[] a, float[] b) => a.CopyTo(b, 0);")]
    [InlineData("static float F(float a) => a * a;")]
    public async Task Inventory_exceptions_and_unattributed_code_remain_legal(string members)
    {
        Assert.Empty(await Analyze(members));
    }

    [Theory]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<float> F(float[] a) => a;")]
    [InlineData("[Binary32Kernel] static Span<float> F(float[] a) => a;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<float> F(Span<float> a) => a;")]
    [InlineData("[Binary32Kernel] static void F(float[] a, Span<float> b) => ((ReadOnlySpan<float>)a).CopyTo(b);")]
    public async Task Span_storage_views_do_not_open_an_arithmetic_call(string members)
    {
        Assert.Empty(await Analyze(members));
    }

    [Theory]
    [InlineData("[Binary32Kernel] class K { float F(float a) { float L(float x) => x * x; return L(a); } }")]
    [InlineData("[Binary32Kernel] class K { class Nested { float F(float a) => a * a; } }")]
    [InlineData("class K { [Binary32Kernel] public K(float a) { a *= a; } }")]
    [InlineData("float P { [Binary32Kernel] get => 1f + 2f; }")]
    [InlineData("float field; float P { [Binary32Kernel] set { field = value * value; } }")]
    [InlineData("static float F(float a) { [Binary32Kernel] float L(float x) => x * x; return L(a); }")]
    [InlineData("static Func<float, float> F() => [Binary32Kernel] (float x) => x * x;")]
    [InlineData("[Binary32Kernel] class K { float P => 1f + 2f; }")]
    public async Task Kernel_membership_follows_symbols_including_nested_functions(string members)
    {
        Assert.Equal("KEB32001", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float F(float a) => a + a;", "KEB32001", "operator", "Binary32Arithmetic")]
    [InlineData("[Binary32Kernel] static float F(int a) => a;", "KEB32002", "conversion", "Binary32Arithmetic")]
    [InlineData("[Binary32Kernel] static float F(float a) => MathF.Sqrt(a);", "KEB32003", "API", "Binary32Arithmetic")]
    [InlineData("static float G(float a) => a; [Binary32Kernel] static float F(float a) => G(a);", "KEB32004", "call", "Binary32Kernel")]
    public async Task Diagnostics_are_errors_with_rule_identity_remediation_and_source_location(
        string members, string id, string rule, string remedy)
    {
        var diagnostic = Assert.Single(await Analyze(members));
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("KhaozEngine.CodeHealth", diagnostic.Descriptor.Category);
        Assert.Contains(id, diagnostic.GetMessage());
        Assert.Contains(rule, diagnostic.GetMessage());
        Assert.Contains(remedy, diagnostic.GetMessage());
        Assert.EndsWith("EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md", diagnostic.Descriptor.HelpLinkUri);
        Assert.Equal("/repo/Kernel.cs", diagnostic.Location.SourceTree!.FilePath);
        Assert.True(diagnostic.Location.SourceSpan.Length > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("namespace Marks { class Binary32ArithmeticAttribute : System.Attribute { } }")]
    public async Task No_kernel_attribute_registers_no_per_node_work(string declarations)
    {
        var analyzer = new Binary32KernelAnalyzer();
        var compilation = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.CreateCompilation(
            ("/repo/Plain.cs", "class C { float F(float a) => a * a; } " + declarations));
        var driver = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        Assert.Empty(await driver.GetAnalyzerDiagnosticsAsync());
        var telemetry = await driver.GetAnalyzerTelemetryInfoAsync(analyzer, CancellationToken.None);
        Assert.Equal(1, telemetry.CompilationStartActionsCount);
        Assert.Equal(0, telemetry.OperationActionsCount);
        Assert.Equal(0, telemetry.OperationBlockStartActionsCount);
        Assert.Equal(0, telemetry.OperationBlockActionsCount);
        Assert.Equal(0, telemetry.SyntaxNodeActionsCount);
        Assert.Equal(0, telemetry.SyntaxTreeActionsCount);
        Assert.Equal(0, telemetry.SymbolActionsCount);
        Assert.Equal(0, telemetry.SymbolStartActionsCount);
        Assert.Equal(0, telemetry.CodeBlockActionsCount);
        Assert.Equal(0, telemetry.CodeBlockStartActionsCount);
        Assert.Equal(0, telemetry.SemanticModelActionsCount);
    }

    [Fact]
    public async Task Analyzer_instance_has_no_state_leaking_between_compilations()
    {
        var analyzer = new Binary32KernelAnalyzer();
        var marked = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.RunAnalyzer(analyzer, "KEB32",
            ("/repo/Marked.cs", Source("[Binary32Kernel] static float F(float a) => a * a;")));
        var plain = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.RunAnalyzer(analyzer, "KEB32",
            ("/repo/Plain.cs", "class C { float F(float a) => a * a; }"));
        await Task.WhenAll(marked, plain);
        Assert.Equal("KEB32001", Assert.Single(await marked).Id);
        Assert.Empty(await plain);
    }

    [Fact]
    public async Task Attribute_in_a_referenced_assembly_also_activates_the_inventory()
    {
        var attributes = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.CreateCompilation(
            ("/repo/Attributes.cs", Attributes)).WithAssemblyName("SharedAttributes");
        using var image = new MemoryStream();
        Assert.True(attributes.Emit(image).Success);
        var compilation = attributes.WithAssemblyName("Consumer").RemoveAllSyntaxTrees()
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray()))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "class C { [Marks.Binary32Kernel] float F(float a) => a * a; }"));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var driver = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new Binary32KernelAnalyzer()));
        Assert.Equal("KEB32001", Assert.Single(await driver.GetAnalyzerDiagnosticsAsync()).Id);
    }

    [Fact]
    public async Task Field_backed_computed_property_is_not_an_auto_property()
    {
        var compilation = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.CreateCompilation(
                ("/repo/Empty.cs", "class Empty { }"))
            .RemoveAllSyntaxTrees().AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                Source("float P { get => field; set; } [Binary32Kernel] float F() => P;"),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var driver = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new Binary32KernelAnalyzer()));
        Assert.Equal("KEB32004", Assert.Single(await driver.GetAnalyzerDiagnosticsAsync()).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static bool Positive(float x) => x is > 0f;", "RelationalPattern")]
    [InlineData("[Binary32Kernel] static bool Zero(float x) => x is 0f;", "ConstantPattern")]
    [InlineData("[Binary32Kernel] static float Select(float x) => x switch { > 0f => 1f, _ => 0f };", "SwitchExpression")]
    [InlineData("[Binary32Kernel] static float Select(float x) => x switch { 0f => 1f, _ => 0f };", "ConstantPattern")]
    public async Task R1_floating_patterns_fail_closed(string members, string kind)
    {
        Assert.Contains(await Analyze(members), d => d.Id == "KEB32005" && d.GetMessage().Contains(kind));
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float Narrow(double[] values) { foreach (float value in values) return value; return 0f; }", "Loop")]
    [InlineData("[Binary32Kernel] static double[] Widen(float[] values) => [.. values];", "Spread")]
    [InlineData("[Binary32Kernel] static float[] Values() => [1f];", "CollectionExpression")]
    [InlineData("[Binary32Kernel] static float First(IEnumerable<float> values) { foreach (float value in values) return value; return 0f; }", "Loop")]
    [InlineData("sealed class Values { public Cursor GetEnumerator() => new Cursor(); } sealed class Cursor { public bool MoveNext() => true; public float Current => MathF.Sqrt(2f); } [Binary32Kernel] static float First(Values values) { foreach (float value in values) return value; return 0f; }", "Loop")]
    public async Task R2_implicit_enumeration_work_fails_closed(string members, string kind)
    {
        Assert.Contains(await Analyze(members), d => d.Id == "KEB32005" && d.GetMessage().Contains(kind));
    }

    [Theory]
    [InlineData("enum Bits : long { } [Binary32Kernel] static float Convert(Bits value) => (float)value;")]
    [InlineData("enum Bits : long { } [Binary32Kernel] static Bits Convert(float value) => (Bits)value;")]
    [InlineData("enum Bits : long { } [Binary32Kernel] static double Convert(Bits value) => (double)value;")]
    [InlineData("enum Bits : long { } [Binary32Kernel] static Bits Convert(double value) => (Bits)value;")]
    public async Task R3_enum_floating_conversions_require_the_arithmetic_home(string members)
    {
        Assert.Equal("KEB32002", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("float result; float? P { [Binary32Kernel] get => null; set { result = MathF.Sqrt(value.GetValueOrDefault()); } } [Binary32Kernel] void Assign(float value) { P ??= value; }", "CoalesceAssignment")]
    [InlineData("float result; float P { [Binary32Kernel] get => result; set { result = MathF.Sqrt(value); } } [Binary32Kernel] void Assign(float value) { (P, _) = (value, 0); }", "DeconstructionAssignment")]
    [InlineData("[Binary32Kernel] static float F(float? value) => value ?? 0f;", "Coalesce")]
    [InlineData("[Binary32Kernel] static (float, int) F(float value) => (value, 0);", "Tuple")]
    [InlineData("[Binary32Kernel] static float F((float, int) pair) { var (value, count) = pair; return value; }", "DeconstructionAssignment")]
    public async Task R4_conditional_and_tuple_writes_fail_closed(string members, string kind)
    {
        Assert.Contains(await Analyze(members), d => d.Id == "KEB32005" && d.GetMessage().Contains(kind));
    }

    [Theory]
    [InlineData("int count; ref int Counter { [Binary32Kernel] get => ref count; } [Binary32Kernel] void Increment() { Counter++; }")]
    [InlineData("int count; ref int Counter { [Binary32Kernel] get => ref count; } [Binary32Kernel] void Increment() { Counter += 2; }")]
    [InlineData("int count; ref int Counter { [Binary32Kernel] get => ref count; } [Binary32Kernel] void Assign() { Counter = 2; }")]
    public async Task R5_ref_return_integer_updates_only_reach_the_getter(string members)
    {
        Assert.Empty(await Analyze(members));
    }

    [Theory]
    [InlineData("[Binary32Kernel] abstract class KernelBase { public abstract float Eval(float value); } sealed class PlatformImpl : KernelBase { public override float Eval(float value) => MathF.Sqrt(value); } [Binary32Kernel] static float Run(KernelBase kernel, float value) => kernel.Eval(value);")]
    [InlineData("[Binary32Kernel] delegate float KernelOp(float value); [Binary32Kernel] static float Run(KernelOp op, float value) => op(value); static float Outside(float value) => Run(x => MathF.Sqrt(x), value);")]
    [InlineData("[Binary32Kernel] class K { public virtual float Eval(float value) => value; } [Binary32Kernel] static float Run(K kernel, float value) => kernel.Eval(value);")]
    [InlineData("[Binary32Kernel] interface K { float Eval(float value); } [Binary32Kernel] static float Run(K kernel, float value) => kernel.Eval(value);")]
    [InlineData("[Binary32Kernel] static float Identity(float value) => value; [Binary32Kernel] static Func<float, float> F() => Identity;")]
    [InlineData("[Binary32Kernel] static Func<float, float> F() => x => x;")]
    [InlineData("[Binary32Kernel] class K { public virtual float Value => 0f; } [Binary32Kernel] static float Run(K kernel) => kernel.Value;")]
    [InlineData("[Binary32Kernel] interface K { float Value { get; } } [Binary32Kernel] static float Run(K kernel) => kernel.Value;")]
    [InlineData("[Binary32Kernel] class K { public virtual float Value { get; set; } } [Binary32Kernel] static void Run(K kernel) { kernel.Value = 0f; }")]
    public async Task R6_unresolved_dispatch_and_delegate_creation_break_closure(string members)
    {
        Assert.Contains(await Analyze(members), d => d.Id == "KEB32004");
    }

    [Fact]
    public async Task Kernel_lambda_bodies_are_checked_even_when_delegate_creation_is_rejected()
    {
        var diagnostics = await Analyze("[Binary32Kernel] static Func<float, float> F() => x => x * x;");
        Assert.Equal(new[] { "KEB32004", "KEB32001" }, diagnostics.Select(d => d.Id));
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float Add(dynamic value) => value + value;")]
    [InlineData("[Binary32Kernel] static float Call(dynamic helper) => helper.Eval();")]
    [InlineData("[Binary32Kernel] static dynamic F(dynamic helper) => helper.Value;")]
    [InlineData("[Binary32Kernel] static dynamic F(dynamic values) => values[0];")]
    [InlineData("[Binary32Kernel] static dynamic F(dynamic value) => value;")]
    [InlineData("[Binary32Kernel] static void F() { dynamic value; }")]
    [InlineData("[Binary32Kernel] static object F(dynamic value) => (object)value;")]
    public async Task R7_dynamic_operations_fail_closed(string members)
    {
        Assert.Contains(await Analyze(members), d => d.Id == "KEB32005");
    }

    [Fact]
    public async Task R8_generated_kernel_bodies_are_analyzed_and_reported()
    {
        var diagnostics = await KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.RunAnalyzer(
            new Binary32KernelAnalyzer(), "KEB32",
            ("/repo/GeneratedKernel.g.cs", "using System; using Marks; [Binary32Kernel] static class GeneratedKernel { public static float Eval(float value) => MathF.Sqrt(value); }"),
            ("/repo/Kernel.cs", Source("[Binary32Kernel] static float Run(float value) => GeneratedKernel.Eval(value);")));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("KEB32003", diagnostic.Id);
        Assert.Equal("/repo/GeneratedKernel.g.cs", diagnostic.Location.SourceTree!.FilePath);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float Convert(int[] source, float[] destination) { source.CopyTo(destination, 0); return destination[0]; }")]
    [InlineData("[Binary32Kernel] static void Copy(float[] source, double[] destination) => source.CopyTo(destination, 0);")]
    [InlineData("[Binary32Kernel] static void Copy(Array source, Array destination) => source.CopyTo(destination, 0);")]
    [InlineData("[Binary32Kernel] static void Copy(Array source, float[] destination) => source.CopyTo(destination, 0);")]
    [InlineData("[Binary32Kernel] static void Copy(float[] source, Array destination) => source.CopyTo(destination, 0);")]
    [InlineData("[Binary32Kernel] static void Copy(float[] source, float[] destination) => ((Array)source).CopyTo(destination, 0);")]
    public async Task R9_copy_APIs_require_identical_static_element_types(string members)
    {
        var diagnostic = Assert.Single(await Analyze(members));
        Assert.Equal("KEB32005", diagnostic.Id);
        Assert.Contains("Invocation", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("[Binary32Kernel] static void Copy(Memory<float> source, Memory<float> destination) => source.CopyTo(destination);")]
    [InlineData("[Binary32Kernel] static bool Copy(ReadOnlyMemory<float> source, Memory<float> destination) => source.TryCopyTo(destination);")]
    [InlineData("[Binary32Kernel] static void Copy(float[] source, Span<float> destination) => source.CopyTo(destination);")]
    [InlineData("[Binary32Kernel] static void Copy(float[] source, Span<float> destination) => MemoryExtensions.CopyTo(destination: destination, source: source);")]
    [InlineData("[Binary32Kernel] static void Copy<T>(T[] source, T[] destination) => source.CopyTo(destination, 0);")]
    public async Task R9_representation_preserving_copies_stay_legal(string members)
    {
        Assert.Empty(await Analyze(members));
    }

    [Fact]
    public async Task R10_overlapping_fields_cannot_reinterpret_float_bits()
    {
        var diagnostics = await Analyze("""
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            struct FloatBits
            {
                [System.Runtime.InteropServices.FieldOffset(0)] public float Value;
                [System.Runtime.InteropServices.FieldOffset(0)] public int Bits;
            }
            [Binary32Kernel]
            static float Negate(float value)
            {
                FloatBits original = default;
                original.Value = value;
                FloatBits copy = original;
                copy.Bits ^= int.MinValue;
                return copy.Value;
            }
            """);
        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("KEB32005", diagnostic.Id);
            Assert.Contains("FieldReference", diagnostic.GetMessage());
        });
    }

    [Fact]
    public async Task R10_explicit_layout_field_references_are_rejected_without_FieldOffset()
    {
        var diagnostic = Assert.Single(await Analyze("""
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            struct Storage { public static float Value; }
            [Binary32Kernel] static float Read() => Storage.Value;
            """));
        Assert.Equal("KEB32005", diagnostic.Id);
    }

    [Theory]
    [InlineData("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float Value; [System.Runtime.InteropServices.FieldOffset(0)] public int Bits; }", "[Binary32Kernel] static float Read(FloatBits value) => value.Value;", true)]
    [InlineData("public class Holder { [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct Storage { public static float Value; } }", "[Binary32Kernel] static float Read() => Holder.Storage.Value;", true)]
    [InlineData("public class Holder<T> { [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct Storage { public float Value; } }", "[Binary32Kernel] static float Read(Holder<int>.Storage value) => value.Value;", false)]
    public async Task R10_referenced_layout_fields_are_checked(string declaration, string members, bool rejected)
    {
        var layout = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.CreateCompilation(
            ("/repo/Layout.cs", declaration)).WithAssemblyName("Layout");
        using var image = new MemoryStream();
        Assert.True(layout.Emit(image).Success);
        var compilation = layout.WithAssemblyName("Consumer").RemoveAllSyntaxTrees()
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray()))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Source(members)));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var driver = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new Binary32KernelAnalyzer()));
        var diagnostics = await driver.GetAnalyzerDiagnosticsAsync();
        if (rejected) Assert.Equal("KEB32005", Assert.Single(diagnostics).Id);
        else Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task R10_sequential_struct_fields_and_copies_stay_legal()
    {
        Assert.Empty(await Analyze("""
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            struct Storage { public float Value; public int Count; }
            [Binary32Kernel] static float Copy(float value)
            {
                Storage original = default;
                original.Value = value;
                Storage copy = original;
                copy.Count++;
                return copy.Value;
            }
            """));
    }

    [Theory]
    [InlineData("[Binary32Kernel] struct KernelValue(int input) { public float Value = input; public float Read() => Value; } [Binary32Kernel] static float Convert(int input) => new KernelValue(input).Read();", "KEB32002")]
    [InlineData("[Binary32Kernel] struct KernelValue(int input) { public float Value { get; } = input; }", "KEB32002")]
    [InlineData("[Binary32Kernel] struct KernelValue(float input) { public float Value = input * input; }", "KEB32001")]
    [InlineData("[Binary32Kernel] struct KernelValue(float input) { public float Value { get; } = input * input; }", "KEB32001")]
    [InlineData("class KernelValue { static int Input = 16777217; public float Value = Input; [Binary32Kernel] public KernelValue() { } }", "KEB32002")]
    [InlineData("class KernelValue { static int Input = 16777217; public float Value { get; } = Input; [Binary32Kernel] public KernelValue() { } }", "KEB32002")]
    [InlineData("[method: Binary32Kernel] struct KernelValue(int input) { public float Value = input; }", "KEB32002")]
    [InlineData("class KernelValue { static float Input = 2f; public static float Value { get; } = Input * Input; [Binary32Kernel] static KernelValue() { } }", "KEB32001")]
    public async Task R11_kernel_type_and_constructor_initializers_are_checked(string members, string id)
    {
        Assert.Equal(id, Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] struct KernelValue(float input) { public float Value = input; public float Copy { get; } = input; public float Constant = 1; }")]
    [InlineData("class KernelValue { static float Input = 2f; public float Value = Input; public float Copy { get; } = Input; [Binary32Kernel] public KernelValue() { } }")]
    [InlineData("[Binary32Kernel] struct KernelValue(float input) { public float Value = Home.Add(input, input); }")]
    [InlineData("[Binary32Kernel, Binary32Arithmetic] struct KernelValue(int input) { public float Value = input; }")]
    [InlineData("struct Ordinary(int input) { public float Value = input; public float Copy { get; } = input; }")]
    public async Task R11_permitted_initializers_and_unmarked_types_stay_legal(string members)
    {
        Assert.Empty(await Analyze(members));
    }

    [Theory]
    [InlineData("[Binary32Kernel] static void F() { goto done; done: return; }")]
    [InlineData("[Binary32Kernel] static unsafe void F() { }")]
    [InlineData("static void F() { [Binary32Kernel] static unsafe void Local() { } }")]
    [InlineData("struct Value { [Binary32Kernel] public static implicit operator int(Value value) => 0; } [Binary32Kernel] static int F(Value value) => value;")]
    [InlineData("[Binary32Kernel] static void F() { unsafe { } }")]
    [InlineData("[Binary32Kernel] static unsafe int F(int* value) => *value;")]
    [InlineData("[Binary32Kernel] static Span<int> F() { Span<int> value = stackalloc int[1]; return default; }")]
    [InlineData("[Binary32Kernel] static unsafe int* F() => null;")]
    [InlineData("[Binary32Kernel] static string F(float value) => $\"{value}\";")]
    [InlineData("[Binary32Kernel] static int F(object value) => value.GetHashCode();")]
    [InlineData("[Binary32Kernel] static int F() { try { return 0; } catch { return 1; } }")]
    public async Task Unlisted_operation_forms_fail_closed(string members)
    {
        Assert.Contains(await Analyze(members), d => d.Id == "KEB32005" || d.Id == "KEB32004");
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float F(ref int value) => System.Runtime.CompilerServices.Unsafe.As<int, float>(ref value);")]
    [InlineData("[Binary32Kernel] static Span<float> F(Span<int> values) => System.Runtime.InteropServices.MemoryMarshal.Cast<int, float>(values);")]
    public async Task Reinterpretation_APIs_break_the_closed_call_graph(string members)
    {
        Assert.Equal("KEB32004", Assert.Single(await Analyze(members)).Id);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static float F(float[] values) { foreach (float value in values) return value; return default; }")]
    [InlineData("[Binary32Kernel] static float F(Span<float> values) { foreach (float value in values) return value; return default; }")]
    [InlineData("[Binary32Kernel] static float F(ReadOnlySpan<float> values) { foreach (float value in values) return value; return default; }")]
    [InlineData("[Binary32Kernel] static float F(Span<float> values) { foreach (ref float value in values) return value; return default; }")]
    [InlineData("[Binary32Kernel] static int F(int count) { int total = 0; for (int i = 0; i < count; i++) { if (i == 1) continue; total += i; if (i == 3) break; } while (total < 10) total++; do { total--; } while (total > 8); return total; }")]
    [InlineData("[Binary32Kernel] static float F(float value) { float Local(float x) => x; return Local(value); }")]
    [InlineData("enum Bits : long { One = 1 } [Binary32Kernel] static long F(Bits value) => (long)value;")]
    [InlineData("enum Bits : long { One = 1 } [Binary32Kernel] static float F() => (float)Bits.One;")]
    [InlineData("[Binary32Kernel] static float F() => default;")]
    public async Task Explicitly_allowed_control_flow_and_storage_stay_legal(string members)
    {
        Assert.Empty(await Analyze(members));
    }

    [Fact]
    public async Task Unlisted_diagnostic_names_the_operation_and_remedy()
    {
        var diagnostic = Assert.Single(await Analyze("[Binary32Kernel] static float F(float? value) => value ?? 0f;"));
        Assert.Equal("KEB32005", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("KhaozEngine.CodeHealth", diagnostic.Descriptor.Category);
        Assert.Contains("Coalesce", diagnostic.GetMessage());
        Assert.Contains("Binary32Arithmetic", diagnostic.GetMessage());
        Assert.Contains("operation not permitted in a binary32 kernel", diagnostic.GetMessage());
        Assert.EndsWith("EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md", diagnostic.Descriptor.HelpLinkUri);
        Assert.Equal("/repo/Kernel.cs", diagnostic.Location.SourceTree!.FilePath);
    }

    [Theory]
    [InlineData("[Binary32Kernel] static Span<int> F(int[] values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<float> F(float[] values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<float> F(Span<float> values) => values;")]
    [InlineData("[Binary32Kernel] static Span<T> F<T>(T[] values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<T> F<T>(T[] values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<T> F<T>(Span<T> values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<float> F(float[] values) => (ReadOnlySpan<float>)values;")]
    public async Task B36_identical_element_implicit_span_views_stay_legal(string members)
    {
        Assert.Empty(await AnalyzeSpan(members));
    }

    [Theory]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<object> F(string[] values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<object> F(Span<string> values) => values;")]
    [InlineData("[Binary32Kernel] static ReadOnlySpan<object> F(string[] values) => (ReadOnlySpan<object>)values;")]
    public async Task B36_differing_element_implicit_span_views_are_rejected(string members)
    {
        Assert.Contains(await AnalyzeSpan(members), diagnostic => diagnostic.Id == "KEB32005");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeSpan(string members)
    {
        var compilation = KhaozEngine.CodeHealth.Analyzers.Tests.AnalyzerHarness.CreateCompilation()
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Source(members), new CSharpParseOptions(LanguageVersion.Preview)));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new Binary32KernelAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        return diagnostics;
    }

}
