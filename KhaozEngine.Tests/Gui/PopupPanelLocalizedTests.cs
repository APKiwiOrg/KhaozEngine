using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class PopupPanelLocalizedTests
    {
        [Fact]
        public void TitleContent_ResolvesViaAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Popup.Title", "Start game?");
                var panel = new PopupPanel { TitleContent = new StringId("Popup.Title") };
                Assert.Equal("Start game?", panel.TitleContent.Resolve());

                LocalizationContext.Catalog = new DictionaryCatalog().Add("Popup.Title", "Commencer ?");
                Assert.Equal("Commencer ?", panel.TitleContent.Resolve()); // re-resolves, not cached
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void ButtonContent_DefaultsAreCloseAndOk()
        {
            var panel = new PopupPanel();
            Assert.Equal("Close", panel.DismissContent.Resolve());
            Assert.Equal("OK", panel.PrimaryActionContent.Resolve());
        }

        [Fact]
        public void ButtonContent_ResolvesViaAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog()
                    .Add("Popup.Cancel", "Cancel").Add("Popup.Start", "Start");
                var panel = new PopupPanel
                {
                    DismissContent = new StringId("Popup.Cancel"),
                    PrimaryActionContent = new StringId("Popup.Start"),
                };
                Assert.Equal("Cancel", panel.DismissContent.Resolve());
                Assert.Equal("Start", panel.PrimaryActionContent.Resolve());
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Row_Header_ResolvesAtConstruction()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Popup.Summary", "Summary");
                var row = PopupRow.Header(new StringId("Popup.Summary"));
                Assert.Equal(PopupRowType.Header, row.Type);
                Assert.Equal("Summary", row.Label);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Row_Stat_ResolvesLabelAndValueAtConstruction()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Popup.Name", "Name");
                var color = new Vector4(0.7f, 0.85f, 1f, 1f);
                var row = PopupRow.Stat(new StringId("Popup.Name"), LocalizedText.Raw("Ada"), color);
                Assert.Equal(PopupRowType.Stat, row.Type);
                Assert.Equal("Name", row.Label);   // localized
                Assert.Equal("Ada", row.Value);     // raw user value
                Assert.Equal(color, row.ValueColor);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Row_Stat_ResolveIsSnapshot_NotReResolvedLater()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Popup.Name", "Name");
                var row = PopupRow.Stat(new StringId("Popup.Name"), LocalizedText.Raw("v"), Vector4.One);
                // The row stores the resolved string; a later catalog swap does not change it.
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Popup.Name", "Nom");
                Assert.Equal("Name", row.Label);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void ObsoleteStringShims_RoundTripAsRaw()
        {
#pragma warning disable CS0618 // exercising the back-compat string shims on purpose
            var panel = new PopupPanel
            {
                Title = "Raw title",
                DismissText = "No",
                PrimaryActionText = "Yes",
            };
            Assert.Equal("Raw title", panel.Title);
            Assert.Equal("No", panel.DismissText);
            Assert.Equal("Yes", panel.PrimaryActionText);
            // The shims store into the LocalizedText members as raw literals.
            Assert.Equal("Raw title", panel.TitleContent.Resolve());
            Assert.Equal("No", panel.DismissContent.Resolve());
            Assert.Equal("Yes", panel.PrimaryActionContent.Resolve());
#pragma warning restore CS0618
        }
    }
}
