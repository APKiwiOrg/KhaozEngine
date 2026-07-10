using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// Headless coverage for <see cref="PatchNotesScreen"/>. A real <see cref="KhaozEngine.Render2D.SpriteFont"/>
/// needs an <c>IGpuDevice</c> to bake its atlas, so it cannot be constructed in a default headless test run
/// (see <see cref="OverlayLegendTests"/> for the same constraint elsewhere in this project). Unlike
/// <see cref="UpdateOverlayView"/> (whose <c>Update</c> never touches a font), <see cref="PatchNotesView.Update"/>
/// always reads its <c>ITextMeasurer</c> argument, even for an empty document (to report the empty-state line
/// height), so a null font cannot flow through <see cref="PatchNotesScreen.Update"/> once <c>receivesInput</c> is
/// true. That path (view interaction, scroll clamp, <see cref="PatchNotesView.CloseRequested"/> latching the
/// screen's exit) is left to GPU golden coverage. This file sticks to the font-free surface: construction (the
/// screen is modal by construction, unlike the sometimes-passthrough update overlay) and the
/// <c>receivesInput: false</c> guard, which returns before ever touching the view or the font. The generic
/// "a modal screen blocks the one below it" mechanics are already covered stack-side by
/// <see cref="ScreenStackTests"/>; this file only proves this screen's own construction wiring.
/// </summary>
public sealed class PatchNotesScreenTests
{
    static PatchNotesScreen NewScreen(PatchNotesDocument? document = null, PatchNotesTheme? theme = null) =>
        new(document ?? PatchNotesDocument.Empty, null!, null!, new DesignViewport(960, 540), theme);

    [Fact]
    public void Construction_is_modal_with_SettingsScreen_style_transitions()
    {
        PatchNotesScreen screen = NewScreen();

        Assert.False(screen.PassUpdateThrough);   // always modal (unlike UpdateOverlayScreen, which toggles)
        Assert.Equal(0.18f, screen.TransitionOnDuration);
        Assert.Equal(0.18f, screen.TransitionOffDuration);
        Assert.NotNull(screen.View);
        Assert.False(screen.View.CloseRequested);
    }

    [Fact]
    public void Construction_wraps_the_document_so_the_newest_build_starts_expanded()
    {
        PatchNotesDocument doc = PatchNotesParser.Parse(
            "# Sample - Player Changelog\n\n" +
            "### Build 0.2.0 (Two)\n\n- **Minor**\n  - Second note.\n\n" +
            "### Build 0.1.0 (One)\n\n- **New**\n  - First note.\n");

        PatchNotesScreen screen = NewScreen(doc);

        Assert.True(screen.View.IsExpanded(0));   // newest build starts expanded
        Assert.False(screen.View.IsExpanded(1));  // older build starts collapsed
    }

    [Fact]
    public void Construction_honors_a_supplied_theme_instance()
    {
        var theme = PatchNotesTheme.Default;
        PatchNotesScreen screen = NewScreen(theme: theme);

        Assert.Same(theme, screen.View.Theme);
    }

    [Fact]
    public void Update_without_receiving_input_is_a_safe_no_op_and_stays_modal()
    {
        PatchNotesScreen screen = NewScreen();

        bool consumed = screen.Update(0.016f, receivesInput: false);

        Assert.True(consumed);                     // matches SettingsScreen's "always true" shape
        Assert.False(screen.PassUpdateThrough);     // still modal, unaffected by the frame
        Assert.False(screen.View.CloseRequested);   // the view was never touched
    }
}
