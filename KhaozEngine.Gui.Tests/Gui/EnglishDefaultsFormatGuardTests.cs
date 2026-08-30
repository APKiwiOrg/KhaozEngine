using KhaozEngine.App;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The Gui-owned English default catalogs all format the same way, and all of them used to be a bare
    /// string.Format that threw on a template its arguments could not satisfy, straight out of the draw call
    /// resolving the text (#163). They route through <see cref="IStringCatalog.SafeFormat"/> now, so a template
    /// that cannot be applied comes back unformatted instead of ending the frame loop.
    /// </summary>
    public class EnglishDefaultsFormatGuardTests
    {
        [Fact]
        public void ReconnectStrings_attempt_line_with_no_args_falls_back_to_the_template()
        {
            Assert.Equal("Attempt {0}", ReconnectStrings.EnglishDefaults.Format("reconnect.attempt"));
        }

        [Fact]
        public void ReconnectStrings_still_formats_when_the_args_fit()
        {
            Assert.Equal("Attempt 3", ReconnectStrings.EnglishDefaults.Format("reconnect.attempt", 3));
        }

        [Fact]
        public void UpdateOverlayStrings_downloading_body_with_too_few_args_falls_back_to_the_template()
        {
            Assert.Equal("Downloading {0}/{1} files ({2:0.0}/{3:0.0} MB)",
                UpdateOverlayStrings.EnglishDefaults.Format("update.overlay.downloading.body", 1));
        }

        [Fact]
        public void UpdateOverlayStrings_still_formats_when_the_args_fit()
        {
            Assert.Equal("Downloading 1/4 files (0.5/2.0 MB)",
                UpdateOverlayStrings.EnglishDefaults.Format("update.overlay.downloading.body", 1, 4, 0.5f, 2f));
        }

        /// <summary>
        /// Every patch-notes default is placeholder-free, so the only malformed template this catalog can hand
        /// its formatter is an absent key, which Get returns verbatim by contract. A key carrying a brace is
        /// therefore the reachable case, and it must not throw either.
        /// </summary>
        [Fact]
        public void PatchNotesStrings_absent_key_carrying_a_placeholder_falls_back_to_the_key()
        {
            Assert.Equal("patchnotes.{0}.missing",
                PatchNotesStrings.EnglishDefaults.Format("patchnotes.{0}.missing"));
        }

        [Fact]
        public void PatchNotesStrings_present_key_still_resolves()
        {
            Assert.Equal("Patch Notes", PatchNotesStrings.EnglishDefaults.Format("patchnotes.title"));
        }
    }
}
