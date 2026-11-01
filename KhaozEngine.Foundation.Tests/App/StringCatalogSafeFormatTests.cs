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
        [Theory]
        [InlineData("Score: {0}", 7, "Score: 7")]
        [InlineData("Score: {0}, Bonus: {1}", 7, "Score: {0}, Bonus: {1}")]
        [InlineData("50% of {0", 1, "50% of {0")]
        public void Formats_valid_templates_and_preserves_templates_that_cannot_be_formatted(
            string template, int argument, string expected)
        {
            Assert.Equal(expected,
                IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, template, new object?[] { argument }));
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
