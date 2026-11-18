using System.Threading.Tasks;
using Xunit;
using static KhaozEngine.Tests.CodeHealth.SubnormalFloatHarness;

namespace KhaozEngine.Tests.CodeHealth;

public sealed partial class SubnormalFloatAnalyzerTests
{
    private const string GenericDeclarations = """
        class Stepper<T> where T : IFloatingPointIeee754<T>
        {
            public Stepper() => _ = T.BitIncrement(T.Zero);
        }
        interface IStepper<T> where T : IFloatingPointIeee754<T>
        {
            static T Step() => T.BitIncrement(T.Zero);
        }
        class StepAttribute<T> : Attribute where T : IFloatingPointIeee754<T>
        {
            public StepAttribute() => _ = T.BitIncrement(T.Zero);
        }
        """;

    public static TheoryData<string, string, string, bool> GenericNamingPositions
    {
        get
        {
            var rows = new TheoryData<string, string, string, bool>();
            void Add(string position, string source, string expected = "Stepper<float>")
            {
                rows.Add(position, source, expected, true);
                rows.Add(position + " with double", source.Replace("Scalar", "double"), "", true);
                rows.Add(position + " without a step", source, "", false);
            }

            // #1832. The source names the closed type in each C# position tracked by KEFP001.
            Add("local", "class Sample { object F() { Stepper<Scalar> item = null!; return item; } }");
            Add("parameter", "class Sample { void F(Stepper<Scalar> item) { } }");
            Add("return", "class Sample { Stepper<Scalar> F() => null!; }");
            Add("field", "class Sample { Stepper<Scalar> item = null!; }");
            Add("property", "class Sample { Stepper<Scalar> Item => null!; }");
            Add("typeof", "class Sample { Type F() => typeof(Stepper<Scalar>); }");
            Add("default", "class Sample { object? F() => default(Stepper<Scalar>); }");
            Add("cast", "class Sample { object F(object item) => (Stepper<Scalar>)item; }");
            Add("pattern", "class Sample { bool F(object item) => item is Stepper<Scalar>; }");
            Add("array", "class Sample { Stepper<Scalar>[]? items; }");
            Add("tuple", "class Sample { (Stepper<Scalar>, int) item; }");
            Add("nested in BCL generic", "class Sample { List<Stepper<Scalar>>? items; }");
            Add("lambda parameter", "class Sample { object F() => (Stepper<Scalar> item) => item; }");
            Add("constraint", "class Sample { void F<T>() where T : Stepper<Scalar> { } }");
            Add("attribute", "[Step<Scalar>] class Sample { }", "Step<float>");
            Add("using alias", "using Alias = global::Stepper<Scalar>; class Sample { }");
            Add("base class", "class Sample : Stepper<Scalar> { }");
            Add("interface", "class Sample : IStepper<Scalar> { }", "IStepper<float>");
            Add("method type argument", "class Sample { static T Id<T>() => default!; object F() => Id<Stepper<Scalar>>(); }");
            Add("nameof", "class Sample { string F() => nameof(Stepper<Scalar>); }");
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(GenericNamingPositions))]
    public async Task AGenericNameReportsAtTheNamedSpecialization(string position, string syntax, string expected, bool steps)
    {
        _ = position;
        string declarations = steps ? GenericDeclarations : GenericDeclarations.Replace("T.BitIncrement(T.Zero)", "T.Zero");
        string source = "using System; using System.Collections.Generic; using System.Numerics;\n" +
            syntax.Replace("Scalar", "float") + "\n" + declarations;
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, source);

        Assert.Equal(expected.Length == 0 ? [] : new[] { expected }, Flagged(diagnostics));
    }

    private const string InferredTypes = """
        struct Stepper<T> : IDisposable where T : IFloatingPointIeee754<T>
        {
            public void Dispose() => _ = T.BitIncrement(T.Zero);
            public Enumerator GetEnumerator()
            {
                _ = T.BitIncrement(T.Zero);
                return default;
            }
            public void Deconstruct(out T item, out int count)
            {
                item = T.BitIncrement(T.Zero);
                count = 0;
            }
            public static T operator +(Stepper<T> value, T item) => T.BitIncrement(item);
            public struct Enumerator
            {
                public bool MoveNext() => false;
                public T Current => T.Zero;
            }
        }
        static Stepper<T> Make<T>(T value) where T : IFloatingPointIeee754<T> => default;
        """;

    public static TheoryData<string, string, string, string[]> InferredClosures => new()
    {
        // The collection expression constructs a second value of the type inferred at Make.
        {
            "collection", "class Stepper<T> : System.Collections.Generic.List<T> where T : IFloatingPointIeee754<T> " +
                "{ public Stepper() => _ = T.BitIncrement(T.Zero); }\n" +
                "static Stepper<T> Make<T>(T value) where T : IFloatingPointIeee754<T> => new();",
            "object F() { var value = Make(1f); value = []; return value; }", ["Make(1f)", "[]"]
        },
        // The compiler-selected members consume the type already inferred at Make. Its call is the source of the closure.
        { "foreach", InferredTypes, "void F() { var value = Make(1f); foreach (var item in value) { } }", ["Make(1f)"] },
        { "using", InferredTypes, "void F() { var value = Make(1f); using var owned = value; }", ["Make(1f)"] },
        { "positional pattern", InferredTypes, "bool F() { var value = Make(1f); return value is (0f, 0); }", ["Make(1f)"] },
        { "deconstruction", InferredTypes, "int F() { var value = Make(1f); var (_, count) = value; return count; }", ["Make(1f)"] },
        { "binary operator", InferredTypes, "float F() { var value = Make(1f); return value + 0f; }", ["Make(1f)"] },
    };

    [Theory]
    [MemberData(nameof(InferredClosures))]
    public async Task CompilerSelectedMembersUseTheInferredSpecialization(string construct, string declarations, string use, string[] expected)
    {
        _ = construct;
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, Members(declarations + "\n" + use));

        Assert.Equal(expected, Flagged(diagnostics));
    }

    [Fact]
    public async Task APartialGenericMethodUsesItsDefinitionParameter()
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

        Assert.Equal(new[] { "Step(1f)" }, Flagged(diagnostics));
    }

    [Theory]
    [InlineData(true, "(uint)(Word)Bits", false)]
    [InlineData(true, "unchecked((uint)(Word)Bits)", true)]
    [InlineData(false, "unchecked(checked((uint)(Word)Bits))", false)]
    public async Task ReadonlyEnumNarrowingUsesTheEffectiveCheckedContext(bool checkOverflow, string bits, bool flagged)
    {
        string members = "enum Word : byte { }\nstatic readonly uint Bits = 0x101u;\n" +
            $"float F() => BitConverter.UInt32BitsToSingle({bits});";
        var diagnostics = await Run("on", withXunit: false, warningsAsErrors: false, checkOverflow, Members(members));

        Assert.Equal(flagged ? new[] { "Bits" } : [], Flagged(diagnostics));
    }
}
