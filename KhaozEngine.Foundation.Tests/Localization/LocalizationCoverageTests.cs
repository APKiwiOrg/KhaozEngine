using System;
using System.Collections.Generic;
using System.Linq;
using System.Resources;
using KhaozEngine.App;
using KhaozEngine.Localization.TestKit;
using Xunit;

namespace KhaozEngine.Tests.Localization
{
    /// <summary>
    /// Exercises <see cref="LocalizationCoverage"/> against embedded resx fixtures under
    /// <c>Localization/Fixtures</c>: a neutral file, a complete <c>fr</c> satellite, a deliberately broken
    /// <c>de</c> satellite (missing keys + an extra placeholder), and <c>CoverageEmptyStrings</c>, a neutral resx
    /// with no string entries. This is the engine's own guard for the test kit the games consume.
    /// </summary>
    public class LocalizationCoverageTests
    {
        private const string BaseName = "KhaozEngine.Tests.Localization.Fixtures.CoverageFixtureStrings";

        private static ResourceManager Rm() => new ResourceManager(BaseName, typeof(LocalizationCoverageTests).Assembly);

        private static ResourceManager NoSuchRm() => new ResourceManager("No.Such.BaseName", typeof(LocalizationCoverageTests).Assembly);

        private static void AssertMissing(
            LocalizationCoverageFinding finding, LocalizationCoverageRefusal reason, string? cultureName, params string[] keys)
        {
            Assert.Equal(reason, finding.Reason);
            Assert.Equal(cultureName, finding.CultureName);
            Assert.Equal(keys, finding.MissingKeys);
        }

        private static void AssertPlaceholderMismatch(
            LocalizationCoverageFinding finding, string cultureName, string key, int[] expected, int[] actual)
        {
            Assert.Equal(LocalizationCoverageRefusal.PlaceholderSetMismatch, finding.Reason);
            Assert.Equal(cultureName, finding.CultureName);
            Assert.Equal(key, finding.Key);
            Assert.Equal(expected, finding.ExpectedIndices);
            Assert.Equal(actual, finding.ActualIndices);
        }

        private static void AssertSatelliteUnavailable(LocalizationCoverageException ex, string cultureName)
        {
            Assert.Equal(LocalizationCoverageRefusal.SatelliteResourcesUnavailable, ex.Reason);
            Assert.Equal(BaseName, ex.BaseName);
            Assert.Equal(4, ex.KeyCount); // every key is left unresolved
            Assert.NotNull(ex.Findings);
            LocalizationCoverageFinding finding = Assert.Single(ex.Findings);
            Assert.Equal(LocalizationCoverageRefusal.SatelliteResourcesUnavailable, finding.Reason);
            Assert.Equal(cultureName, finding.CultureName);
        }

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
        public void RefusalCollections_AreOwnedReadOnlySnapshots()
        {
            string[] keys = ["menu.quit", "hud.wave"];
            int[] indices = [0, 1];
            var finding = new LocalizationCoverageFinding(LocalizationCoverageRefusal.PlaceholderSetMismatch)
            { MissingKeys = keys, ExpectedIndices = indices, ActualIndices = indices };
            var source = new List<LocalizationCoverageFinding> { finding };
            var error = new LocalizationCoverageException(LocalizationCoverageRefusal.PlaceholderSetMismatch, "refused")
            { Findings = source };
            keys[0] = "changed";
            indices[0] = 9;
            source.Clear();

            Assert.Same(finding, Assert.Single(error.Findings!));
            Assert.Equal(new[] { "menu.quit", "hud.wave" }, finding.MissingKeys);
            Assert.Equal(new[] { 0, 1 }, finding.ExpectedIndices);
            Assert.Equal(new[] { 0, 1 }, finding.ActualIndices);
            Assert.Throws<NotSupportedException>(() => ((IList<LocalizationCoverageFinding>)error.Findings!).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<string>)finding.MissingKeys!)[0] = "changed");
            Assert.Throws<NotSupportedException>(() => ((IList<int>)finding.ExpectedIndices!)[0] = 9);
            Assert.Throws<NotSupportedException>(() => ((IList<int>)finding.ActualIndices!)[0] = 9);
            Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)error.Findings!).SyncRoot);
            Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)finding.MissingKeys!).SyncRoot);
            Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)finding.ExpectedIndices!).SyncRoot);
            Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)finding.ActualIndices!).SyncRoot);
        }

        // W7-S1 regression in 45e770def. Reassignment must retain the earlier record equality contract.
        [Theory]
        [InlineData(nameof(LocalizationCoverageFinding.MissingKeys))]
        [InlineData(nameof(LocalizationCoverageFinding.ExpectedIndices))]
        [InlineData(nameof(LocalizationCoverageFinding.ActualIndices))]
        public void FindingCollectionReassignment_KeepsDistinctRecordIdentity(string property)
        {
            var original = new LocalizationCoverageFinding(LocalizationCoverageRefusal.PlaceholderSetMismatch)
            { MissingKeys = ["menu.quit"], ExpectedIndices = [0], ActualIndices = [1] };
            LocalizationCoverageFinding reassigned = property switch
            {
                nameof(LocalizationCoverageFinding.MissingKeys) => original with { MissingKeys = original.MissingKeys },
                nameof(LocalizationCoverageFinding.ExpectedIndices) => original with { ExpectedIndices = original.ExpectedIndices },
                nameof(LocalizationCoverageFinding.ActualIndices) => original with { ActualIndices = original.ActualIndices },
                _ => throw new ArgumentOutOfRangeException(nameof(property)),
            };

            Assert.Equal(original, original with { });
            Assert.NotEqual(original, reassigned);
            Assert.Equal(2, new HashSet<LocalizationCoverageFinding> { original, reassigned }.Count);
        }

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

            Assert.Equal(LocalizationCoverageRefusal.TranslationsMissing, ex.Reason);
            Assert.Equal(typeof(ConstKeys), ex.KeysType);
            Assert.Equal(BaseName, ex.BaseName);
            Assert.Equal(4, ex.KeyCount);
            Assert.NotNull(ex.Findings);
            Assert.Equal(2, ex.Findings.Count);
            AssertMissing(ex.Findings[0], LocalizationCoverageRefusal.TranslationsMissing, "de", "menu.quit", "hud.wave");
            AssertPlaceholderMismatch(ex.Findings[1], "de", "hud.score", new[] { 0 }, new[] { 0, 1 });
        }

        [Fact]
        public void AssertComplete_AbsentSatellite_ReportsNoResourceSet()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm(), "es"));

            AssertSatelliteUnavailable(ex, "es");
            Assert.Equal(typeof(ConstKeys), ex.KeysType);
        }

        [Fact]
        public void AssertComplete_NoKeys_Throws()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(EmptyKeys), Rm()));

            Assert.Equal(LocalizationCoverageRefusal.KeyUniverseEmpty, ex.Reason);
            Assert.Equal(typeof(EmptyKeys), ex.KeysType);
            Assert.Null(ex.Findings);
        }

        [Fact]
        public void AssertComplete_InvalidCultureName_ReportsIt()
        {
            // A space is outside the characters a culture name may hold, so every platform refuses it before any
            // culture data is read.
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm(), "not a culture"));

            Assert.Equal(LocalizationCoverageRefusal.CultureNameInvalid, ex.Reason);
            Assert.NotNull(ex.Findings);
            LocalizationCoverageFinding finding = Assert.Single(ex.Findings);
            Assert.Equal(LocalizationCoverageRefusal.CultureNameInvalid, finding.Reason);
            Assert.Equal("not a culture", finding.CultureName);

            LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm(), "fr"); // control: a valid shipped culture
        }

        [Fact]
        public void AssertComplete_UnloadableNeutral_ReportsNeutralResourcesUnavailable()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(typeof(ConstKeys), NoSuchRm()));

            Assert.Equal(LocalizationCoverageRefusal.NeutralResourcesUnavailable, ex.Reason);
            Assert.Equal("No.Such.BaseName", ex.BaseName);
            Assert.NotNull(ex.Findings);
            Assert.Equal(LocalizationCoverageRefusal.NeutralResourcesUnavailable, Assert.Single(ex.Findings).Reason);

            LocalizationCoverage.AssertComplete(typeof(ConstKeys), Rm()); // control: a loadable neutral set
        }

        [Fact]
        public void AssertComplete_NullResources_Throws()
            => Assert.Throws<ArgumentNullException>(() => LocalizationCoverage.AssertComplete(typeof(ConstKeys), null!));

        // --- ResourceManager-driven key universe (no keys class; keys live in the neutral resx) ---

        [Fact]
        public void NeutralKeys_EnumeratesNeutralResxStringEntries_Sorted()
            => Assert.Equal(new[] { "hud.score", "hud.wave", "menu.play", "menu.quit" }, LocalizationCoverage.NeutralKeys(Rm()));

        [Fact]
        public void NeutralKeys_NullResources_Throws()
            => Assert.Throws<ArgumentNullException>(() => LocalizationCoverage.NeutralKeys(null!));

        [Fact]
        public void NeutralKeys_UnloadableNeutral_Throws()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(() => LocalizationCoverage.NeutralKeys(NoSuchRm()));

            Assert.Equal(LocalizationCoverageRefusal.NeutralResourcesUnavailable, ex.Reason);
            Assert.Equal("No.Such.BaseName", ex.BaseName);
            Assert.Null(ex.Findings);

            // The resx overload reads its universe through NeutralKeys first, so it refuses the same way.
            ex = Assert.Throws<LocalizationCoverageException>(() => LocalizationCoverage.AssertComplete(NoSuchRm()));

            Assert.Equal(LocalizationCoverageRefusal.NeutralResourcesUnavailable, ex.Reason);
            Assert.Equal("No.Such.BaseName", ex.BaseName);
            Assert.Null(ex.Findings);
        }

        [Fact]
        public void AssertComplete_ResourceManager_NeutralOnly_Passes()
            => LocalizationCoverage.AssertComplete(Rm()); // no satellites: nothing beyond the neutral universe itself

        [Fact]
        public void AssertComplete_ResourceManager_CompleteSatellite_Passes()
            => LocalizationCoverage.AssertComplete(Rm(), "fr");

        [Fact]
        public void AssertComplete_ResourceManager_BrokenSatellite_ReportsMissingAndPlaceholderGaps()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(Rm(), "de"));

            Assert.Equal(LocalizationCoverageRefusal.TranslationsMissing, ex.Reason);
            Assert.Equal(BaseName, ex.BaseName);
            Assert.Null(ex.KeysType);
            Assert.Equal(4, ex.KeyCount);
            Assert.NotNull(ex.Findings);
            Assert.Equal(2, ex.Findings.Count);
            // The resx universe is ordinally sorted, so the missing keys follow that order.
            AssertMissing(ex.Findings[0], LocalizationCoverageRefusal.TranslationsMissing, "de", "hud.wave", "menu.quit");
            AssertPlaceholderMismatch(ex.Findings[1], "de", "hud.score", new[] { 0 }, new[] { 0, 1 });
        }

        [Fact]
        public void AssertComplete_ResourceManager_AbsentSatellite_ReportsNoResourceSet()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(Rm(), "es"));

            AssertSatelliteUnavailable(ex, "es");
            Assert.Null(ex.KeysType);
        }

        [Fact]
        public void AssertComplete_ResourceManager_NeutralWithNoEntries_ReportsKeyUniverseEmpty()
        {
            const string EmptyBaseName = "KhaozEngine.Tests.Localization.Fixtures.CoverageEmptyStrings";
            var empty = new ResourceManager(EmptyBaseName, typeof(LocalizationCoverageTests).Assembly);

            var ex = Assert.Throws<LocalizationCoverageException>(() => LocalizationCoverage.AssertComplete(empty));

            Assert.Equal(LocalizationCoverageRefusal.KeyUniverseEmpty, ex.Reason);
            Assert.Equal(EmptyBaseName, ex.BaseName);
            Assert.Null(ex.Findings);

            LocalizationCoverage.AssertComplete(Rm()); // control: a neutral resx with string entries
        }

        [Fact]
        public void AssertComplete_ResourceManager_NullResources_Throws()
            => Assert.Throws<ArgumentNullException>(() => LocalizationCoverage.AssertComplete((ResourceManager)null!, "fr"));

        // --- Explicit key-sequence universe (e.g. a filtered subset of NeutralKeys) ---

        [Fact]
        public void AssertComplete_KeySequence_FilteredSubset_PassesAgainstBrokenSatellite()
        {
            // de is broken overall, but the "menu.play" slice of the universe is fully translated there.
            IEnumerable<string> subset = LocalizationCoverage.NeutralKeys(Rm()).Where(k => k == "menu.play");
            LocalizationCoverage.AssertComplete(subset, Rm(), "de");
        }

        [Fact]
        public void AssertComplete_KeySequence_ReportsKeysMissingFromNeutral()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(new[] { "menu.play", "not.a.key" }, Rm()));

            Assert.Equal(LocalizationCoverageRefusal.NeutralKeysMissing, ex.Reason);
            Assert.Null(ex.KeysType);
            Assert.Equal(2, ex.KeyCount);
            Assert.NotNull(ex.Findings);
            AssertMissing(Assert.Single(ex.Findings), LocalizationCoverageRefusal.NeutralKeysMissing, null, "not.a.key");

            LocalizationCoverage.AssertComplete(new[] { "menu.play" }, Rm()); // control: every key is in the neutral resx
        }

        [Fact]
        public void AssertComplete_KeySequence_NeutralAndTwoSatelliteGaps_ReportsNeutralFirstThenCulturesAsPassed()
        {
            // The README promises the neutral finding first, then each satellite in the order passed. es (no resource
            // set) is passed before de, against their ordinal order, so a sorted sweep would fail.
            string[] keys = { "menu.play", "menu.quit", "not.a.key" };
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(keys, Rm(), "es", "de"));

            Assert.Equal(LocalizationCoverageRefusal.NeutralKeysMissing, ex.Reason);
            Assert.NotNull(ex.Findings);
            Assert.Equal(3, ex.Findings.Count);
            AssertMissing(ex.Findings[0], LocalizationCoverageRefusal.NeutralKeysMissing, null, "not.a.key");
            Assert.Equal(LocalizationCoverageRefusal.SatelliteResourcesUnavailable, ex.Findings[1].Reason);
            Assert.Equal("es", ex.Findings[1].CultureName);
            AssertMissing(ex.Findings[2], LocalizationCoverageRefusal.TranslationsMissing, "de", "menu.quit", "not.a.key");

            // The same gaps passed the other way round follow that order.
            ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(keys, Rm(), "de", "es"));

            Assert.Equal(LocalizationCoverageRefusal.NeutralKeysMissing, ex.Reason);
            Assert.NotNull(ex.Findings);
            Assert.Equal(
                new string?[] { null, "de", "es" },
                ex.Findings.Select(finding => finding.CultureName).ToArray());
        }

        [Theory]
        [InlineData("menu.quit")]
        [InlineData("hud.wave")]
        public void AssertComplete_KeySequence_UntranslatedKey_ReportsTranslationsMissing(string key)
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(new[] { "menu.play", key }, Rm(), "de"));

            Assert.Equal(LocalizationCoverageRefusal.TranslationsMissing, ex.Reason);
            Assert.NotNull(ex.Findings);
            AssertMissing(Assert.Single(ex.Findings), LocalizationCoverageRefusal.TranslationsMissing, "de", key);

            LocalizationCoverage.AssertComplete(new[] { "menu.play", key }, Rm(), "fr"); // control: fr translates it
        }

        [Fact]
        public void AssertComplete_KeySequence_PlaceholderDrift_ReportsPlaceholderSetMismatch()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(new[] { "menu.play", "hud.score" }, Rm(), "de"));

            Assert.Equal(LocalizationCoverageRefusal.PlaceholderSetMismatch, ex.Reason);
            Assert.NotNull(ex.Findings);
            AssertPlaceholderMismatch(Assert.Single(ex.Findings), "de", "hud.score", new[] { 0 }, new[] { 0, 1 });

            // control: fr carries the neutral template's one placeholder
            LocalizationCoverage.AssertComplete(new[] { "menu.play", "hud.score" }, Rm(), "fr");
        }

        [Fact]
        public void AssertComplete_KeySequence_Empty_Throws()
        {
            var ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(new[] { "", "" }, Rm(), "fr"));

            Assert.Equal(LocalizationCoverageRefusal.KeyUniverseEmpty, ex.Reason);
            Assert.Null(ex.KeysType);
            Assert.Null(ex.Findings);

            ex = Assert.Throws<LocalizationCoverageException>(
                () => LocalizationCoverage.AssertComplete(Array.Empty<string>(), Rm(), "fr"));
            Assert.Equal(LocalizationCoverageRefusal.KeyUniverseEmpty, ex.Reason);

            LocalizationCoverage.AssertComplete(new[] { "", "menu.play" }, Rm(), "fr"); // control: one key remains
        }

        [Fact]
        public void AssertComplete_KeySequence_NullKeys_Throws()
            => Assert.Throws<ArgumentNullException>(
                () => LocalizationCoverage.AssertComplete((IEnumerable<string>)null!, Rm(), "fr"));
    }
}
