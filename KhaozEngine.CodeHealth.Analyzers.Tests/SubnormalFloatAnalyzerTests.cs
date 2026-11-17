using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using static KhaozEngine.Tests.CodeHealth.SubnormalFloatHarness;

namespace KhaozEngine.Tests.CodeHealth;

// #1832. The JIT folds a subnormal float constant through the flush mode of the thread that compiles the method, and
// the BCL float steps hold one in their zero branch. KEFP001 keeps every such constant out of engine code.
public sealed class SubnormalFloatAnalyzerTests
{
    private const string BclStep = "float F(float x) => MathF.BitDecrement(x);";

    // Each row is a class body and the exact source text KEFP001 must point at, in source order.
    public static TheoryData<string, string[]> FlaggedForms => new()
    {
        { "float F(float x) => MathF.BitDecrement(x);", ["MathF.BitDecrement(x)"] },
        { "float F(float x) => MathF.BitIncrement(x);", ["MathF.BitIncrement(x)"] },
        { "float F(float x) => float.BitDecrement(x);", ["float.BitDecrement(x)"] },
        { "float F(float x) => float.BitIncrement(x);", ["float.BitIncrement(x)"] },
        { "Func<float, float> F() => MathF.BitIncrement;", ["MathF.BitIncrement"] },
        // A generic constrained to the float interface resolves straight to the float step.
        { "static float F<T>(float x) where T : IFloatingPointIeee754<float> => T.BitDecrement(x);", ["T.BitDecrement(x)"] },
        // A generic that steps its own type parameter, used with float, directly, through a generic type, and
        // through another generic that forwards its type parameter.
        {
            "static T Step<T>(T x) where T : IFloatingPointIeee754<T> => T.BitDecrement(x);\n" +
            "float F(float x) => Step(x);",
            ["Step(x)"]
        },
        {
            "sealed class Stepper<T> where T : IFloatingPointIeee754<T> { public static T Up(T x) => T.BitIncrement(x); }\n" +
            "float F(float x) => Stepper<float>.Up(x);",
            ["Stepper<float>.Up(x)"]
        },
        {
            "static T Inner<T>(T x) where T : IFloatingPointIeee754<T> => T.BitIncrement(x);\n" +
            "static U Outer<U>(U x) where U : IFloatingPointIeee754<U> => Inner(x);\n" +
            "float F(float x) => Outer(x);",
            ["Outer(x)"]
        },
        {
            "sealed class Stepper<T> where T : IFloatingPointIeee754<T> { public static T Next => T.BitIncrement(T.Zero); }\n" +
            "float F() => Stepper<float>.Next;",
            ["Stepper<float>.Next"]
        },
        {
            "sealed class Stepper<T> where T : IFloatingPointIeee754<T> { public Stepper() => _ = T.BitIncrement(T.Zero); }\n" +
            "object F() => new Stepper<float>();",
            ["new Stepper<float>()"]
        },
        {
            "sealed class Stepper<T> where T : IFloatingPointIeee754<T> { private static T Up(T x) => T.BitIncrement(x); " +
            "public static T Next => Up(T.Zero); }\nfloat F() => Stepper<float>.Next;",
            ["Stepper<float>.Next"]
        },
        { "float F() => 1e-45f;", ["1e-45f"] },
        { "float F() => float.Epsilon;", ["float.Epsilon"] },
        { "float F() => -float.Epsilon;", ["-float.Epsilon"] },
        { "bool F(float x) => x >= float.Epsilon;", ["float.Epsilon"] },
        // Normal operands whose folded product is subnormal, and a double cast down to a subnormal float.
        { "float F() => 2e-38f * 0.25f;", ["2e-38f * 0.25f"] },
        { "float F() => (float)1e-40;", ["(float)1e-40"] },
        { "float F() { const float tiny = 1e-45f; return tiny; }", ["1e-45f", "tiny"] },
        // A default parameter value reaches the JIT at every call that omits it.
        { "static float G(float x = 1e-45f) => x;\nfloat F() => G();", ["1e-45f", "G()"] },
        { "float F() => BitConverter.UInt32BitsToSingle(1u);", ["BitConverter.UInt32BitsToSingle(1u)"] },
        {
            "float F() => BitConverter.Int32BitsToSingle(unchecked((int)0x807FFFFF));",
            ["BitConverter.Int32BitsToSingle(unchecked((int)0x807FFFFF))"]
        },
        { "float F() => Unsafe.BitCast<uint, float>(0x00000001u);", ["Unsafe.BitCast<uint, float>(0x00000001u)"] },
        { "static readonly float Tiny = 1e-45f;", ["1e-45f"] },
        // The JIT folds a static readonly field, so bits held in one and read as a float bake like a literal.
        {
            "static readonly uint Bits = 0x80000001u;\nfloat F() => BitConverter.UInt32BitsToSingle(Bits);",
            ["Bits"]
        },
        {
            "static readonly uint Bits = 0x00800001u;\nfloat F() => BitConverter.UInt32BitsToSingle((byte)Bits);",
            ["Bits"]
        },
        {
            "static readonly ulong Bits = 0x1_0000_0081ul;\n" +
            "float F() => BitConverter.Int32BitsToSingle((byte)(sbyte)(uint)Bits);",
            ["Bits"]
        },
    };

    [Theory]
    [MemberData(nameof(FlaggedForms))]
    public async Task EveryFormThatPutsASubnormalFloatConstantInFrontOfTheJitIsFlagged(string members, string[] expected)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(members));

        Assert.Equal(expected, Flagged(diagnostics));
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    // Doubles, normal floats, zeros, bits read at run time and a generic used with double stay allowed.
    [Theory]
    [InlineData("double F(double x) => Math.BitDecrement(x) + Math.BitIncrement(x) + double.BitDecrement(x);")]
    [InlineData("double F() => double.Epsilon + 4.9e-324;")]
    [InlineData("const double MinimumSubnormal = (double)float.Epsilon;")]
    [InlineData("float F() => 1.17549435E-38f + float.MaxValue + float.MinValue + 0f + -0f;")]
    [InlineData("float F() => BitConverter.UInt32BitsToSingle(0x80000000u) + BitConverter.UInt32BitsToSingle(0x00800000u);")]
    [InlineData("float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);")]
    [InlineData("static readonly int Count = 5;\nint F() => Count;")]
    [InlineData("static uint s_bits = 0x00000001u;\nfloat F() => BitConverter.UInt32BitsToSingle(s_bits);")]
    [InlineData("static readonly uint Mask = 0x007FFFFFu;\nfloat F(uint x) => BitConverter.UInt32BitsToSingle(x & Mask);")]
    [InlineData("static T Step<T>(T x) where T : IFloatingPointIeee754<T> => T.BitDecrement(x);\ndouble F(double x) => Step(x);")]
    [InlineData("sealed class Stepper<T> where T : IFloatingPointIeee754<T> { public T Up(T x) => T.BitIncrement(x); public static int Dimension() => 3; }\nint F() => Stepper<float>.Dimension();")]
    [InlineData("static readonly uint Bits = 0x100u;\nfloat F() => BitConverter.UInt32BitsToSingle((byte)Bits);")]
    [InlineData("static readonly uint Bits = 0x101u;\nfloat F() => BitConverter.UInt32BitsToSingle(checked((byte)Bits));")]
    public async Task DoublesNormalFloatsAndRunTimeBitsAreNotFlagged(string members)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(members));

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("FloatUlp.Down(1.17549435E-38f)")]
    [InlineData("FloatUlp.Up(BitConverter.UInt32BitsToSingle(0x80800000u))")]
    [InlineData("FloatUlp.Down(Unsafe.BitCast<uint, float>(0x00800000u))")]
    public async Task AConstantHelperStepIntoASubnormalIsFlagged(string expression)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, HelperTemplate(),
            "using KhaozEngine.FloatSteps;\n" + Members($"float F() => {expression};"));

        Assert.Equal(new[] { expression }, Flagged(diagnostics));
    }

    // The engine's one constant-argument site, SnapStripGeometry's float.MaxValue step, after the move to FloatUlp.
    [Fact]
    public async Task TheHelperAndItsConstantArgumentSiteAreNotFlagged()
    {
        const string site = "static double F() =>\n" +
            "    (double)float.MaxValue + ((double)float.MaxValue - FloatUlp.Down(float.MaxValue)) / 2d;";

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, HelperTemplate(),
            "using KhaozEngine.FloatSteps;\n" + Members(site +
                "\nfloat Zeros() => FloatUlp.Up(-0f) + FloatUlp.Down(0f);"));

        Assert.Empty(diagnostics);
    }

    // The helper needs no suppression because its subnormal bits live in mutable statics. Making them readonly, the
    // change an IDE suggests, would let the JIT fold them, so the rule reports that.
    [Fact]
    public async Task TheHelperWithReadonlyEpsilonBitsIsFlagged()
    {
        string readonlyHelper = HelperTemplate().Replace("private static uint", "private static readonly uint");
        Assert.NotEqual(HelperTemplate(), readonlyHelper);

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, readonlyHelper);

        Assert.Equal(new[] { "s_negativeEpsilonBits", "s_positiveEpsilonBits" }, Flagged(diagnostics));
    }

    // A consumer compilation gets the analyzer through the umbrella packages and builds with warnings as errors. It
    // sees nothing until it sets the property. Null is a compilation that never surfaces the property, the empty
    // string is the value an unset CompilerVisibleProperty carries.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    public async Task AConsumerThatHasNotOptedInSeesNothing(string? scope)
    {
        var diagnostics = await Run(scope, withXunit: false, warningsAsErrors: true, Members(BclStep));

        Assert.Empty(diagnostics);
    }

    // "on" leaves a test compilation alone, because tests compare against the BCL steps. "all" covers one, which is
    // how the floating point test assemblies opt in.
    [Theory]
    [InlineData("on", false, true)]
    [InlineData("on", true, false)]
    [InlineData("all", true, true)]
    [InlineData("ALL", false, true)]
    public async Task TheScopeDecidesWhetherATestCompilationIsChecked(string scope, bool withXunit, bool flagged)
    {
        var diagnostics = await Run(scope, withXunit, warningsAsErrors: false, Members(BclStep));

        string[] expected = flagged ? ["MathF.BitDecrement(x)"] : [];
        Assert.Equal(expected, Flagged(diagnostics));
    }
}
