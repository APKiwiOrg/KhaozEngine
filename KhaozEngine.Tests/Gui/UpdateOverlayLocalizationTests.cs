using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Tests.App;      // DictionaryCatalog
using KhaozEngine.Tests.Updates;  // FakeUpdateStatus
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The default <see cref="UpdateOverlayTheme"/> is localization-aware: it resolves through the ambient
    /// <see cref="LocalizationContext.Catalog"/> against the <see cref="UpdateOverlayStrings"/> keys and falls
    /// back to the built-in English when a catalog is absent or missing a key. These mutate the process-wide
    /// ambient catalog, so they share the serialized AmbientLocalization collection.
    /// </summary>
    [Collection("AmbientLocalization")]
    public sealed class UpdateOverlayLocalizationTests
    {
        static DictionaryCatalog FullCatalog() => new DictionaryCatalog()
            .Add("update.overlay.available.title", "MAJ dispo v{0}")
            .Add("update.overlay.available.body", "Appuyez sur [{0}]")
            .Add("update.overlay.downloading.title", "Telechargement")
            .Add("update.overlay.downloading.body", "DL {0}/{1}")
            .Add("update.overlay.applying.title", "Application")
            .Add("update.overlay.failed.body", "Reessayez [{0}]");

        [Fact]
        public void Wired_catalog_resolves_titles_and_bodies()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = FullCatalog();
                var t = UpdateOverlayTheme.Default;
                t.TriggerKeyLabel = "U";

                Assert.Equal("MAJ dispo v2.0", t.TitleFor(UpdateState.UpdateAvailable, "2.0"));
                Assert.Equal("Appuyez sur [U]", t.BodyFor(UpdateState.UpdateAvailable, new FakeUpdateStatus()));
                Assert.Equal("Application", t.TitleFor(UpdateState.Applying, null));
                Assert.Equal("Reessayez [U]", t.BodyFor(UpdateState.Failed, new FakeUpdateStatus()));

                var dl = new FakeUpdateStatus
                {
                    State = UpdateState.Downloading,
                    FilesDownloaded = 2,
                    TotalFilesToDownload = 5,
                };
                // The catalog template is used (integer-only args, so no culture-sensitive formatting).
                Assert.Equal("DL 2/5", t.BodyFor(UpdateState.Downloading, dl));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Wired_catalog_missing_the_keys_falls_back_to_english()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                // A catalog that has SOME other key but none of the overlay keys: a game that localizes its own
                // UI but has not added update.overlay.* must still see the historical English, not raw keys.
                LocalizationContext.Catalog = new DictionaryCatalog().Add("menu.play", "Play");
                var t = UpdateOverlayTheme.Default;
                t.TriggerKeyLabel = "X";

                Assert.Equal("Update Available - v1.2.3", t.TitleFor(UpdateState.UpdateAvailable, "1.2.3"));
                Assert.Equal("Press [X] to download", t.BodyFor(UpdateState.UpdateAvailable, new FakeUpdateStatus()));
                Assert.Equal("Update Failed", t.TitleFor(UpdateState.Failed, null));

                var dl = new FakeUpdateStatus
                {
                    State = UpdateState.Downloading,
                    FilesDownloaded = 2,
                    TotalFilesToDownload = 5,
                    BytesDownloaded = 3 * 1024 * 1024,
                    TotalDownloadBytes = 10 * 1024 * 1024,
                };
                // Fallback formats invariantly, exactly like the pre-localization overlay (culture-stable "3.0").
                Assert.Equal("Downloading 2/5 files (3.0/10.0 MB)", t.BodyFor(UpdateState.Downloading, dl));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Subclass_override_still_wins_over_catalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = FullCatalog(); // has the keys, yet the override must take priority
                var t = new OverridingTheme();

                Assert.Equal("OVR-title", t.TitleFor(UpdateState.UpdateAvailable, "9.9"));
                Assert.Equal("OVR-body", t.BodyFor(UpdateState.UpdateAvailable, new FakeUpdateStatus()));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void EnglishDefaults_carry_every_overlay_key_and_format_invariantly()
        {
            var en = UpdateOverlayStrings.EnglishDefaults;
            Assert.Equal("Update Available - v3.1", en.Format(UpdateOverlayStrings.AvailableTitle.Key, "3.1"));
            Assert.Equal("Downloading Update...", en.Get(UpdateOverlayStrings.DownloadingTitle.Key));
            Assert.Equal("Game will restart shortly", en.Get(UpdateOverlayStrings.ApplyingBody.Key));
            Assert.Equal("Downloading 2/5 files (3.0/10.0 MB)",
                en.Format(UpdateOverlayStrings.DownloadingBody.Key, 2, 5, 3.0, 10.0));
            Assert.True(en.TryGet(UpdateOverlayStrings.FailedTitle.Key, out string failed));
            Assert.Equal("Update Failed", failed);
            Assert.False(en.TryGet("update.overlay.nonexistent", out _));
        }

        sealed class OverridingTheme : UpdateOverlayTheme
        {
            public override string TitleFor(UpdateState state, string? remoteVersion) => "OVR-title";
            public override string BodyFor(UpdateState state, IUpdateStatus status) => "OVR-body";
        }
    }
}
