using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Dropdown option labels are a <see cref="LocalizedText"/> sink like every other player-facing widget text.
    /// The gap this covers is invisible to the localization analyzer by construction: with a bare
    /// <c>string Label</c> there is no sink parameter to flag, so a settings selector built entirely out of raw
    /// literals compiled clean. Same for the scroll panel's header title.
    /// </summary>
    [Collection("AmbientLocalization")]
    public class DropdownLocalizedTests
    {
        static readonly Rect Trigger = new(0, 0, 200, 30);

        static List<DropdownOption> Difficulty() => new()
        {
            new DropdownOption(new StringId("Difficulty.Easy"), 0),
            new DropdownOption(new StringId("Difficulty.Hard"), 1),
        };

        [Fact]
        public void Option_labels_resolve_through_the_catalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog()
                    .Add("Difficulty.Easy", "Easy").Add("Difficulty.Hard", "Hard");
                var d = new Dropdown(Difficulty(), Trigger);
                Assert.Equal("Easy", d.SelectedLabel);

                LocalizationContext.Catalog = new DictionaryCatalog()
                    .Add("Difficulty.Easy", "Facile").Add("Difficulty.Hard", "Difficile");
                Assert.Equal("Facile", d.SelectedLabel);   // re-resolves, never cached

                d.SelectByValue(1);
                Assert.Equal("Difficile", d.SelectedLabel);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void SelectedContent_is_the_unresolved_value()
        {
            var d = new Dropdown(Difficulty(), Trigger);
            Assert.False(d.SelectedContent.IsRaw);
            Assert.Equal("Difficulty.Easy", d.SelectedContent.Id.Key);
        }

        [Fact]
        public void Option_content_is_a_localized_text_sink()
        {
            PropertyInfo? content = typeof(DropdownOption).GetProperty("Content");
            Assert.NotNull(content);
            Assert.Equal(typeof(LocalizedText), content!.PropertyType);
        }

        [Fact]
        public void Raw_option_labels_still_resolve_verbatim()
        {
            var d = new Dropdown(new[] { new DropdownOption(LocalizedText.Raw("v1.2"), 0) }, Trigger);
            Assert.Equal("v1.2", d.SelectedLabel);
        }

        [Fact]
        public void The_string_option_ctor_still_compiles_and_is_obsolete()
        {
            // Kept so an existing caller keeps building, marked so it shows up. Nothing is removed.
            ConstructorInfo? ctor = typeof(DropdownOption).GetConstructor(new[] { typeof(string), typeof(int) });
            Assert.NotNull(ctor);
            Assert.NotNull(ctor!.GetCustomAttribute<System.ObsoleteAttribute>());
        }

        [Fact]
        public void ScrollablePanel_header_takes_localized_text()
        {
            MethodInfo[] headers = typeof(ScrollablePanel)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "DrawHeader").ToArray();

            Assert.Contains(headers, m => m.GetParameters()[^1].ParameterType == typeof(LocalizedText));

            MethodInfo? raw = headers.FirstOrDefault(m => m.GetParameters()[^1].ParameterType == typeof(string));
            Assert.NotNull(raw);   // the old signature stays, so a caller keeps compiling
            Assert.NotNull(raw!.GetCustomAttribute<System.ObsoleteAttribute>());
        }
    }
}
