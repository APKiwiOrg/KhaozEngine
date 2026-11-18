using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using static KhaozEngine.Tests.CodeHealth.SubnormalFloatHarness;

namespace KhaozEngine.Tests.CodeHealth;

public sealed partial class SubnormalFloatAnalyzerTests
{
    // #1832. The declaration is the boundary, including steps a compiler-selected or external caller could reach.
    public static TheoryData<string, string, string> GenericStepSites => new()
    {
        {
            "static method", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> { public static T Up(T x) => T.BitIncrement(x); }\n" +
                "float F() => Stepper<float>.Up(0f);"),
            "T.BitIncrement(x)"
        },
        {
            "instance method", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> { public T Down(T x) => T.BitDecrement(x); }\n" +
                "float F() => new Stepper<float>().Down(0f);"),
            "T.BitDecrement(x)"
        },
        {
            "property", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> { public T Next => T.BitIncrement(T.Zero); }"),
            "T.BitIncrement(T.Zero)"
        },
        {
            "indexer accessor", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> " +
                "{ public T this[int i] { get => T.Zero; set => _ = T.BitIncrement(value); } }"),
            "T.BitIncrement(value)"
        },
        {
            "constructor", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> { public Stepper() => _ = T.BitIncrement(T.Zero); }"),
            "T.BitIncrement(T.Zero)"
        },
        {
            "static constructor", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> { static Stepper() => _ = T.BitDecrement(T.Zero); }"),
            "T.BitDecrement(T.Zero)"
        },
        {
            "field initializer", Members(
                "class Stepper<T> where T : IFloatingPointIeee754<T> { static readonly T Next = T.BitIncrement(T.Zero); }"),
            "T.BitIncrement(T.Zero)"
        },
        {
            "lambda", Members("static Func<T, T> Make<T>() where T : IFloatingPointIeee754<T> => static value => T.BitIncrement(value);"),
            "T.BitIncrement(value)"
        },
        {
            "local function", Members("static T F<T>(T x) where T : IFloatingPointIeee754<T> " +
                "{ static T Step(T value) => T.BitDecrement(value); return Step(x); }"),
            "T.BitDecrement(value)"
        },
        {
            "method group", Members("static Func<T, T> Make<T>() where T : IFloatingPointIeee754<T> " +
                "{ Func<T, T> f = T.BitIncrement; return f; }"),
            "T.BitIncrement"
        },
        {
            "generic method on a non-generic type", Members(
                "static T Step<T>(T x) where T : IFloatingPointIeee754<T> => T.BitIncrement(x);\n" +
                "float F() => Step(0f);"),
            "T.BitIncrement(x)"
        },
        {
            "nested type", Members("class Stepper<T> where T : IFloatingPointIeee754<T> " +
                "{ public class Nested { public static T Down(T x) => T.BitDecrement(x); } }"),
            "T.BitDecrement(x)"
        },
        {
            "default interface member", Members(
                "interface IStepper<T> where T : IFloatingPointIeee754<T> { T Step(T x) => T.BitIncrement(x); }"),
            "T.BitIncrement(x)"
        },
        {
            "awaiter extension",
            """
            using System.Numerics;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            static class AwaitSteps
            {
                public static TaskAwaiter GetAwaiter<T>(this T value) where T : IFloatingPointIeee754<T>
                {
                    _ = T.BitIncrement(value);
                    return Task.CompletedTask.GetAwaiter();
                }

                public static async Task Run() => await 0f;
            }
            """,
            "T.BitIncrement(value)"
        },
        {
            "interface implementation",
            """
            using System.Numerics;

            interface IStep
            {
                T Step<T>(T value) where T : IFloatingPointIeee754<T>;
            }

            sealed class Stepper : IStep
            {
                public T Step<T>(T value) where T : IFloatingPointIeee754<T> => T.BitIncrement(value);
            }

            static class Demo
            {
                public static float Run()
                {
                    IStep stepper = new Stepper();
                    return stepper.Step(0f);
                }
            }
            """,
            "T.BitIncrement(value)"
        },
        {
            "hidden partial declaration",
            """
            using System.Numerics;

            #line default
            interface IStepper<T> where T : IFloatingPointIeee754<T>
            {
                static T Step() => T.BitIncrement(T.Zero);
            }

            partial class Bridge<T> where T : IFloatingPointIeee754<T> { }

            #line hidden
            partial class Bridge<T> :
            #line default
                IStepper<T> where T : IFloatingPointIeee754<T> { }

            sealed class Use : Bridge<float> { }
            """,
            "T.BitIncrement(T.Zero)"
        },
        // The generic step is intentionally reported even when its only specialization uses double.
        {
            "double-only specialization", Members(
                "static T Step<T>(T x) where T : IFloatingPointIeee754<T> => T.BitDecrement(x);\n" +
                "double F() => Step(0d);"),
            "T.BitDecrement(x)"
        },
        {
            "unused generic", Members("T Unused<T>(T x) where T : IFloatingPointIeee754<T> => T.BitIncrement(x);"),
            "T.BitIncrement(x)"
        },
    };

    [Theory]
    [MemberData(nameof(GenericStepSites))]
    public async Task AGenericStepReportsOnceWhereItIsWritten(string shape, string source, string expected)
    {
        _ = shape;
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, source);

        Assert.Equal(new[] { expected }, Flagged(diagnostics));
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public async Task APartialGenericMethodReportsInItsImplementation()
    {
        const string definition = """
            using System.Numerics;
            partial class Sample
            {
                private static partial T Step<T>(T value) where T : IFloatingPointIeee754<T>;
                float F() => Step(1f);
            }
            """;
        const string implementation = """
            using System.Numerics;
            partial class Sample
            {
                private static partial T Step<T>(T value) where T : IFloatingPointIeee754<T> => T.BitIncrement(value);
            }
            """;

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, definition, implementation);

        Assert.Equal(new[] { "T.BitIncrement(value)" }, Flagged(diagnostics));
    }

    [Fact]
    public async Task OtherGenericMathMembersAreAllowed()
    {
        const string source = "static T F<T>(T x) where T : IFloatingPointIeee754<T> => T.Sqrt(x + T.One);";

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(source));

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericAndDirectStepsFollowGeneratedCodePolicy(bool hiddenRegion)
    {
        const string steps = "static T Step<T>(T x) where T : IFloatingPointIeee754<T> => T.BitIncrement(x);\n" + BclStep;
        string source = hiddenRegion
            ? Members("#line hidden\n" + steps + "\n#line default")
            : "// <auto-generated/>\n" + Members(steps);

        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, source);

        Assert.Empty(diagnostics);
    }
}
