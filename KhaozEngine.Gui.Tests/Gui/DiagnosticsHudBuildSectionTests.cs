using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The built-in Build section: every game's F1 panel names the running app and its version with no wiring and
    /// no debug switch, a game can put its own display strings there, it sits before the game's sections, and the
    /// identity is read once rather than on every refresh. The title resolves through the ambient catalog, so a
    /// test that wires one lives in <see cref="DiagnosticsOverlayStringsTests"/> under the serialized collection.
    /// </summary>
    public sealed class DiagnosticsHudBuildSectionTests
    {
        static InputState KeyFrame(params Key[] pressed) => new(
            new HashSet<Key>(pressed), new HashSet<Key>(pressed), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 1280, 720);

        static DiagnosticsOverlayTheme InstantTheme() =>
            new() { FadeSpeed = 0f, ToggleKey = Key.F1 };

        static OverlaySection Build(DiagnosticsHud hud) => hud.Overlay.Sections.Single(s => s.Title == "Build");

        static string[] Titles(DiagnosticsHud hud) => hud.Overlay.Sections.Select(s => s.Title).ToArray();

        [Fact]
        public void Build_section_is_built_in_with_one_row_and_needs_no_setup()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);

            hud.Update(KeyFrame(Key.F1), 0.016f);

            OverlaySection build = Build(hud);
            Assert.Single(build.Rows);
            Assert.False(string.IsNullOrWhiteSpace(build.Rows[0].Label));
            Assert.False(string.IsNullOrWhiteSpace(build.Rows[0].Value));
        }

        [Fact]
        public void Default_identity_is_the_entry_assembly_product_and_informational_version()
        {
            Assembly? entry = Assembly.GetEntryAssembly();
            Assert.NotNull(entry);
            string product = entry!.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
            string informational = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            int plus = informational.IndexOf('+');
            string expectedVersion = plus >= 0 ? informational.Substring(0, plus) : informational;

            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.Update(KeyFrame(Key.F1), 0.016f);

            OverlayRow row = Build(hud).Rows[0];
            Assert.Equal(product, row.Label);
            Assert.Equal(expectedVersion, row.Value);
        }

        [Fact]
        public void FromAssembly_reads_the_product_and_the_informational_version()
        {
            Assembly tests = typeof(DiagnosticsHudBuildSectionTests).Assembly;
            string informational = tests.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            int plus = informational.IndexOf('+');
            string expectedVersion = plus >= 0 ? informational.Substring(0, plus) : informational;

            BuildIdentity identity = DiagnosticsBuildSection.FromAssembly(tests);

            Assert.Equal(tests.GetCustomAttribute<AssemblyProductAttribute>()!.Product, identity.Name);
            Assert.Equal(expectedVersion, identity.Version);
        }

        [Fact]
        public void FromAssembly_without_an_assembly_shows_a_placeholder_rather_than_throwing()
        {
            BuildIdentity identity = DiagnosticsBuildSection.FromAssembly(null);

            Assert.Equal("?", identity.Name);
            Assert.Equal("?", identity.Version);
        }

        [Theory]
        [InlineData("1.4.0+3f2a9c1d", "1.4.0")]
        [InlineData("Codex (0.10.2)+3f2a9c1d", "Codex (0.10.2)")]
        [InlineData("20.0.0", "20.0.0")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void StripBuildMetadata_drops_the_sourcelink_suffix_only(string? stamped, string expected)
        {
            Assert.Equal(expected, DiagnosticsBuildSection.StripBuildMetadata(stamped));
        }

        [Fact]
        public void Override_wins_and_the_default_is_never_read()
        {
            int reads = 0;
            var hud = new DiagnosticsHud(InstantTheme(), false, 0f, false,
                () => { reads++; return new BuildIdentity("Default", "0.0.0"); });

            hud.SetBuildIdentity("Grimhollow", "Codex (0.10.2)");
            hud.Update(KeyFrame(Key.F1), 0.016f);

            Assert.Equal(new OverlayRow("Grimhollow", "Codex (0.10.2)"), Build(hud).Rows[0]);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void Override_after_the_default_was_shown_replaces_it()
        {
            var hud = new DiagnosticsHud(InstantTheme(), false, 0f, false, () => new BuildIdentity("Default", "0.0.0"));
            hud.Update(KeyFrame(Key.F1), 0.016f);
            Assert.Equal(new OverlayRow("Default", "0.0.0"), Build(hud).Rows[0]);

            hud.SetBuildIdentity("Grimhollow", "Codex (0.10.2)");
            hud.Update(KeyFrame(), 0.016f);

            Assert.Equal(new OverlayRow("Grimhollow", "Codex (0.10.2)"), Build(hud).Rows[0]);
        }

        [Fact]
        public void SetBuildIdentity_rejects_null()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false);

            Assert.Throws<ArgumentNullException>(() => hud.SetBuildIdentity(null!, "1.0.0"));
            Assert.Throws<ArgumentNullException>(() => hud.SetBuildIdentity("Game", null!));
        }

        [Fact]
        public void Build_section_follows_the_built_ins_and_precedes_game_sections()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: true, refreshSeconds: 0f);
            hud.PassTimings!.Sample("model", 1.5f);
            hud.SetNetStatsSource(() => new ClientNetStats { Connected = true, RttMs = 20f });
            hud.AddSection(() => new OverlaySection("World", new[] { new OverlayRow("tile", "3200, 3200") }));

            hud.Update(KeyFrame(Key.F1), 0.016f);

            Assert.Equal(new[] { "Performance", "Draw stats", "Pass timings", "Network", "Build", "World" }, Titles(hud));
        }

        [Fact]
        public void Identity_source_is_read_once_across_many_refreshes()
        {
            int reads = 0;
            var hud = new DiagnosticsHud(InstantTheme(), false, 0f, false,
                () => { reads++; return new BuildIdentity("Game", "1.0.0"); });

            hud.Update(KeyFrame(Key.F1), 0.016f);
            for (int i = 0; i < 120; i++) hud.Update(KeyFrame(), 0.016f);   // refreshSeconds 0: every Update polls

            Assert.Equal(1, reads);
            Assert.Equal(new OverlayRow("Game", "1.0.0"), Build(hud).Rows[0]);
        }

        [Fact]
        public void Identity_source_is_not_read_while_the_panel_stays_hidden()
        {
            int reads = 0;
            var hud = new DiagnosticsHud(InstantTheme(), false, 0f, false,
                () => { reads++; return new BuildIdentity("Game", "1.0.0"); });

            for (int i = 0; i < 10; i++) hud.Update(KeyFrame(), 0.016f);

            Assert.Equal(0, reads);
        }

        [Fact]
        public void Build_section_instance_is_reused_while_its_title_holds()
        {
            var hud = new DiagnosticsHud(InstantTheme(), false, 0f, false, () => new BuildIdentity("Game", "1.0.0"));

            hud.Update(KeyFrame(Key.F1), 0.016f);
            OverlaySection first = Build(hud);
            hud.Update(KeyFrame(), 0.016f);

            Assert.Same(first, Build(hud));
        }
    }
}
