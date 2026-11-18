using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using static KhaozEngine.Tests.CodeHealth.SubnormalFloatHarness;

namespace KhaozEngine.Tests.CodeHealth;

public sealed partial class SubnormalFloatAnalyzerTests
{
    [Theory]
    [InlineData("dynamic F(float x) => MathF.BitIncrement((dynamic)x);", "MathF.BitIncrement((dynamic)x)")]
    [InlineData("dynamic F(float x) => float.BitIncrement((dynamic)x);", "float.BitIncrement((dynamic)x)")]
    [InlineData("static T F<T>(T x) where T : IFloatingPointIeee754<T> => T.BitIncrement((dynamic)x);",
        "T.BitIncrement((dynamic)x)")]
    [InlineData("interface IOther<T> { static abstract T BitIncrement(T value); }\n" +
        "static T F<T>(T x) where T : IFloatingPointIeee754<T>, IOther<T> => T.BitIncrement((dynamic)x);",
        "T.BitIncrement((dynamic)x)")]
    public async Task AStepWithADynamicArgumentReportsOnceAtTheCall(string members, string expected)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(members));

        Assert.Equal(new[] { expected }, Flagged(diagnostics));
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public async Task AStaticImportDoesNotHideAStepWithADynamicArgument()
    {
        const string source = """
            using static System.MathF;

            static class Sample
            {
                static dynamic Step(float x) => BitIncrement((dynamic)x);
            }
            """;

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, source);

        Assert.Equal(new[] { "BitIncrement((dynamic)x)" }, Flagged(diagnostics));
    }

    [Theory]
    [InlineData("static class Own { public static float BitIncrement(float x) => x; }\n" +
        "dynamic F(float x) => Own.BitIncrement((dynamic)x);")]
    [InlineData("dynamic F(double x) => Math.BitIncrement((dynamic)x);")]
    public async Task OtherStepsWithADynamicArgumentAreAllowed(string members)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(members));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AShadowingInterfaceDoesNotHideTheInheritedBclStep()
    {
        const string source = """
            namespace System.Numerics { public interface IFloatingPointIeee754<T> { } }

            static class Sample
            {
                static T Step<T>(T x) where T : System.Numerics.IBinaryFloatingPointIeee754<T> => T.BitIncrement(x);
                public static float Run() => Step(0f);
            }
            """;

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, source);

        Assert.Equal(new[] { "T.BitIncrement(x)" }, Flagged(diagnostics));
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Theory]
    [InlineData("""
        namespace System.Numerics
        {
            public interface IFloatingPointIeee754<T>
            {
                static abstract T BitIncrement(T value);
                static abstract T Epsilon { get; }
            }
        }

        static class Sample
        {
            static T Step<T>(T x) where T : System.Numerics.IFloatingPointIeee754<T> => T.BitIncrement(x);
            static T Read<T>() where T : System.Numerics.IFloatingPointIeee754<T> => T.Epsilon;
        }
        """)]
    [InlineData("""
        namespace System { public static class MathF { public static float BitIncrement(float value) => value; } }
        static class Sample { static float Step(float x) => System.MathF.BitIncrement(x); }
        """)]
    public async Task ASourceTypeWithABclNameIsAllowed(string source)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, source);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("static T Read<T>() where T : IFloatingPointIeee754<T> => T.Epsilon;")]
    [InlineData("static T Read<T>() where T : IBinaryFloatingPointIeee754<T> => T.Epsilon;")]
    [InlineData("interface ISelf<T> : IFloatingPointIeee754<T> where T : ISelf<T> { T Read() => T.Epsilon; }")]
    [InlineData("class Outer<T> where T : IFloatingPointIeee754<T> { public class Nested { public T Read() => T.Epsilon; } }")]
    [InlineData("static float Read<T>() where T : IFloatingPointIeee754<float> => T.Epsilon;")]
    public async Task EpsilonOnAnIeeeTypeParameterReportsAtTheProperty(string members)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(members));

        Assert.Equal(new[] { "T.Epsilon" }, Flagged(diagnostics));
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Theory]
    [InlineData("static T Read<T>() where T : IFloatingPointIeee754<T> => T.NaN;")]
    [InlineData("static T Read<T>() where T : IFloatingPointIeee754<T> => T.NegativeZero;")]
    [InlineData("static T Read<T>() where T : IFloatingPointIeee754<T> => T.PositiveInfinity;")]
    [InlineData("static T Read<T>() where T : IMinMaxValue<T> => T.MaxValue;")]
    [InlineData("double F() => double.Epsilon;")]
    [InlineData("string F() => nameof(float.Epsilon) + nameof(MathF.BitIncrement);")]
    [InlineData("static string Name<T>() where T : IFloatingPointIeee754<T> => nameof(T.Epsilon) + nameof(T.BitIncrement);")]
    [InlineData("static string Name<T>() where T : IFloatingPointIeee754<float> => nameof(T.Epsilon);")]
    [InlineData("static readonly uint Bits = 1u;\nfloat F() => BitConverter.UInt32BitsToSingle((uint)(nint)Bits);")]
    [InlineData("static readonly uint Bits = 1u;\nfloat F() => BitConverter.UInt32BitsToSingle((uint)(nuint)Bits);")]
    public async Task OtherConstantsAndNameOnlyReferencesAreAllowed(string members)
    {
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(members));

        Assert.Empty(diagnostics);
    }
}
