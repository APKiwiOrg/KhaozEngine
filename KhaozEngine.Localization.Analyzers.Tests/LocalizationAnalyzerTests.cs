using System.Linq;
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

        // ---- KELOC003: raw string literal drawn straight to SpriteBatch.DrawString ----

        [Fact]
        public async Task KELOC003_FiresOnDrawStringLiteral()
        {
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){ b.DrawString(f, ""Play"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_FiresOnScaledOverload()
        {
            // The 5-arg overload (with scale) carries the same string 'text' argument.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){ b.DrawString(f, ""Continue"", default(Vector2), 0, 1.5f); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_FiresOnInterpolatedLiteralSegment()
        {
            // The literal segment ("Score ") of an interpolated string is as hardcoded as a plain literal (#171).
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int n){ b.DrawString(f, $""Score {n}"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentOnVariable()
        {
            // A variable (even a const) is out of scope for v1 - localize at its source.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, string label){ b.DrawString(f, label, default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentOnVerbatimLiteral()
        {
            // Verbatim (@"...") literals are out of scope for v1.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){ b.DrawString(f, @""Logs"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentOnNumericLiteral()
        {
            // No letter -> a number/format token, not player-facing copy.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){ b.DrawString(f, ""3.14"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentOnSingleGlyph()
        {
            // Single-character tokens (a close 'X', a glyph) are allowed.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){ b.DrawString(f, ""X"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentUnderExemptMethod()
        {
            var src = @"
using KhaozEngine.App;
using KhaozEngine.Render2D;
using System.Numerics;
class C { [LocalizationExempt] void M(SpriteBatch b, SpriteFont f){ b.DrawString(f, ""Debug Overlay"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentInsideActiveIfDebugRegion()
        {
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){
#if DEBUG
    b.DrawString(f, ""Frame time"", default(Vector2), 0);
#endif
} }
";
            var diags = await AnalyzerHarness.RunWithDebug(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        // ---- KELOC003: interpolated / concatenated string literal segments (#171) ----

        [Fact]
        public async Task KELOC003_FiresOnShowcaseInterpolationShape()
        {
            // The exact shape shipped unflagged in KhaozEngine.Showcase/Room2DGui.cs:439 - $""Item {i + 1}"".
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int i){ b.DrawString(f, $""Item {i + 1}"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Single(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_FiresOnEachInterpolatedLiteralSegment()
        {
            // Both surrounding literal segments (""Item "" and "" of "") are player-facing copy, the two holes are not.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int i, int n){ b.DrawString(f, $""Item {i} of {n}"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Equal(2, diags.Count(d => d.Id == "KELOC003"));
        }

        [Fact]
        public async Task KELOC003_SilentOnInterpolationWithNoLiteralText()
        {
            // A hole-only interpolation ($""{a}{b}"") carries no hardcoded copy - still out of scope.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int a, int b2){ b.DrawString(f, $""{a}{b2}"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentOnInterpolatedSingleGlyphSegment()
        {
            // The only literal segment (""%"") is a single glyph, and the number hole is out of scope.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int n){ b.DrawString(f, $""{n}%"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_SilentOnVerbatimInterpolatedString()
        {
            // Verbatim ($@""..."") interpolated strings stay out of scope, matching the plain-literal carve-out.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int n){ b.DrawString(f, $@""Path {n}"", default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_FiresOnConcatenatedLiteral()
        {
            // A literal operand of a string concatenation (""Item "" + i) is as hardcoded as a plain literal (#171).
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int i){ b.DrawString(f, ""Item "" + i, default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Single(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC003_FiresOnEachConcatenatedLiteralOperand()
        {
            // Chained concatenation flags each hardcoded literal operand ("" of "" too), not the variables.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, int i, int n){ b.DrawString(f, ""Item "" + i + "" of "" + n, default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Equal(2, diags.Count(d => d.Id == "KELOC003"));
        }

        [Fact]
        public async Task KELOC003_SilentOnConcatenatedVariablesOnly()
        {
            // Concatenation of only variables carries no hardcoded literal - out of scope.
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f, string a, string c){ b.DrawString(f, a + c, default(Vector2), 0); } }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC003");
        }

        // ---- #165: the DEBUG carve-out must NOT exempt #if !DEBUG (release-live) code ----

        [Fact]
        public async Task KELOC002_FiresInsideIfNotDebugRegion_WhenDebugUndefined()
        {
            // Parsed WITHOUT DEBUG (Release-equivalent), so the #if !DEBUG branch is live and analyzed. Its
            // LocalizedText.Raw must still be flagged - the old substring test wrongly exempted it (#165).
            var src = @"
using KhaozEngine.App;
class C { void M(){
#if !DEBUG
    var x = LocalizedText.Raw(""release-only"");
#endif
} }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC002");
        }

        [Fact]
        public async Task KELOC003_FiresInsideIfNotDebugRegion_WhenDebugUndefined()
        {
            var src = @"
using KhaozEngine.Render2D;
using System.Numerics;
class C { void M(SpriteBatch b, SpriteFont f){
#if !DEBUG
    b.DrawString(f, ""Release banner"", default(Vector2), 0);
#endif
} }
";
            var diags = await AnalyzerHarness.Run(src);
            Assert.Contains(diags, d => d.Id == "KELOC003");
        }

        [Fact]
        public async Task KELOC002_SilentInsideIfDebugDisjunction_WhenDebugDefined()
        {
            // A non-negated DEBUG in a compound condition (#if DEBUG || TRACE) still counts as a debug carve-out.
            var src = @"
using KhaozEngine.App;
class C { void M(){
#if DEBUG || TRACE
    var x = LocalizedText.Raw(""dbg"");
#endif
} }
";
            var diags = await AnalyzerHarness.RunWithDebug(src);
            Assert.DoesNotContain(diags, d => d.Id == "KELOC002");
        }
    }
}
