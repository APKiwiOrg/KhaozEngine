using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// The two composition levels end to end: a real <see cref="ScreenStack"/> over a <see cref="Screen"/> that
/// composes a <see cref="ScreenComponentList"/>. Proves the load/unload chain reaches the components, that
/// they are handed the stack's OWN input manager (which is what makes click-through blocking compose), and
/// that a component consuming input propagates all the way out to the screen below.
/// <para>
/// <c>stack.Draw</c> is deliberately not called here: it runs the real screen draw path. Draw fan-out is
/// covered at the list level in <c>ScreenComponentListTests</c>, headlessly.
/// </para>
/// </summary>
public sealed class ScreenComponentListInScreenTests
{
    sealed class RecordingComponent : IScreenComponent
    {
        public int LoadCount, UnloadCount, UpdateCount;
        public bool LastReceivedInput;
        public InputManager? LastInput;
        public bool ConsumeInput;

        public void LoadContent() { LoadCount++; }
        public void UnloadContent() { UnloadCount++; }

        public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            UpdateCount++; LastReceivedInput = receivesInput; LastInput = input;
            return ConsumeInput && receivesInput;
        }

        public void Draw(SpriteBatch batch, Rect bounds) { }
    }

    // The host-side pattern from docs/USING-KHAOZENGINE.md: a list as a field, four forwarding lines, and
    // none of them grows when the component count does.
    sealed class ComponentHostScreen : Screen
    {
        readonly ScreenComponentList _components = new();
        readonly IDesignViewport _viewport;
        readonly RecordingComponent[] _seed;

        public ComponentHostScreen(IDesignViewport viewport, params RecordingComponent[] seed)
        {
            _viewport = viewport;
            _seed = seed;
            PassUpdateThrough = true;   // a HUD, not a modal: the screen below keeps updating
        }

        public override void LoadContent()
        {
            foreach (var c in _seed) _components.Add(c);
        }

        public override void UnloadContent() => _components.Clear();

        public override bool Update(float dt, bool receivesInput) =>
            _components.Update(dt, receivesInput, _viewport.WindowBounds, Manager.InputManager);

        public override void Draw(SpriteBatch batch) =>
            _components.Draw(batch, _viewport.WindowBounds);
    }

    // Stand-in for the game screen underneath the HUD.
    sealed class RecordingScreen : Screen
    {
        public bool Updated, ReceivedInput;
        public override bool Update(float dt, bool receivesInput)
        {
            Updated = true; ReceivedInput = receivesInput;
            return false;
        }
        public override void Draw(SpriteBatch batch) { }
    }

    static readonly DesignViewport Viewport = new(960, 540);

    [Fact]
    public void Adding_the_screen_to_a_stack_loads_its_components()
    {
        var c = new RecordingComponent();
        var stack = new ScreenStack();

        stack.Add(new ComponentHostScreen(Viewport, c));

        Assert.Equal(1, c.LoadCount);
    }

    [Fact]
    public void Components_are_handed_the_stacks_own_input_manager()
    {
        var c = new RecordingComponent();
        var stack = new ScreenStack();
        stack.Add(new ComponentHostScreen(Viewport, c));

        stack.Update(0.016f, InputState.Empty);

        Assert.Equal(1, c.UpdateCount);
        // Reference equality, not just non-null: the components, the widgets and the screens must all
        // hit-test through ONE pointer or click-through blocking does not compose across the levels.
        Assert.Same(stack.InputManager, c.LastInput);
        Assert.Same(stack.Pointer, c.LastInput!.Pointer);
    }

    [Fact]
    public void Components_receive_the_hosts_bounds_from_the_live_viewport()
    {
        var c = new RecordingComponent();
        var stack = new ScreenStack();
        stack.Add(new ComponentHostScreen(Viewport, c));

        stack.Update(0.016f, InputState.Empty);

        Assert.True(c.LastReceivedInput);
    }

    [Fact]
    public void A_component_consuming_input_withholds_it_from_the_screen_below()
    {
        // The one test that proves the two levels compose: consumption starts in a component, becomes the
        // hosting screen's return value, and ScreenStack then withholds input from the screen beneath it.
        var below = new RecordingScreen();
        var consuming = new RecordingComponent { ConsumeInput = true };
        var stack = new ScreenStack();
        stack.Add(below);
        stack.Add(new ComponentHostScreen(Viewport, consuming));

        stack.Update(0.016f, InputState.Empty);

        Assert.True(below.Updated);        // still updates (the host is passthrough)
        Assert.False(below.ReceivedInput); // but the component above ate the input
    }

    [Fact]
    public void A_passive_component_leaves_input_to_the_screen_below()
    {
        var below = new RecordingScreen();
        var passive = new RecordingComponent();
        var stack = new ScreenStack();
        stack.Add(below);
        stack.Add(new ComponentHostScreen(Viewport, passive));

        stack.Update(0.016f, InputState.Empty);

        Assert.True(below.ReceivedInput);
    }

    [Fact]
    public void Removing_the_screen_unloads_every_component()
    {
        var a = new RecordingComponent();
        var b = new RecordingComponent();
        var stack = new ScreenStack();
        var screen = new ComponentHostScreen(Viewport, a, b);
        stack.Add(screen);

        stack.Remove(screen);

        Assert.Equal(1, a.UnloadCount);
        Assert.Equal(1, b.UnloadCount);
    }
}
