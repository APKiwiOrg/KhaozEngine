using System.Globalization;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    /// <summary>
    /// <see cref="IStringCatalog.SafeFormat"/> is the one place the never-throw promise on
    /// <see cref="IStringCatalog.Format"/> lives, so every catalog inherits it by routing through here (#163).
    /// The failure it absorbs is translator-authored content, a template whose placeholders the call site's
    /// arguments cannot satisfy, which used to throw out of a Gui draw call with nothing above it to catch.
    /// </summary>
    public class StringCatalogSafeFormatTests
    {
        [Fact]
        public void Substitutes_args_into_a_well_formed_template()
        {
            Assert.Equal("Score: 7",
                IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "Score: {0}", new object?[] { 7 }));
        }

        [Fact]
        public void Formats_through_the_provider_it_is_handed()
        {
            // A cloned NumberFormatInfo rather than a named culture: it pins the provider being honoured without
            // depending on the host's ICU data.
            var comma = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            comma.NumberDecimalSeparator = ",";

            Assert.Equal("1.5", IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "{0:0.0}", new object?[] { 1.5 }));
            Assert.Equal("1,5", IStringCatalog.SafeFormat(comma, "{0:0.0}", new object?[] { 1.5 }));
        }

        [Fact]
        public void A_placeholder_index_the_args_cannot_satisfy_falls_back_to_the_template()
        {
            Assert.Equal("Score: {0}, Bonus: {1}",
                IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "Score: {0}, Bonus: {1}", new object?[] { 7 }));
        }

        [Fact]
        public void An_unbalanced_brace_falls_back_to_the_template()
        {
            Assert.Equal("50% of {0",
                IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "50% of {0", new object?[] { 1 }));
        }

        [Fact]
        public void No_args_at_all_falls_back_rather_than_throwing()
        {
            Assert.Equal("Attempt {0}",
                IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "Attempt {0}", System.Array.Empty<object?>()));
        }

        /// <summary>Null args means "none", not "skip formatting": a template with no placeholders still gets its
        /// doubled braces unescaped, exactly as string.Format would.</summary>
        [Fact]
        public void Null_args_is_treated_as_none_and_still_unescapes_braces()
        {
            Assert.Equal("{literal}", IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "{{literal}}", null));
        }
    }
}
