using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using KhaozEngine.App;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>
    /// Verifies the showcase's localization catalog is wired correctly: the embedded <c>ShowcaseStrings.resx</c>
    /// resolves, and the generated <see cref="ShowcaseStrings"/> members match its complete key set.
    /// </summary>
    public class ShowcaseStringsTests
    {
        static ResourceManager Resources() =>
            new("KhaozEngine.Showcase.ShowcaseStrings", typeof(ShowcaseApp).Assembly);

        static ResourceStringCatalog Catalog() => new(Resources());

        [Fact]
        public void Resx_ResolvesKnownKeys()
        {
            var cat = Catalog();
            Assert.Equal("KhaozEngine Showcase", cat.Get("Hub.Title"));
            Assert.Equal("Boot screen", cat.Get("Room.Boot.Title"));
            Assert.Equal("Pause overlay", cat.Get("Screens.Overlay"));
        }

        [Fact]
        public void GeneratedStringIds_MatchNeutralResxKeys()
        {
            string[] generatedKeys = typeof(ShowcaseStrings)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(StringId))
                .Select(field => ((StringId)field.GetValue(null)!).Key)
                .OrderBy(key => key, System.StringComparer.Ordinal)
                .ToArray();
            using ResourceSet resources = Resources().GetResourceSet(
                CultureInfo.InvariantCulture,
                createIfNotExists: true,
                tryParents: false)!;
            string[] resourceKeys = resources.Cast<DictionaryEntry>()
                .Where(entry => entry.Value is string)
                .Select(entry => (string)entry.Key)
                .OrderBy(key => key, System.StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(resourceKeys, generatedKeys);
        }
    }
}
