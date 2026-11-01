using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace KhaozEngine.Tests.CodeHealth;

public class LoopOnlyAssertionTests
{
    [Fact]
    public async Task AssertsOnlyInForeachOverResult_Fires() =>
        await AssertFinding("foreach (var item in GetItems()) Assert.True(item > 0);");

    [Fact]
    public async Task AssertAllOverResult_Fires() =>
        await AssertFinding("Assert.All(GetItems(), item => Assert.True(item > 0));");

    [Fact]
    public async Task CountAssertedBeforeLoop_IsSilent() =>
        Assert.Empty(await RunBody("""
            var items = GetItems();
            Assert.Equal(3, items.Length);
            foreach (var item in items) Assert.True(item > 0);
            """));

    [Fact]
    public async Task NotEmptyBeforeLoop_IsSilent() =>
        Assert.Empty(await RunBody("""
            var items = GetItems();
            Assert.NotEmpty(items);
            foreach (var item in items) Assert.True(item > 0);
            """));

    [Fact]
    public async Task LoopOverArrayLiteral_IsSilent() =>
        Assert.Empty(await RunBody("foreach (var item in new[] { 1, 2 }) Assert.True(item > 0);"));

    [Fact]
    public async Task ForWithLiteralBounds_IsSilent() =>
        Assert.Empty(await RunBody("for (int i = 0; i < 4; i++) Assert.True(i >= 0);"));

    [Fact]
    public async Task AssertCollection_IsSilent() =>
        Assert.Empty(await RunBody("Assert.Collection(GetItems(), item => Assert.True(item > 0));"));

    [Fact]
    public async Task HelperOnlyAssertions_IsSilent() =>
        Assert.Empty(await RunBody("foreach (var item in GetItems()) AssertShape(item);"));

    [Fact]
    public async Task DerivedFactAttribute_IsChecked() =>
        await AssertFinding("foreach (var item in GetItems()) Assert.True(item > 0);", "GpuFact");

    [Fact]
    public async Task NonTestMethodWithLoop_IsSilent() =>
        Assert.Empty(await RunBody("foreach (var item in GetItems()) Assert.True(item > 0);", attribute: ""));

    [Fact]
    public async Task OneFindingPerMethod_NotPerAssert() =>
        await AssertFinding("""
            foreach (var item in GetItems())
            {
                Assert.True(item > 0);
                Assert.Equal(1, item);
            }
            """);

    [Theory]
    [InlineData("AssertAlias.All(GetItems(), item => Assert.True(item > 0));")]
    [InlineData("All(GetItems(), item => True(item > 0));")]
    [InlineData("Assert.All(GetItems(), item => { });")]
    [InlineData("Assert.All(action: item => Assert.True(item > 0), collection: GetItems());")]
    [InlineData("Assert.All(GetItems(), (item, index) => Assert.Equal(index, item));")]
    [InlineData("Assert.All(GetItems(), item => Assert.Collection(new[] { item }, x => Assert.True(x > 0)));")]
    [InlineData("Assert.All(GetItems(), item => Assert.Throws<Exception>(() => Assert.True(item > 0)));")]
    [InlineData("for (int i = 0; i < GetItems().Length; i++) Assert.True(i >= 0);")]
    [InlineData("for (int i = 4; i < 4; i++) Assert.True(i >= 0);")]
    [InlineData("while (GetItems().Length > 0) { Assert.True(true); break; }")]
    [InlineData("foreach (var item in Array.Empty<int>()) Assert.True(item > 0);")]
    [InlineData("foreach (var item in new int[0]) Assert.True(item > 0);")]
    [InlineData("foreach (var item in Enumerable.Range(0, 0)) Assert.True(item > 0);")]
    [InlineData("foreach (var item in Enumerable.Range(0, GetItems().Length)) Assert.True(item > 0);")]
    [InlineData("foreach (var item in new[] { 1 }) foreach (var other in GetItems()) Assert.Equal(item, other);")]
    [InlineData("foreach (var item in GetItems()) do { Assert.True(item > 0); } while (false);")]
    [InlineData("var items = new[] { 1, 2 }; foreach (var item in items) Assert.True(item > 0);")]
    [InlineData("foreach (var (left, right) in GetPairs()) Assert.Equal(left, right);")]
    [InlineData("foreach (var item in PretendEnumerable.Range(0, 2)) Assert.True(item > 0);")]
    public async Task PossiblyEmptyGuard_Fires(string body) => await AssertFinding(body);

    [Theory]
    [InlineData("foreach (var item in new int[] { 1 }) Assert.True(item > 0);")]
    [InlineData("foreach (var item in new int[2]) Assert.True(item >= 0);")]
    [InlineData("foreach (var item in new int[2, 3]) Assert.True(item >= 0);")]
    [InlineData("foreach (var item in (int[])[1, 2]) Assert.True(item > 0);")]
    [InlineData("foreach (var item in (int[])[1, ..GetItems()]) Assert.True(item > 0);")]
    [InlineData("foreach (var item in new List<int> { 1, 2 }) Assert.True(item > 0);")]
    [InlineData("foreach (var item in Enumerable.Range(0, 2)) Assert.True(item >= 0);")]
    [InlineData("foreach (var item in Range(0, 1 + 1)) Assert.True(item >= 0);")]
    [InlineData("foreach (var item in Enumerable.Range(count: 2, start: 0)) Assert.True(item >= 0);")]
    [InlineData("foreach (var item in Enum.GetValues<DayOfWeek>()) Assert.True((int)item >= 0);")]
    [InlineData("foreach (var item in Enum.GetValues(typeof(DayOfWeek))) Assert.NotNull(item);")]
    [InlineData("for (int i = 4; i > 0; i--) Assert.True(i > 0);")]
    [InlineData("for (int i = -1; i <= -1; i++) Assert.True(i < 0);")]
    [InlineData("for (int i = 0; 4 > i; i++) Assert.True(i >= 0);")]
    [InlineData("int i; for (i = 0; i < 4; i++) Assert.True(i >= 0);")]
    [InlineData("do { Assert.True(true); } while (false);")]
    [InlineData("Assert.All(new[] { 1, 2 }, item => Assert.True(item > 0));")]
    [InlineData("Assert.All(new[] { 1 }, item => { foreach (var other in GetItems()) Assert.Equal(item, other); });")]
    [InlineData("Assert.All((int[])[1, 2], item => Assert.True(item > 0));")]
    [InlineData("Assert.All(new List<int> { 1 }, item => Assert.True(item > 0));")]
    [InlineData("Assert.All(new List<int>(), item => Assert.True(item > 0)); Assert.True(true);")]
    [InlineData("Assert.Throws<Exception>(() => Assert.True(true));")]
    [InlineData("Action check = () => Assert.True(true); foreach (var item in GetItems()) check();")]
    [InlineData("void Check() { Assert.True(true); } foreach (var item in GetItems()) Check();")]
    [InlineData("foreach (var item in GetItems()) { void Check() { Assert.True(true); } Check(); }")]
    [InlineData("foreach (var item in GetItems()) { void Check() => Assert.True(true); Check(); }")]
    [InlineData("foreach (var item in GetItems()) Assert.True(item > 0); Assert.NotEmpty(GetItems());")]
    [InlineData("foreach (var item in InspectSource(() => Assert.True(true))) Assert.True(item > 0);")]
    public async Task DirectAssertionOrNonEmptyHeader_IsSilent(string body) =>
        Assert.Empty(await RunBody(body));

    [Fact]
    public async Task TheoryAttribute_IsChecked() =>
        await AssertFinding("foreach (var item in GetItems()) Assert.True(item > 0);", "Theory");

    [Fact]
    public async Task ExpressionBodiedTest_IsChecked()
    {
        const string source = """
            using System;
            using Xunit;
            public class R
            {
                [Fact] public void Inspect() => Assert.All(Array.Empty<int>(), item => Assert.True(item > 0));
            }
            """;
        await AssertDiagnostic(source);
    }

    [Fact]
    public async Task NonXunitAssert_IsSilent() =>
        Assert.Empty(await Run("""
            public static class Assert { public static void True(bool value) { } }
            public class R
            {
                [Xunit.Fact] public void Inspect()
                {
                    foreach (var item in System.Array.Empty<int>()) Assert.True(item > 0);
                }
            }
            """));

    [Theory]
    [InlineData("for (double i = 0.5; i < 1.5; i++) Assert.True(i > 0);")]
    [InlineData("for (float i = 0.5f; i <= 0.5f; i++) Assert.True(i > 0);")]
    [InlineData("for (decimal i = -0.5m; i < 0m; i++) Assert.True(i < 0);")]
    [InlineData("for (double i = 0.1; i < 0.10000000000000002; i++) Assert.True(i > 0);")]
    [InlineData("for (float i = 16777217; i <= 16777216; i++) Assert.True(i > 0);")]
    [InlineData("for (double i = 9007199254740993; i <= 9007199254740992; i++) Assert.True(i > 0);")]
    public async Task NonEmptyNumericLiteralFor_IsSilent(string body) =>
        Assert.Empty(await RunBody(body));

    private static async Task AssertFinding(string body, string attribute = "Fact") =>
        await AssertDiagnostic(Source(body, attribute));

    private static async Task AssertDiagnostic(string source)
    {
        var diagnostic = Assert.Single(await Run(source));
        Assert.Equal("KETEST003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Inspect", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    private static Task<ImmutableArray<Diagnostic>> RunBody(string body, string attribute = "Fact") =>
        Run(Source(body, attribute));

    private static string Source(string body, string attribute) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Xunit;
        using AssertAlias = Xunit.Assert;
        using static Xunit.Assert;
        using static System.Linq.Enumerable;
        public class GpuFactAttribute : FactAttribute { }
        public static class PretendEnumerable
        {
            public static int[] Range(int start, int count) => Array.Empty<int>();
        }
        public class R
        {
            {{(attribute.Length == 0 ? "" : "[" + attribute + "]")}}
            public void Inspect()
            {
                {{body}}
            }
            private static int[] GetItems() => Array.Empty<int>();
            private static (int, int)[] GetPairs() => Array.Empty<(int, int)>();
            private static int[] InspectSource(Action action) => GetItems();
            private static void AssertShape(int value) => Assert.True(value > 0);
        }
        """;

    private static Task<ImmutableArray<Diagnostic>> Run(string source) =>
        TestHealthHarness.Run(new[] { ("/repo/Tests/R.cs", source) }, baseline: "", mode: "report");
}
