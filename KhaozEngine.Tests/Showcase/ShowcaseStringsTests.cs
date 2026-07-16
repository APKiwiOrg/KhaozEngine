using System.Reflection;
using System.Resources;
using KhaozEngine.App;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>
    /// Verifies the showcase's localization catalog is wired correctly: the embedded <c>ShowcaseStrings.resx</c>
    /// resolves, and every hand-authored <see cref="ShowcaseStrings"/> <see cref="StringId"/> constant has a
    /// matching resx entry (so no label silently renders its key).
    /// </summary>
    public class ShowcaseStringsTests
    {
        static ResourceStringCatalog Catalog() => new(
            new ResourceManager("KhaozEngine.Showcase.ShowcaseStrings", typeof(ShowcaseApp).Assembly));

        [Fact]
        public void Resx_ResolvesKnownKeys()
        {
            var cat = Catalog();
            Assert.Equal("KhaozEngine Showcase", cat.Get("Hub.Title"));
            Assert.Equal("Boot screen", cat.Get("Room.Boot.Title"));
            Assert.Equal("Pause overlay", cat.Get("Screens.Overlay"));
        }

        [Fact]
        public void EveryStringIdConstant_HasAResxEntry()
        {
            var cat = Catalog();
            foreach (FieldInfo f in typeof(ShowcaseStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(StringId)) continue;
                var id = (StringId)f.GetValue(null)!;
                // ResourceStringCatalog returns the key itself when it is absent; a present key resolves to a
                // different value, so key-equals-value means the resx is missing an entry.
                Assert.True(cat.TryGet(id.Key, out _), $"ShowcaseStrings.{f.Name} key '{id.Key}' has no resx entry");
            }
        }
    }
}
