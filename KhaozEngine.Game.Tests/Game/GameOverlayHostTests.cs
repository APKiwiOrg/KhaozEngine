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

    sealed class IdleOverlay : IGameOverlay
    {
        public void Attach(GameOverlayContext context) { }
        public bool Update(InputState input, float realDt) => false;
        public void DrawUi(SpriteBatch batch) { }
    }
}
