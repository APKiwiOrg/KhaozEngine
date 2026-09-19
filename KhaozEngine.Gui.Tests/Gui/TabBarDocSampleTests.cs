using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The side-panel strip sample printed in <c>KhaozEngine.Gui/README.md</c> and
    /// <c>docs/USING-KHAOZENGINE.md</c>, as code. A sample only has to name one member of the wrong type to
    /// send a consumer looking for a property that was never there, and prose is not compiled. This is: the
    /// method below is never called (it needs a GPU-backed batch), it just has to build.
    /// </summary>
    public static class TabBarDocSample
    {
        /// <summary>The documented sample, verbatim but for the host's own call.</summary>
        /// <param name="tabs">The tab roster.</param>
        /// <param name="font">The font the labels draw with.</param>
        /// <param name="bounds">The window's outer bounds.</param>
        /// <param name="pointer">The frame's pointer.</param>
        /// <param name="batch">An in-progress sprite batch.</param>
        /// <param name="white">A one by one white texture.</param>
        /// <returns>The tab under the pointer, or -1.</returns>
        public static int Sample(IReadOnlyList<TabBarItem> tabs, SpriteFont font, Rect bounds, Pointer pointer,
            SpriteBatch batch, Texture2D white)
        {
            // A collapsed side panel's strip: two centred rows of five, flat, with nothing active.
            var strip = new TabBar(tabs, font, PanelFrame.StripRect(bounds, stripHeight: 56f))
            {
                TabWidth = 56f, TabHeight = 26f, Columns = 5, Spacing = 4f,
                BlockAlign = GuiAlign.Center,
                DrawMode = TabBarDrawMode.Flat,
                AllowNoActiveTab = true,              // before ActiveIndex: the setter clamps against it
                ActiveIndex = TabBar.NoActiveTab,
                BlocksPointer = false,                // the window already reserves its own bounds
            };

            // Per frame, after laying the window out:
            strip.Bounds = PanelFrame.StripRect(bounds, stripHeight: 56f);
            strip.Update(pointer);
            if (strip.ChangedThisFrame) OpenPanel(strip.ActiveIndex);
            strip.Draw(batch, white);

            // The same rects with no widget in hand:
            return TabStrip.TabAt(strip.Bounds, pointer.Position, tabs.Count, strip.Metrics);
        }

        static void OpenPanel(int index) => _ = index;
    }

    /// <summary>
    /// What a <see cref="TabBar"/> built the old way still does, pinned against the additions: start-anchored,
    /// clamped to a real active tab, blocking the pointer, drawing buttons.
    /// </summary>
    public class TabBarDocSampleTests
    {
        [Fact]
        public void The_sample_is_compiled_rather_than_only_printed()
        {
            // Building the sample is the assertion. Naming it here keeps it wired to a test run.
            Assert.NotNull(typeof(TabBarDocSample).GetMethod(nameof(TabBarDocSample.Sample)));
        }

        [Fact]
        public void The_shipped_defaults_are_what_a_bar_that_asks_for_nothing_gets()
        {
            var bar = new TabBar(new[] { LocalizedText.Raw("A"), LocalizedText.Raw("B") }, font: null,
                new Rect(10f, 20f, 200f, 30f))
            {
                TabWidth = 40f, TabHeight = 20f,
            };

            Assert.Equal(GuiAlign.Left, bar.BlockAlign);
            Assert.Equal(10f, bar.TabRect(0).X, 3);
            Assert.Equal(10f, bar.ContentBounds.X, 3);
            Assert.False(bar.AllowNoActiveTab);
            Assert.Equal(0, bar.ActiveIndex);
            Assert.True(bar.BlocksPointer);
            Assert.Equal(TabBarDrawMode.Button, bar.DrawMode);
        }
    }
}
