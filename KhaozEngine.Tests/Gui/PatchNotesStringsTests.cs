using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Tests.App; // DictionaryCatalog
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="PatchNotesStrings"/> is the localization-aware chrome text for the patch-notes screen
    /// (title, close, empty state, category labels): every key resolves through the ambient
    /// <see cref="LocalizationContext.Catalog"/> and falls back to the built-in English default when no
    /// catalog is wired or the key is absent, mirroring <see cref="UpdateOverlayStrings"/>. These mutate the
    /// process-wide ambient catalog, so they share the serialized AmbientLocalization collection.
    /// </summary>
    [Collection("AmbientLocalization")]
    public sealed class PatchNotesStringsTests
    {
        [Fact]
        public void No_catalog_wired_resolves_english_defaults()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = null;
                Assert.Equal("Patch Notes", PatchNotesStrings.Resolve(PatchNotesStrings.Title));
                Assert.Equal("Close", PatchNotesStrings.Resolve(PatchNotesStrings.Close));
                Assert.Equal("No patch notes available.", PatchNotesStrings.Resolve(PatchNotesStrings.Empty));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Theory]
        [InlineData(PatchNoteCategory.New, "New")]
        [InlineData(PatchNoteCategory.Major, "Major")]
        [InlineData(PatchNoteCategory.Minor, "Minor")]
        [InlineData(PatchNoteCategory.Rebalance, "Rebalance")]
        [InlineData(PatchNoteCategory.Bug, "Bug fixes")]
        [InlineData(PatchNoteCategory.Other, "Notes")]
        public void CategoryLabel_covers_every_enum_value_with_english_default(PatchNoteCategory category, string expected)
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = null;
                StringId id = PatchNotesStrings.CategoryLabel(category);
                Assert.Equal(expected, PatchNotesStrings.Resolve(id));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Wired_catalog_overrides_the_english_default()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog()
                    .Add("patchnotes.title", "Notes de mise a jour")
                    .Add("patchnotes.category.bug", "Corrections");

                Assert.Equal("Notes de mise a jour", PatchNotesStrings.Resolve(PatchNotesStrings.Title));
                Assert.Equal("Corrections",
                    PatchNotesStrings.Resolve(PatchNotesStrings.CategoryLabel(PatchNoteCategory.Bug)));
                // A key absent from the wired catalog still falls back to English, not a raw key.
                Assert.Equal("Close", PatchNotesStrings.Resolve(PatchNotesStrings.Close));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void EnglishDefaults_carry_every_key_and_never_show_a_raw_key()
        {
            var en = PatchNotesStrings.EnglishDefaults;
            Assert.Equal("Patch Notes", en.Get(PatchNotesStrings.Title.Key));
            Assert.Equal("Close", en.Get(PatchNotesStrings.Close.Key));
            Assert.Equal("No patch notes available.", en.Get(PatchNotesStrings.Empty.Key));
            Assert.True(en.TryGet(PatchNotesStrings.CategoryLabel(PatchNoteCategory.Rebalance).Key, out string rebalance));
            Assert.Equal("Rebalance", rebalance);
            Assert.False(en.TryGet("patchnotes.nonexistent", out _));
        }
    }
}
