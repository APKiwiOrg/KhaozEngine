using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace KhaozEngine.Tests.CodeHealth;

public class PrivateReflectionTests
{
    [Fact]
    public async Task NonPublicFlag_Fires()
    {
        const string source = """
            using System.Reflection;
            public class R
            {
                public BindingFlags Flags => BindingFlags.NonPublic | BindingFlags.Instance;
            }
            """;
        var diagnostic = Assert.Single(await Run(source));
        Assert.Equal("KETEST002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("NonPublic", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
        Assert.Equal("'Tests/R.cs' has 1 non-public reflection access(es), .testhealth-baseline allows 0. " +
            "Test through the public API, or an internal seam with InternalsVisibleTo. " +
            "A structural guard that must reflect over non-public members needs the user to exempt the file in .testhealth-baseline.",
            diagnostic.GetMessage());
    }

    [Fact]
    public async Task UnsafeAccessor_Fires()
    {
        const string source = """
            using System.Runtime.CompilerServices;
            public class R
            {
                [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_value")]
                public static extern ref int Value(object instance);
            }
            """;
        var diagnostic = Assert.Single(await Run(source));
        Assert.Equal("KETEST002", diagnostic.Id);
        Assert.Equal("UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"_value\")",
            source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task PublicOnlyReflection_IsSilent() =>
        Assert.Empty(await Run("""
            using System.Reflection;
            public class R
            {
                public BindingFlags Flags => BindingFlags.Public | BindingFlags.Instance;
            }
            """));

    [Fact]
    public async Task NonPublicReferences_IncludeAliasStaticImportAndNameof()
    {
        var diagnostics = await Run("""
            using Flags = System.Reflection.BindingFlags;
            using static System.Reflection.BindingFlags;
            public class R
            {
                public Flags First => Flags.NonPublic;
                public Flags Second => NonPublic;
                public string Name => nameof(Flags.NonPublic);
            }
            """);
        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.StartsWith("'Tests/R.cs' has 3 ", diagnostic.GetMessage()));
    }

    [Fact]
    public async Task UnsafeAccessor_FullyQualifiedAndAliasedApplications_Fire()
    {
        var diagnostics = await Run("""
            using Accessor = System.Runtime.CompilerServices.UnsafeAccessorAttribute;
            using System.Runtime.CompilerServices;
            public class R
            {
                [System.Runtime.CompilerServices.UnsafeAccessorAttribute(UnsafeAccessorKind.Field, Name = "_first")]
                public static extern ref int First(object instance);
                [Accessor(UnsafeAccessorKind.Field, Name = "_second")]
                public static extern ref int Second(object instance);
            }
            """);
        Assert.Equal(2, diagnostics.Length);
    }

    [Fact]
    public async Task SameNamedSymbols_StringsAndComments_AreSilent() =>
        Assert.Empty(await Run("""
            using System;
            public enum BindingFlags { NonPublic }
            public sealed class UnsafeAccessorAttribute : Attribute { }
            public class R
            {
                // System.Reflection.BindingFlags.NonPublic
                [UnsafeAccessor]
                public BindingFlags Flags => BindingFlags.NonPublic;
                public string Text => "BindingFlags.NonPublic";
                public Type AttributeType => typeof(System.Runtime.CompilerServices.UnsafeAccessorAttribute);
            }
            """));

    [Fact]
    public async Task FlagsAndAccessor_ShareTheFileAllowance()
    {
        var diagnostics = await TestHealthHarness.Run(new[] { ("/repo/Tests/R.cs", """
            using System.Reflection;
            using System.Runtime.CompilerServices;
            public class R
            {
                public BindingFlags Flags => BindingFlags.NonPublic;
                [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_value")]
                public static extern ref int Value(object instance);
            }
            """) }, baseline: "1 KETEST002 Tests/R.cs\n");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.StartsWith("'Tests/R.cs' has 2 ", diagnostic.GetMessage()));
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Run(string source) =>
        TestHealthHarness.Run(new[] { ("/repo/Tests/R.cs", source) }, baseline: "");
}
