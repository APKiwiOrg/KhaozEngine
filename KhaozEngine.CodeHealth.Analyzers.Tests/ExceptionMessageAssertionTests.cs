using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace KhaozEngine.Tests.CodeHealth;

public class ExceptionMessageAssertionTests
{
    [Theory]
    [InlineData("Assert.Contains(\"bad\", ex.Message);")]
    [InlineData("Assert.Equal(\"bad input\", ex.Message);")]
    [InlineData("Assert.StartsWith(\"bad\", ex.Message);")]
    [InlineData("Assert.True(ex.Message.Contains(\"bad\"));")]
    [InlineData("Assert.Contains(\"bad\", Assert.Throws<ArgumentException>(() => Throw()).Message);")]
    [InlineData("Assert.NotEqual(\"good input\", ex.Message);")]
    [InlineData("Assert.EndsWith(\"input\", ex.Message);")]
    [InlineData("Assert.Matches(\"bad.*\", ex.Message);")]
    [InlineData("Assert.False(ex.Message.Contains(\"good\"));")]
    [InlineData("AssertAlias.Equal(\"bad input\", ex.Message);")]
    [InlineData("Equal(\"bad input\", ex.Message);")]
    [InlineData("Assert.Equal(\"bad input\", ex?.Message);")]
    [InlineData("Assert.True(Check(() => Assert.Equal(\"bad input\", ex.Message)));")]
    [InlineData("Assert.True(ex.Message.Contains(\"bad\"), \"explains the failure\");")]
    [InlineData("Assert.True(userMessage: \"explains\", condition: ex.Message.StartsWith(\"bad\"));")]
    public async Task WordingAssertion_Fires(string statement)
    {
        var diagnostic = Assert.Single(await RunStatement(statement));
        Assert.Equal("KETEST001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Theory]
    [InlineData("Assert.DoesNotContain(\"secret\", ex.Message);")]
    [InlineData("Assert.DoesNotMatch(\"tok_[a-z]+\", ex.Message);")]
    [InlineData("Assert.NotNull(ex.Message);")]
    [InlineData("Assert.Equal(\"value\", ex.ParamName);")]
    [InlineData("Assert.Contains(\"bad\", diagnostic.GetMessage());")]
    [InlineData("Assert.DoesNotContain(\"secret\", ex.Message.ToLowerInvariant());")]
    [InlineData("Assert.IsType<string>(ex.Message);")]
    [InlineData("var message = ex.Message;")]
    [InlineData("Assert.Throws<ArgumentException>(() => throw new ArgumentException(ex.Message));")]
    [InlineData("Assert.True(ex is null, ex.Message);")]
    [InlineData("Assert.False(ex is not null, ex?.Message);")]
    [InlineData("Assert.True(userMessage: ex.Message, condition: ex is null);")]
    [InlineData("Assert.False(userMessage: \"load failed: \" + ex.Message, condition: false);")]
    public async Task NonWordingUse_IsSilent(string statement) =>
        Assert.Empty(await RunStatement(statement));

    [Theory]
    [InlineData("Assert.DoesNotContain(\"secret\", ex.Message);")]
    [InlineData("Assert.DoesNotMatch(\"tok_[a-z]+\", ex.Message);")]
    [InlineData("Assert.NotNull(ex.Message);")]
    [InlineData("Assert.DoesNotContain(\"secret\", ex.Message.ToLowerInvariant());")]
    public async Task ExcludedAssertInsideCountedAssert_IsSilent(string statement) =>
        Assert.Empty(await RunStatement("Assert.True(Check(() => { " + statement + " }));"));

    [Fact]
    public async Task DerivedExceptionMessage_Fires()
    {
        var diagnostic = Assert.Single(await Run("""
            using System;
            using Xunit;
            public class CustomException : Exception
            {
                public override string Message => "bad input";
            }
            public class DerivedException : CustomException
            {
                public override string Message => "bad derived input";
            }
            public class R
            {
                [Fact]
                public void Inspect()
                {
                    var ex = new DerivedException();
                    Assert.Equal("bad derived input", ex.Message);
                }
            }
            """));
        Assert.Equal("KETEST001", diagnostic.Id);
    }

    [Fact]
    public async Task NonExceptionMessageProperty_IsSilent() =>
        Assert.Empty(await Run("""
            using Xunit;
            public record Result(string Message);
            public class R
            {
                [Fact]
                public void Inspect()
                {
                    Assert.Equal("value", new Result("value").Message);
                }
            }
            """));

    [Fact]
    public async Task HiddenExceptionMessageProperty_IsSilent() =>
        Assert.Empty(await Run("""
            using System;
            using Xunit;
            public class CustomException : Exception
            {
                public new string Message => "custom";
            }
            public class R
            {
                [Fact]
                public void Inspect()
                {
                    Assert.Equal("custom", new CustomException().Message);
                }
            }
            """));

    [Fact]
    public async Task NonXunitAssert_IsSilent() =>
        Assert.Empty(await Run("""
            using System;
            public static class Assert
            {
                public static void Equal(string expected, string actual) { }
            }
            public class R
            {
                [Xunit.Fact]
                public void Inspect()
                {
                    Assert.Equal("bad input", new Exception().Message);
                }
            }
            """));

    [Fact]
    public async Task MultipleMessageAccesses_ReportEachLocationOnce()
    {
        const string source = """
            using System;
            using Xunit;
            public class R
            {
                [Fact]
                public void Inspect()
                {
                    var ex = new Exception("bad input");
                    var other = new Exception("bad input");
                    Assert.Equal(ex.Message, other.Message);
                }
            }
            """;
        var diagnostics = await Run(source);
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("KETEST001", diagnostic.Id));
        Assert.Equal(new[] { "ex.Message", "other.Message" }, diagnostics
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .Select(diagnostic => source.Substring(diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length)));
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> RunStatement(string statement) =>
        Run($$"""
            using System;
            using Microsoft.CodeAnalysis;
            using Xunit;
            using AssertAlias = Xunit.Assert;
            using static Xunit.Assert;
            public class R
            {
                [Fact]
                public void Inspect()
                {
                    var ex = new ArgumentException("bad input", "value");
                    Diagnostic diagnostic = null;
                    {{statement}}
                }
                private static void Throw() => throw new ArgumentException("bad input");
                private static bool Check(Action assertion) => true;
            }
            """);

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Run(string source) =>
        TestHealthHarness.Run(new[] { ("/repo/Tests/R.cs", source) }, baseline: "", mode: "report");
}
