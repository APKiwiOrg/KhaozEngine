using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game;

// GameApp needs a real window, so its two call sites are sample verified. The host owns every overlay rule
// (order, hold, fault containment, changes during a pass) and is driven here headless. The batch is never
// touched by these fakes, so a null batch stands in for the begun UI batch.
[Collection("AllocSensitive")]
public sealed class GameOverlayHostTests
{
    static readonly GameOverlayContext Context = new(null!, new UiViewport(), null!);
    static readonly SpriteBatch NoBatch = null!;

    static InputState Frame() => new(
        new HashSet<Key> { Key.F10 }, new HashSet<Key> { Key.F10 }, new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(), new Vector2(5f, 5f), Vector2.Zero, 0f, 800, 600,
        textInput: "x");

    sealed class FakeOverlay : IGameOverlay
    {
        readonly string _name;
        readonly List<string> _log;

        public FakeOverlay(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public bool Holds { get; set; }
        public bool ThrowOnUpdate { get; set; }
        public bool ThrowOnDraw { get; set; }
        public Action? DuringUpdate { get; set; }
        public Action? DuringDraw { get; set; }
        public int Attaches { get; private set; }
        public InputState? LastInput { get; private set; }

        public void Attach(GameOverlayContext context)
        {
            Assert.Same(Context, context);
            Attaches++;
        }

        public bool Update(InputState input, float realDt)
        {
            _log.Add($"update {_name}");
            LastInput = input;
            DuringUpdate?.Invoke();
            if (ThrowOnUpdate) throw new InvalidOperationException($"{_name} broke");
            return Holds;
        }

        public void DrawUi(SpriteBatch batch)
        {
            _log.Add($"draw {_name}");
            DuringDraw?.Invoke();
            if (ThrowOnDraw) throw new InvalidOperationException($"{_name} broke");
        }
    }

    readonly List<string> _log = new();
    readonly List<string> _errors = new();

    GameOverlayHost NewHost() => new((message, _) => _errors.Add(message));

    FakeOverlay Add(GameOverlayHost host, string name)
    {
        var overlay = new FakeOverlay(name, _log);
        host.Add(overlay, Context);
        return overlay;
    }

    [Fact]
    public void No_overlays_return_the_same_instance()
    {
        var host = NewHost();
        InputState input = Frame();

        Assert.True(host.IsEmpty);
        Assert.Same(input, host.Update(input, 0.016f));

        Add(host, "a");
        Add(host, "b");
        Assert.False(host.IsEmpty);
        Assert.Same(input, host.Update(input, 0.016f));
    }

    [Fact]
    public void Any_holding_overlay_holds_the_game()
    {
        var host = NewHost();
        Add(host, "a");
        Add(host, "b").Holds = true;
        InputState input = Frame();

        InputState seen = host.Update(input, 0.016f);

        Assert.NotSame(input, seen);
        Assert.Empty(seen.KeysPressed);
        Assert.Equal("", seen.TextInput);
        Assert.Equal(800, seen.Width);
    }

    [Fact]
    public void Every_overlay_updates_in_add_order_even_after_one_holds()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        a.Holds = true;
        FakeOverlay b = Add(host, "b");
        InputState input = Frame();

        host.Update(input, 0.016f);

        Assert.Equal(new[] { "update a", "update b" }, _log);
        // Every overlay reads the raw frame, not the held one.
        Assert.Same(input, a.LastInput);
        Assert.Same(input, b.LastInput);
    }

    [Fact]
    public void Draw_runs_in_add_order()
    {
        var host = NewHost();
        Add(host, "a");
        Add(host, "b");
        Add(host, "c");
        host.Update(Frame(), 0.016f);
        _log.Clear();

        host.DrawUi(NoBatch);

        Assert.Equal(new[] { "draw a", "draw b", "draw c" }, _log);
    }

    [Fact]
    public void Add_attaches_once_and_rejects_a_second_add()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");

        Assert.Throws<InvalidOperationException>(() => host.Add(a, Context));
        Assert.Equal(1, a.Attaches);

        host.Update(Frame(), 0.016f);
        Assert.Equal(new[] { "update a" }, _log);
    }

    [Fact]
    public void Removed_overlay_stops_updating()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        Add(host, "b");
        host.Update(Frame(), 0.016f);
        _log.Clear();

        Assert.True(host.Remove(a));
        Assert.False(host.Remove(a));
        host.Update(Frame(), 0.016f);
        host.DrawUi(NoBatch);

        Assert.Equal(new[] { "update b", "draw b" }, _log);
    }

    [Fact]
    public void Throwing_update_removes_only_that_overlay()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        a.Holds = true;
        a.ThrowOnUpdate = true;
        Add(host, "b");
        InputState input = Frame();

        InputState seen = host.Update(input, 0.016f);

        Assert.Same(input, seen);
        Assert.Equal(new[] { "update a", "update b" }, _log);
        string error = Assert.Single(_errors);
        Assert.Contains(nameof(FakeOverlay), error);
        Assert.Contains("a broke", error);

        _log.Clear();
        host.Update(Frame(), 0.016f);
        Assert.Equal(new[] { "update b" }, _log);
        Assert.Single(_errors);
    }

    [Fact]
    public void Throwing_draw_removes_only_that_overlay()
    {
        var host = NewHost();
        Add(host, "a").ThrowOnDraw = true;
        Add(host, "b");
        host.Update(Frame(), 0.016f);
        _log.Clear();

        host.DrawUi(NoBatch);

        Assert.Equal(new[] { "draw a", "draw b" }, _log);
        Assert.Single(_errors);

        _log.Clear();
        host.Update(Frame(), 0.016f);
        host.DrawUi(NoBatch);
        Assert.Equal(new[] { "update b", "draw b" }, _log);
        Assert.Single(_errors);
    }

    [Fact]
    public void Overlay_removing_itself_during_update_is_safe()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        FakeOverlay b = Add(host, "b");
        FakeOverlay c = Add(host, "c");
        FakeOverlay? late = null;
        a.DuringUpdate = () =>
        {
            Assert.True(host.Remove(a));
            Assert.True(host.Remove(c));
            late = Add(host, "late");
        };

        host.Update(Frame(), 0.016f);

        // a finishes its own pass, c had not run yet so it is skipped, and the late add waits for the next frame.
        Assert.Equal(new[] { "update a", "update b" }, _log);
        _log.Clear();
        host.DrawUi(NoBatch);
        Assert.Equal(new[] { "draw b" }, _log);

        _log.Clear();
        a.DuringUpdate = null;
        host.Update(Frame(), 0.016f);
        host.DrawUi(NoBatch);
        Assert.Equal(new[] { "update b", "update late", "draw b", "draw late" }, _log);
        Assert.NotNull(late);
        Assert.Empty(_errors);
    }

    [Fact]
    public void Overlay_changing_the_set_during_draw_is_safe()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        FakeOverlay b = Add(host, "b");
        FakeOverlay? late = null;
        a.DuringDraw = () =>
        {
            Assert.True(host.Remove(b));
            late = Add(host, "late");
        };
        host.Update(Frame(), 0.016f);
        _log.Clear();

        host.DrawUi(NoBatch);

        // b had not drawn yet so it is skipped, and the late add waits for the next frame.
        Assert.Equal(new[] { "draw a" }, _log);

        _log.Clear();
        a.DuringDraw = null;
        host.Update(Frame(), 0.016f);
        host.DrawUi(NoBatch);
        Assert.Equal(new[] { "update a", "update late", "draw a", "draw late" }, _log);
        Assert.NotNull(late);
        Assert.Empty(_errors);
    }

    [Fact]
    public void Throwing_attach_propagates_and_registers_nothing()
    {
        var host = NewHost();
        InputState input = Frame();

        Assert.Throws<InvalidOperationException>(() => host.Add(new ThrowingAttachOverlay(), Context));

        Assert.True(host.IsEmpty);
        Assert.Same(input, host.Update(input, 0.016f));
    }

    [Fact]
    public void Overlays_that_compare_equal_are_still_distinct()
    {
        var host = NewHost();
        var first = new EqualOverlay("first", _log);
        var second = new EqualOverlay("second", _log);
        Assert.Equal<IGameOverlay>(first, second);

        host.Add(first, Context);
        host.Add(second, Context);
        host.Update(Frame(), 0.016f);
        Assert.Equal(new[] { "update first", "update second" }, _log);

        _log.Clear();
        Assert.True(host.Remove(second));
        Assert.False(host.Remove(second));
        host.Update(Frame(), 0.016f);
        Assert.Equal(new[] { "update first" }, _log);
    }

    [Fact]
    public void Hold_ending_with_mouse_down_gives_no_fresh_press()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        var game = new InputManager();
        a.Holds = true;
        game.Update(host.Update(Mouse(down: true, pressed: true), 0.016f));
        Assert.Empty(game.State.MouseDown);

        // The hold ends with the button still down. The game sees nothing of it, its release included.
        a.Holds = false;
        for (int i = 0; i < 3; i++)
        {
            game.Update(host.Update(Mouse(down: true), 0.016f));
            Assert.Empty(game.State.MouseDown);
            Assert.False(game.IsPointerJustPressed);
        }
        game.Update(host.Update(Mouse(released: true), 0.016f));
        Assert.Empty(game.State.MouseReleased);
        Assert.False(game.IsPointerJustPressed);

        InputState press = Mouse(down: true, pressed: true);
        game.Update(host.Update(press, 0.016f));
        Assert.Same(press, game.State);
        Assert.True(game.IsPointerJustPressed);
    }

    [Fact]
    public void Hold_ending_with_a_stick_deflected_gives_no_menu_step()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        var game = new InputManager();
        a.Holds = true;
        game.Update(host.Update(Pad(new Vector2(0f, -0.9f)), 0.016f));

        a.Holds = false;
        for (int i = 0; i < 3; i++)
        {
            game.Update(host.Update(Pad(new Vector2(0f, -0.9f)), 0.016f));
            Assert.Equal(Vector2.Zero, game.State.Gamepad(0).LeftStick);
            Assert.False(game.IsMenuDown());
        }

        // The pad goes idle. It reaches the game again from the next frame, so a fresh deflection steps.
        game.Update(host.Update(Pad(Vector2.Zero), 0.016f));
        Assert.False(game.IsMenuDown());
        InputState rest = Pad(Vector2.Zero);
        game.Update(host.Update(rest, 0.016f));
        Assert.Same(rest, game.State);
        game.Update(host.Update(Pad(new Vector2(0f, -0.9f)), 0.016f));
        Assert.True(game.IsMenuDown());
    }

    [Fact]
    public void Hold_ending_with_a_trigger_pulled_hides_the_trigger_until_released()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        a.Holds = true;
        host.Update(PadTrigger(1f), 0.016f);

        a.Holds = false;
        Assert.Equal(0f, host.Update(PadTrigger(1f), 0.016f).Gamepad(0).RightTrigger);
        Assert.Equal(0f, host.Update(PadTrigger(0.8f), 0.016f).Gamepad(0).RightTrigger);

        // Released, then a fresh pull reaches the game.
        host.Update(PadTrigger(0f), 0.016f);
        Assert.Equal(1f, host.Update(PadTrigger(1f), 0.016f).Gamepad(0).RightTrigger);
    }

    [Fact]
    public void Keys_held_across_a_hold_have_no_press_edge()
    {
        var host = NewHost();
        FakeOverlay a = Add(host, "a");
        var game = new InputManager();
        a.Holds = true;
        game.Update(host.Update(Keys(Key.Space, pressed: true), 0.016f));

        a.Holds = false;
        InputState after = Keys(Key.Space, pressed: false);
        game.Update(host.Update(after, 0.016f));

        // Nothing to hide, so the hold ends without a copy.
        Assert.Same(after, game.State);
        Assert.Contains(Key.Space, game.State.KeysDown);
        Assert.False(game.IsKeyJustPressed(Key.Space));
    }

    [Fact]
    public void Idle_frame_allocates_nothing()
    {
        var host = new GameOverlayHost((_, _) => { });
        host.Add(new IdleOverlay(), Context);
        host.Add(new IdleOverlay(), Context);
        InputState input = Frame();
        host.Update(input, 0.016f);
        host.DrawUi(NoBatch);

        AllocAssert.NoPerCallAllocation("idle overlay frame", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                host.Update(input, 0.016f);
                host.DrawUi(NoBatch);
            }
        });
    }

    static InputState Mouse(bool down = false, bool pressed = false, bool released = false) => new(
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        Buttons(down), Buttons(pressed), new Vector2(5f, 5f), Vector2.Zero, 0f, 800, 600,
        mouseReleased: Buttons(released));

    static HashSet<MouseButton> Buttons(bool left) => left ? new() { MouseButton.Left } : new();

    static InputState Pad(Vector2 leftStick) => new(
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(), new Vector2(5f, 5f), Vector2.Zero, 0f, 800, 600,
        gamepads: new[]
        {
            new GamepadState(0, new HashSet<GamepadButton>(), new HashSet<GamepadButton>(),
                new HashSet<GamepadButton>(), leftStick, Vector2.Zero, 0f, 0f),
        });

    static InputState PadTrigger(float rightTrigger) => new(
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(), new Vector2(5f, 5f), Vector2.Zero, 0f, 800, 600,
        gamepads: new[]
        {
            new GamepadState(0, new HashSet<GamepadButton>(), new HashSet<GamepadButton>(),
                new HashSet<GamepadButton>(), Vector2.Zero, Vector2.Zero, 0f, rightTrigger),
        });

    static InputState Keys(Key key, bool pressed) => new(
        new HashSet<Key> { key }, pressed ? new HashSet<Key> { key } : new HashSet<Key>(), new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(), new Vector2(5f, 5f), Vector2.Zero, 0f, 800, 600);

    sealed class ThrowingAttachOverlay : IGameOverlay
    {
        public void Attach(GameOverlayContext context) => throw new InvalidOperationException("attach broke");
        public bool Update(InputState input, float realDt) => false;
        public void DrawUi(SpriteBatch batch) { }
    }

    // Every instance equals every other, so only reference membership tells them apart.
    sealed class EqualOverlay : IGameOverlay
    {
        readonly string _name;
        readonly List<string> _log;

        public EqualOverlay(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public override bool Equals(object? obj) => obj is EqualOverlay;
        public override int GetHashCode() => 0;
        public void Attach(GameOverlayContext context) { }

        public bool Update(InputState input, float realDt)
        {
            _log.Add($"update {_name}");
            return false;
        }

        public void DrawUi(SpriteBatch batch) { }
    }

    sealed class IdleOverlay : IGameOverlay
    {
        public void Attach(GameOverlayContext context) { }
        public bool Update(InputState input, float realDt) => false;
        public void DrawUi(SpriteBatch batch) { }
    }
}
