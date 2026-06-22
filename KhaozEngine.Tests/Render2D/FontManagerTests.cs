using System;
using System.IO;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless tests for the system-font-free font story: the embedded engine default (<see cref="DefaultFont"/>)
    /// and the key-based <see cref="FontManager"/>. None of these touch a GPU device - resolution yields raw TTF
    /// bytes, and the embedded default is validated through the device-free <see cref="SpriteFont.BakeCpu"/> path.
    /// </summary>
    public class FontManagerTests
    {
        static readonly string RobotoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        static int AtlasCoverage(byte[] ttf)
        {
            BakedFont baked = SpriteFont.BakeCpu(ttf, 32f, 1);
            int covered = 0;
            for (int i = 3; i < baked.Atlas.Length; i += 4)
                if (baked.Atlas[i] != 0) covered++;
            return covered;
        }

        [Fact]
        public void Default_font_bytes_are_nonempty_and_bake_with_glyph_coverage()
        {
            Assert.NotEmpty(DefaultFont.Bytes);
            Assert.True(AtlasCoverage(DefaultFont.Bytes) > 0, "embedded default font produced an empty atlas");
        }

        [Fact]
        public void Default_font_bytes_are_cached_as_the_same_instance()
        {
            Assert.Same(DefaultFont.Bytes, DefaultFont.Bytes);
        }

        [Fact]
        public void New_manager_preregisters_the_default_key_to_the_embedded_face()
        {
            var fonts = new FontManager();
            Assert.True(fonts.IsFontRegistered(FontManager.DefaultKey));
            Assert.Same(DefaultFont.Bytes, fonts.GetFontBytes(FontManager.DefaultKey));
        }

        [Fact]
        public void Register_by_bytes_then_resolve_returns_those_bytes()
        {
            var fonts = new FontManager();
            byte[] ttf = File.ReadAllBytes(RobotoPath);
            fonts.RegisterFont("title", ttf);

            Assert.True(fonts.IsFontRegistered("title"));
            Assert.Same(ttf, fonts.GetFontBytes("title"));
        }

        [Fact]
        public void Register_by_key_probes_the_content_directory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-fonts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] ttf = File.ReadAllBytes(RobotoPath);
                File.WriteAllBytes(Path.Combine(dir, "hud.ttf"), ttf);

                var fonts = new FontManager(dir);
                fonts.RegisterFont("hud"); // key == path under the dir, no extension

                Assert.True(fonts.IsFontRegistered("hud"));
                Assert.Equal(ttf, fonts.GetFontBytes("hud"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Register_by_key_resolves_a_nested_key_path()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-fonts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "ui"));
            try
            {
                byte[] ttf = File.ReadAllBytes(RobotoPath);
                File.WriteAllBytes(Path.Combine(dir, "ui", "title.ttf"), ttf);

                var fonts = new FontManager(dir);
                fonts.RegisterFont("ui/title");

                Assert.Equal(ttf, fonts.GetFontBytes("ui/title"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Register_by_key_throws_when_no_file_is_found()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-fonts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var fonts = new FontManager(dir);
                Assert.Throws<FileNotFoundException>(() => fonts.RegisterFont("missing"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Registering_a_key_overrides_a_previous_registration_including_the_default()
        {
            var fonts = new FontManager();
            byte[] custom = File.ReadAllBytes(RobotoPath);
            fonts.RegisterFont(FontManager.DefaultKey, custom);

            Assert.Same(custom, fonts.GetFontBytes(FontManager.DefaultKey));
        }

        [Fact]
        public void GetFontBytes_throws_for_an_unknown_key()
        {
            var fonts = new FontManager();
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => fonts.GetFontBytes("nope"));
        }

        [Fact]
        public void TryGetFontBytes_reports_presence()
        {
            var fonts = new FontManager();
            Assert.True(fonts.TryGetFontBytes(FontManager.DefaultKey, out byte[] def));
            Assert.NotEmpty(def);
            Assert.False(fonts.TryGetFontBytes("nope", out _));
        }

        [Fact]
        public void Content_directory_defaults_under_the_base_directory()
        {
            var fonts = new FontManager();
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "assets", "fonts"), fonts.ContentDirectory);
        }
    }
}
