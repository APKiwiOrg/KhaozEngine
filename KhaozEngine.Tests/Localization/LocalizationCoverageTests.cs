using System;
using System.Resources;
using KhaozEngine.App;
using KhaozEngine.Localization.TestKit;
using Xunit;

namespace KhaozEngine.Tests.Localization
{
    /// <summary>
    /// Exercises <see cref="LocalizationCoverage"/> against embedded resx fixtures under
    /// <c>Localization/Fixtures</c>: a neutral file, a complete <c>fr</c> satellite, and a deliberately broken
    /// <c>de</c> satellite (missing keys + an extra placeholder). This is the engine's own guard for the test kit
    /// the games consume.
    /// </summary>
    public class LocalizationCoverageTests
    {
        private const string BaseName = "KhaozEngine.Tests.Localization.Fixtures.CoverageFixtureStrings";

        private static ResourceManager Rm() => new ResourceManager(BaseName, typeof(LocalizationCoverageTests).Assembly);

        // public const string keys (the style Hardpoint / SpaceGame use).
        private static class ConstKeys
        {
            public const string Play = "menu.play";
            public const string Quit = "menu.quit";
            public const string Score = "hud.score";
            public const string Wave = "hud.wave";
        }

        // public static readonly StringId keys (the typed-key style).
        private static class StringIdKeys
        {
            public static readonly StringId Play = new("menu.play");
            public static readonly StringId Quit = new("menu.quit");
            public static readonly StringId Score = new("hud.score");
            public static readonly StringId Wave = new("hud.wave");
        }

        private static class EmptyKeys { }

        [Fact]
        public void Keys_ExtractsConstStrings()
            => Assert.Equal(new[] { "menu.play", "menu.quit", "hud.score", "hud.wave" }, LocalizationCoverage.Keys(typeof(ConstKeys)));

        [Fact]
        public void Keys_ExtractsStringIds()
            => Assert.Equal(new[] { "menu.play", "menu.quit", "hud.score", "hud.wave" }, LocalizationCoverage.Keys(typeof(StringIdKeys)));

        [Fact]
        public void AssertComplete_NeutralOnly_Passes()
            => LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm()); // no satellites: only the neutral resx checked

        [Fact]
        public void AssertComplete_CompleteSatellite_Passes()
            => LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm(), "fr");

        [Fact]
        public void AssertComplete_StringIdKeys_Passes()
            => LocalizationCoverage.AssertComplete(typeof(StringIdKeys), Rm(), "fr");

        [Fact]
        public void AssertComplete_BrokenSatellite_ReportsMissingAndPlaceholderGaps()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm(), "de"));

            Assert.Contains("de is missing translations", ex.Message);
            Assert.Contains("menu.quit", ex.Message);
            Assert.Contains("hud.wave", ex.Message);
            Assert.Contains("placeholder mismatch", ex.Message);
            Assert.Contains("hud.score", ex.Message);
        }

        [Fact]
        public void AssertComplete_AbsentSatellite_ReportsNoResourceSet()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm(), "es"));

            Assert.Contains("no resource set", ex.Message);
        }

        [Fact]
        public void AssertComplete_NoKeys_Throws()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(EmptyKeys), Rm()));

            Assert.Contains("No localization keys", ex.Message);
        }

        [Fact]
        public void AssertComplete_NullResources_Throws()
            => Assert.Throws<ArgumentNullException>(() => LocalizationCoverage.AssertComplete(typeof(ConstKeys), null!));
    }
}
