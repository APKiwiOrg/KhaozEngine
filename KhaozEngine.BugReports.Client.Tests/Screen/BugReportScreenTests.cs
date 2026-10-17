using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using GuiScreen = KhaozEngine.Gui.Screen;

namespace KhaozEngine.Tests.BugReports.Client.Screen;

/// <summary>
/// Drives <see cref="BugReportScreen"/> through a real <see cref="ScreenStack"/> over a 1280 by 720 design
/// viewport, with a fixed-width measurer and no font or texture, so every frame is headless.
/// </summary>
[Collection("AmbientLocalization")]
public sealed class BugReportScreenTests
{
    const float Dt = 1f / 60f;

    readonly MouseFrames _mouse = new();
    readonly DesignViewport _viewport = new(1280, 720);
    readonly ScreenStack _stack = new();
    readonly List<BugReportScreenResult> _closed = new();

    BugReportScreen Open(bool signedIn = true, bool clipAvailable = true, IReadOnlyList<string>? categories = null)
    {
        var model = new BugReportScreenModel(categories ?? BugReportDraft.DefaultCategories, signedIn,
            screenshotAvailable: true, clipAvailable: clipAvailable);
        var screen = new BugReportScreen(model, new BugReportScreenResources(new FixedMeasurer(), null, null, _viewport));
        screen.Closed += _closed.Add;
        _stack.Add(screen);
        return screen;
    }

    InputState Frame(Vector2 pointer = default, bool leftDown = false, Key[]? held = null, Key[]? pressed = null,
        Key[]? repeated = null, string? text = null)
    {
        var down = new HashSet<Key>(held ?? []);
        var edges = new HashSet<Key>(pressed ?? []);
        down.UnionWith(edges);
        var (mousePressed, mouseReleased) = _mouse.Advance(MouseButton.Left, leftDown);
        var mouseDown = new HashSet<MouseButton>();
        if (leftDown) mouseDown.Add(MouseButton.Left);
        return new InputState(down, edges, new HashSet<Key>(), mouseDown, mousePressed, pointer, Vector2.Zero, 0f,
            1280, 720, repeated: repeated is null ? null : new HashSet<Key>(repeated), mouseReleased: mouseReleased,
            textInput: text ?? "", textInputAvailable: text is not null);
    }

    void Step(InputState frame) => _stack.Update(Dt, frame, _viewport);

    void Tap(Rect bounds)
    {
        var centre = new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
        Step(Frame(centre));
        Step(Frame(centre, leftDown: true));
        Step(Frame(centre));
    }

    void Type(string text) => Step(Frame(text: text));

    [Fact]
    public void Opens_modal_with_the_description_focused()
    {
        var below = new CountingScreen();
        _stack.Add(below);
        BugReportScreen screen = Open();

        Step(Frame());

        Assert.False(screen.PassUpdateThrough);
        Assert.Equal(20000, screen.DrawOrder);
        Assert.Same(screen, _stack.Screens[^1]);
        Assert.True(screen.DescriptionArea.IsFocused);
        Assert.Equal(BugReportScreenResult.Open, screen.Result);
        Assert.Equal(0, below.Updates);
        Assert.Empty(_closed);
    }

    [Fact]
    public void First_update_after_open_ignores_a_still_held_trigger_key()
    {
        BugReportScreen screen = Open();

        Step(Frame(held: [Key.F10], repeated: [Key.F10]));

        Assert.Equal(BugReportScreenResult.Open, screen.Result);
        Assert.Equal("", screen.Model.Description);
        Assert.Equal("", screen.DescriptionArea.Text);
        Assert.False(screen.CategoryDropdown.IsOpen);
        Assert.Empty(_closed);
    }

    [Fact]
    public void Typing_fills_the_description()
    {
        BugReportScreen screen = Open();

        Type("Fell through");

        Assert.Equal("Fell through", screen.Model.Description);
        Assert.True(screen.Model.CanSend);
    }

    [Fact]
    public void Send_is_ignored_until_there_is_text()
    {
        BugReportScreen screen = Open();
        Type("   ");

        Step(Frame());
        Assert.False(screen.SendButton.Enabled);
        Tap(screen.Layout.Send);
        Step(Frame(held: [Key.LeftControl], pressed: [Key.Enter]));

        Assert.Equal(BugReportScreenResult.Open, screen.Result);
        Assert.Empty(_closed);

        // The tap on Send released outside the description and unfocused it.
        Tap(screen.Layout.Description);
        Type("Fell through");
        Step(Frame());
        Assert.True(screen.SendButton.Enabled);
        Tap(screen.Layout.Send);
        Assert.Equal(BugReportScreenResult.Send, screen.Result);
        Assert.Equal([BugReportScreenResult.Send], _closed);
    }

    [Fact]
    public void Ctrl_enter_sends_when_text_is_present()
    {
        BugReportScreen screen = Open();
        Type("Fell through");

        Step(Frame(held: [Key.LeftControl], pressed: [Key.Enter]));
        Step(Frame(held: [Key.LeftSuper], pressed: [Key.Enter]));

        Assert.Equal(BugReportScreenResult.Send, screen.Result);
        Assert.Equal([BugReportScreenResult.Send], _closed);
        Assert.Equal("Fell through", screen.Model.Description);
    }

    [Fact]
    public void Escape_cancels()
    {
        BugReportScreen screen = Open();
        Tap(screen.Layout.Category);
        Assert.True(screen.CategoryDropdown.IsOpen);

        Step(Frame(pressed: [Key.Escape]));
        Assert.False(screen.CategoryDropdown.IsOpen);
        Assert.Equal(BugReportScreenResult.Open, screen.Result);

        Step(Frame(pressed: [Key.Escape]));
        Step(Frame(pressed: [Key.Escape]));
        Assert.Equal(BugReportScreenResult.Cancel, screen.Result);
        Assert.Equal([BugReportScreenResult.Cancel], _closed);
    }

    [Fact]
    public void Toggles_flip_screenshot_and_clip()
    {
        BugReportScreen screen = Open();
        Assert.True(screen.Model.IncludeScreenshot);
        Assert.True(screen.Model.IncludeClip);

        Tap(screen.Layout.ScreenshotToggle);
        Tap(screen.Layout.ClipToggle);
        Assert.False(screen.Model.IncludeScreenshot);
        Assert.False(screen.Model.IncludeClip);

        Tap(screen.Layout.ClipToggle);
        Assert.True(screen.Model.IncludeClip);
        Assert.False(screen.Model.IncludeScreenshot);
    }

    [Fact]
    public void Clip_toggle_is_inert_when_clip_is_unavailable()
    {
        BugReportScreen screen = Open(clipAvailable: false);

        Tap(screen.Layout.ClipToggle);

        Assert.False(screen.ClipToggle.Enabled);
        Assert.False(screen.ClipToggle.IsOn);
        Assert.False(screen.Model.IncludeClip);
    }

    [Fact]
    public void Signed_out_variant_adds_the_pending_line()
    {
        using var _ = LocalizationContext.Use(null);
        const string Included = "Your account and your game and system info are included.";
        const string SignedOut = "You are not signed in. This report will be sent after you sign in.";

        Assert.Equal([Included], Open(signedIn: true).InfoLines());
        Assert.Equal([Included, SignedOut], Open(signedIn: false).InfoLines());
    }

    [Fact]
    public void Category_dropdown_lists_localized_labels()
    {
        using var _ = LocalizationContext.Use(new BugReportStringsTests.Catalog()
            .Add("bugreport.category.gameplay", "Jouabilite")
            .Add("bugreport.category.other", "Autre"));

        BugReportScreen screen = Open();
        Assert.Equal(["Jouabilite", "Visual", "Audio", "Performance", "Interface", "Autre"],
            screen.CategoryDropdown.Options.Select(o => o.Content.Resolve()));
        Assert.Equal(5, screen.CategoryDropdown.SelectedIndex);

        Tap(screen.Layout.Category);
        Tap(screen.CategoryDropdown.OptionBounds(0));
        Assert.Equal("Gameplay", screen.Model.Category);

        BugReportScreen custom = Open(categories: ["Crafting"]);
        Assert.Equal(["Crafting"], custom.CategoryDropdown.Options.Select(o => o.Content.Resolve()));
    }

    [Fact]
    public void Layout_centres_the_panel_and_never_scales_up()
    {
        BugReportScreenLayout large = BugReportScreenLayout.Compute(new Rect(0, 0, 1280, 720));
        Assert.Equal(new Rect(320, 80, 640, 560), large.Panel);

        BugReportScreenLayout small = BugReportScreenLayout.Compute(new Rect(0, 0, 640, 400));
        Assert.Equal(360f, small.Panel.Height, 3);
        Assert.Equal(400f * 0.05f, small.Panel.Y, 3);
        Assert.Equal((640f - small.Panel.Width) * 0.5f, small.Panel.X, 3);
        Assert.True(small.Send.Bottom <= small.Panel.Bottom);
    }

    sealed class CountingScreen : GuiScreen
    {
        public int Updates;

        public CountingScreen() => PassUpdateThrough = true;

        public override bool Update(float dt, bool receivesInput)
        {
            Updates++;
            return false;
        }

        public override void Draw(SpriteBatch batch) { }
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 20f;

        public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

        public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
    }
}
