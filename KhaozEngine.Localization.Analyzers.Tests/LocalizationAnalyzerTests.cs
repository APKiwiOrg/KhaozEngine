using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Localization.Analyzers.Tests
{
    public class LocalizationAnalyzerTests
    {
        // ---- KELOC001: raw string passed to a [LocalizationStringSink] member ----

        [Fact]
        public async Task KELOC001_FiresOnStringSinkCall()
        {
            var src = @"
using KhaozEngine.App;
class Sink { [LocalizationStringSink] public static void Label(string s){} }
class C { void M(){ Sink.Label(""hi""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC001");
        }

        [Fact]
        public async Task KELOC001_FiresOnStringSinkConstructor()
        {
            var src = @"
using KhaozEngine.App;
class Widget { [LocalizationStringSink] public Widget(string s){} }
class C { void M(){ var w = new Widget(""hi""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC001");
        }

        [Fact]
        public async Task KELOC001_FiresOnMultiArgStringSink()
        {
            // Mirrors PopupRow.Stat(string label, string value, ...): a multi-arg raw-string sink is flagged.
            var src = @"
using KhaozEngine.App;
using System.Numerics;
class PopupRow { [LocalizationStringSink] public static PopupRow Stat(string label, string value, Vector4 color) => default; }
class C { void M(){ PopupRow.Stat(""Name"", ""value"", default); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC001");
        }

        [Fact]
        public async Task KELOC001_SilentOnLocalizedOverload()
        {
            var src = @"
using KhaozEngine.App;
class Sink {
    public static void Label(LocalizedText t){}
    [LocalizationStringSink] public static void Label(string s){}
}
class C { void M(){ Sink.Label(new StringId(""k"")); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC001");
        }

        // ---- KELOC002: LocalizedText.Raw outside exempt / debug ----

        [Fact]
        public async Task KELOC002_FiresOnRawInNormalCode()
        {
            var src = @"
using KhaozEngine.App;
class C { void M(){ var x = LocalizedText.Raw(""v1""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC002_SilentUnderExemptType()
        {
            var src = @"
using KhaozEngine.App;
[LocalizationExempt] class C { void M(){ var x = LocalizedText.Raw(""v1""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC002_SilentUnderExemptMethod()
        {
            var src = @"
using KhaozEngine.App;
class C { [LocalizationExempt] void M(){ var x = LocalizedText.Raw(""v1""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC002_SilentUnderExemptAssembly()
        {
            var src = @"
using KhaozEngine.App;
[assembly: LocalizationExempt]
class C { void M(){ var x = LocalizedText.Raw(""v1""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC002_SilentUnderConditionalDebugMethod()
        {
            var src = @"
using KhaozEngine.App;
class C { [System.Diagnostics.Conditional(""DEBUG"")] void M(){ var x = LocalizedText.Raw(""v1""); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC002_SilentInsideActiveIfDebugRegion()
        {
            // Parsed WITH DEBUG defined, so the #if DEBUG code is active and analyzed; the region exemption applies.
            var src = @"
using KhaozEngine.App;
class C { void M(){
#if DEBUG
    var x = LocalizedText.Raw(""v1"");
#endif
} }
";
            var diags = await AnalyzerHarness.RunWithDebug(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC002_FiresOutsideIfDebugRegion_WhenDebugDefined()
        {
            // Even with DEBUG defined, a Raw OUTSIDE the #if DEBUG region is still flagged.
            var src = @"
using KhaozEngine.App;
class C { void M(){
    var y = LocalizedText.Raw(""outside"");
#if DEBUG
    var x = LocalizedText.Raw(""inside"");
#endif
} }
";
            var diags = await AnalyzerHarness.RunWithDebug(src);
            Assert.Single(diags, d => d.Id == "KELOC002");
        }
    }
}
