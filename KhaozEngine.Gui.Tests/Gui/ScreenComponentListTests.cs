using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// Fan-out, ordering, and input-routing contract of <see cref="ScreenComponentList"/>.
/// <para>
/// Headless throughout, including the DRAW tests. A real <c>SpriteFont</c> needs an <c>IGpuDevice</c> to bake
/// its atlas and cannot be constructed in a default headless run (see <c>PatchNotesScreenTests</c>), but
/// <see cref="ScreenComponentList.Draw"/> only forwards the batch and the fakes below only count calls, so
/// passing <c>null!</c> for the batch exercises draw order fully without a GPU. Nothing here needs golden
/// images or a device.
/// </para>
/// </summary>
public sealed class ScreenComponentListTests
{
    static readonly Rect Bounds = new(0f, 0f, 960f, 540f);

    static InputManager NewInput()
    {
        var input = new InputManager();
        input.Update(InputState.Empty, null);
        return input;
    }

    // Records every lifecycle call it receives, optionally into a shared ordering log.
    sealed class FakeComponent : IScreenComponent
    {
        public string Name = "";
        public List<string>? Log;                 // shared ordering log
        public int UpdateCount, DrawCount, LoadCount, UnloadCount;
        public bool LastReceivedInput;
        public Rect LastUpdateBounds, LastDrawBounds;
        public bool ConsumeInput;                 // well-behaved consumer
        public bool ConsumeAlways;                // contract VIOLATOR, for the poison test
        public Action? OnUpdate;                  // for the mutation-during-iteration tests

        public void LoadContent() { LoadCount++; Log?.Add($"load:{Name}"); }
        public void UnloadContent() { UnloadCount++; Log?.Add($"unload:{Name}"); }

        public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            UpdateCount++; LastReceivedInput = receivesInput; LastUpdateBounds = bounds;
            Log?.Add($"update:{Name}");
            OnUpdate?.Invoke();
            return ConsumeAlways || (ConsumeInput && receivesInput);
        }

        public void Draw(SpriteBatch batch, Rect bounds)
        {
            DrawCount++; LastDrawBounds = bounds; Log?.Add($"draw:{Name}");
        }
    }

    // Declares ONLY the two required members. That this compiles at all is the default-interface-member test:
    // LoadContent/UnloadContent are DIMs, so a component with nothing to load omits them entirely.
    sealed class MinimalComponent : IScreenComponent
    {
        public int UpdateCount, DrawCount;
        public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input) { UpdateCount++; return false; }
        public void Draw(SpriteBatch batch, Rect bounds) { DrawCount++; }
    }

    // Throws out of LoadContent, capturing the list's Count at that moment to prove load-then-insert ordering.
    sealed class ThrowingLoadComponent : IScreenComponent
    {
        public ScreenComponentList Owner = null!;
        public int CountSeenDuringLoad = -1;
        public void LoadContent()
        {
            CountSeenDuringLoad = Owner.Count;
            throw new InvalidOperationException("asset load failed");
        }
        public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input) => false;
        public void Draw(SpriteBatch batch, Rect bounds) { }
    }

    // Looks back at the owning list from inside UnloadContent, which is how the unload-BEFORE-remove ordering
    // is observable at all: a component that unloads after its own removal sees a list it is no longer in.
    sealed class UnloadObservingComponent : IScreenComponent
    {
        public ScreenComponentList Owner = null!;
        public bool StillPresentDuringUnload;
        public int CountSeenDuringUnload = -1;

        public void UnloadContent()
        {
            StillPresentDuringUnload = Owner.Items.Contains(this);
            CountSeenDuringUnload = Owner.Count;
        }

        public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input) => false;
        public void Draw(SpriteBatch batch, Rect bounds) { }
    }

    static (ScreenComponentList list, FakeComponent bottom, FakeComponent middle, FakeComponent top, List<string> log) ThreeDeep()
    {
        var log = new List<string>();
        var list = new ScreenComponentList();
        var bottom = list.Add(new FakeComponent { Name = "bottom", Log = log });
        var middle = list.Add(new FakeComponent { Name = "middle", Log = log });
        var top = list.Add(new FakeComponent { Name = "top", Log = log });
        return (list, bottom, middle, top, log);
    }

    // ---- Registration and lifecycle -------------------------------------------------------------------

    [Fact]
    public void Add_loads_the_component_once()
    {
        var list = new ScreenComponentList();
        var c = list.Add(new FakeComponent());

        Assert.Equal(1, c.LoadCount);
        Assert.Equal(1, list.Count);
        Assert.Same(c, Assert.Single(list.Items));
    }

    [Fact]
    public void Add_loads_BEFORE_inserting_so_a_throwing_load_leaves_no_half_live_component()
    {
        var list = new ScreenComponentList();
        var c = new ThrowingLoadComponent();
        c.Owner = list;

        Assert.Throws<InvalidOperationException>(() => list.Add(c));

        Assert.Equal(0, c.CountSeenDuringLoad);   // not yet in the list while loading, as ScreenStack.Add does
        Assert.Equal(0, list.Count);              // and the throw left nothing behind
    }

    [Fact]
    public void Add_returns_the_component_with_its_concrete_type_preserved()
    {
        var list = new ScreenComponentList();

        // The point of the test is the compile-time type: Add<T> returns T, not IScreenComponent, so a host
        // can register and keep a typed reference in one line.
        FakeComponent typed = list.Add(new FakeComponent { Name = "typed" });

        Assert.Equal("typed", typed.Name);
    }

    [Fact]
    public void Add_null_throws()
    {
        var list = new ScreenComponentList();
        Assert.Throws<ArgumentNullException>(() => list.Add<FakeComponent>(null!));
    }

    [Fact]
    public void Remove_unloads_a_registered_component_and_returns_true()
    {
        var list = new ScreenComponentList();
        var c = list.Add(new FakeComponent());

        Assert.True(list.Remove(c));
        Assert.Equal(1, c.UnloadCount);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Remove_of_an_unregistered_component_returns_false_and_unloads_nothing()
    {
        var list = new ScreenComponentList();
        var stranger = new FakeComponent();

        Assert.False(list.Remove(stranger));
        Assert.Equal(0, stranger.UnloadCount);
    }

    [Fact]
    public void Remove_unloads_BEFORE_removing_from_the_list()
    {
        // The ordering ScreenStack.Remove uses (unload, then drop it), and the one Clear already used. Untested,
        // Remove drifted to the opposite order while the CHANGELOG documented this one.
        var list = new ScreenComponentList();
        var c = new UnloadObservingComponent();
        c.Owner = list;
        list.Add(c);

        Assert.True(list.Remove(c));

        Assert.True(c.StillPresentDuringUnload);
        Assert.Equal(1, c.CountSeenDuringUnload);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Clear_unloads_BEFORE_removing_too_so_both_teardown_paths_agree()
    {
        var list = new ScreenComponentList();
        var a = new UnloadObservingComponent { Owner = list };
        var b = new UnloadObservingComponent { Owner = list };
        var c = new UnloadObservingComponent { Owner = list };
        list.Add(a); list.Add(b); list.Add(c);

        list.Clear();

        foreach (var each in new[] { a, b, c })
        {
            Assert.True(each.StillPresentDuringUnload);
            Assert.Equal(3, each.CountSeenDuringUnload);   // Clear unloads the whole set before dropping any of it
        }
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Clear_unloads_every_component_topmost_first()
    {
        var (list, _, _, _, log) = ThreeDeep();
        log.Clear();

        list.Clear();

        Assert.Equal(new[] { "unload:top", "unload:middle", "unload:bottom" }, log);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Clear_on_an_empty_list_is_a_no_op()
    {
        var list = new ScreenComponentList();
        list.Clear();
        Assert.Equal(0, list.Count);
    }

    // ---- Ordering -------------------------------------------------------------------------------------

    [Fact]
    public void Update_runs_top_down_and_Draw_runs_bottom_up()
    {
        var (list, _, _, _, log) = ThreeDeep();
        log.Clear();

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());
        list.Draw(null!, Bounds);

        // Stated once, visibly: the two passes are exact opposites. Update offers input to the topmost first,
        // Draw paints the first-added underneath.
        Assert.Equal(
            new[]
            {
                "update:top", "update:middle", "update:bottom",
                "draw:bottom", "draw:middle", "draw:top",
            },
            log);
    }

    // ---- Input consumption ----------------------------------------------------------------------------

    [Fact]
    public void A_consuming_component_blocks_input_to_the_ones_below_it()
    {
        var (list, bottom, middle, top, _) = ThreeDeep();
        middle.ConsumeInput = true;

        bool consumed = list.Update(0.016f, receivesInput: true, Bounds, NewInput());

        Assert.True(top.LastReceivedInput);      // nothing above it
        Assert.True(middle.LastReceivedInput);   // top consumed nothing
        Assert.False(bottom.LastReceivedInput);  // middle consumed
        Assert.True(consumed);
    }

    [Fact]
    public void Every_component_still_updates_even_when_blocked_from_input()
    {
        var (list, bottom, middle, top, _) = ThreeDeep();
        middle.ConsumeInput = true;

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());

        // "Received" and "consumed" are different questions, per Screen.Update: a blocked component still ticks.
        Assert.Equal(1, top.UpdateCount);
        Assert.Equal(1, middle.UpdateCount);
        Assert.Equal(1, bottom.UpdateCount);
    }

    [Fact]
    public void Update_returns_false_when_nothing_consumed()
    {
        var (list, _, _, _, _) = ThreeDeep();
        Assert.False(list.Update(0.016f, receivesInput: true, Bounds, NewInput()));
    }

    [Fact]
    public void A_blocked_host_propagates_false_to_every_component_and_returns_false()
    {
        var (list, bottom, middle, top, _) = ThreeDeep();
        middle.ConsumeInput = true;   // would consume if it were offered input

        bool consumed = list.Update(0.016f, receivesInput: false, Bounds, NewInput());

        Assert.False(consumed);
        Assert.False(top.LastReceivedInput);
        Assert.False(middle.LastReceivedInput);
        Assert.False(bottom.LastReceivedInput);
        Assert.Equal(1, bottom.UpdateCount);   // still updated, just not offered input
    }

    [Fact]
    public void A_contract_violating_component_cannot_poison_the_latch_while_blocked()
    {
        // THE guard test. A component that returns true without having been offered input is violating the
        // contract. The latch is guarded by `receives` (mirroring ScreenStack.cs:98), so its lie is ignored.
        // Without that guard this Update would return true and the whole host would claim it consumed input.
        var (list, bottom, middle, top, _) = ThreeDeep();
        middle.ConsumeAlways = true;

        bool consumed = list.Update(0.016f, receivesInput: false, Bounds, NewInput());

        Assert.False(middle.LastReceivedInput);   // it was blocked
        Assert.False(consumed);                   // and its bare `true` was not believed
        Assert.False(bottom.LastReceivedInput);   // so it could not starve the one below it either
        Assert.Equal(1, top.UpdateCount);
    }

    [Fact]
    public void A_contract_violating_component_below_a_consumer_cannot_starve_the_rest()
    {
        var (list, bottom, middle, top, _) = ThreeDeep();
        top.ConsumeInput = true;      // legitimately consumes, blocking the two below
        middle.ConsumeAlways = true;  // blocked, but lies about consuming anyway

        bool consumed = list.Update(0.016f, receivesInput: true, Bounds, NewInput());

        Assert.True(consumed);                    // true because of `top`, which really did consume
        Assert.False(middle.LastReceivedInput);
        Assert.False(bottom.LastReceivedInput);
        Assert.Equal(1, bottom.UpdateCount);      // and everything below still ticked
    }

    [Fact]
    public void An_empty_list_updates_to_false_and_draws_without_throwing()
    {
        var list = new ScreenComponentList();

        Assert.False(list.Update(0.016f, receivesInput: true, Bounds, NewInput()));
        list.Draw(null!, Bounds);
        Assert.Equal(0, list.Count);
    }

    // ---- Bounds ---------------------------------------------------------------------------------------

    [Fact]
    public void Bounds_reach_every_component_unchanged_in_both_passes()
    {
        var (list, bottom, middle, top, _) = ThreeDeep();
        var bounds = new Rect(12f, 34f, 500f, 250f);

        list.Update(0.016f, receivesInput: true, bounds, NewInput());
        list.Draw(null!, bounds);

        foreach (var c in new[] { bottom, middle, top })
        {
            Assert.Equal(bounds, c.LastUpdateBounds);
            Assert.Equal(bounds, c.LastDrawBounds);
        }
    }

    [Fact]
    public void Bounds_are_per_call_so_a_resize_is_picked_up_with_no_resize_hook()
    {
        // The reason bounds is a parameter and not a captured field. Nothing anywhere caches it, so a host
        // that re-reads its viewport each frame gets a correct layout after a resize or a letterbox change
        // without any OnResize plumbing.
        var (list, _, _, top, _) = ThreeDeep();
        var first = new Rect(0f, 0f, 960f, 540f);
        var resized = new Rect(-40f, 0f, 1040f, 540f);

        list.Update(0.016f, receivesInput: true, first, NewInput());
        list.Draw(null!, first);
        Assert.Equal(first, top.LastUpdateBounds);

        list.Update(0.016f, receivesInput: true, resized, NewInput());
        list.Draw(null!, resized);
        Assert.Equal(resized, top.LastUpdateBounds);
        Assert.Equal(resized, top.LastDrawBounds);
    }

    // ---- Mutation during iteration --------------------------------------------------------------------

    [Fact]
    public void A_component_can_remove_itself_during_its_own_Update()
    {
        var (list, bottom, middle, top, _) = ThreeDeep();
        middle.OnUpdate = () => list.Remove(middle);

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());

        Assert.Equal(1, middle.UnloadCount);   // removal really unloaded it
        Assert.Equal(1, bottom.UpdateCount);   // and the iteration was not disturbed: the one below still ran
        Assert.Equal(1, top.UpdateCount);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void A_component_removing_one_BELOW_itself_neither_re_updates_nor_skips_anything()
    {
        // THE test for the scratch copy, and the only mutation case that actually needs it. Reverse iteration is
        // incidentally robust to a component removing ITSELF and to an append past the cursor, so both other
        // mutation tests pass against an implementation that iterates _items directly. This one does not: without
        // the copy, removing `bottom` from inside `top` shifts everything down one, so the descending index lands
        // on `top` a second time and `bottom` never runs at all.
        var (list, bottom, middle, top, _) = ThreeDeep();
        top.OnUpdate = () => list.Remove(bottom);

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());

        Assert.Equal(1, top.UpdateCount);      // not re-updated
        Assert.Equal(1, middle.UpdateCount);
        Assert.Equal(1, bottom.UpdateCount);   // the frame in flight iterates the copy taken before the removal
        Assert.Equal(1, bottom.UnloadCount);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void A_component_added_during_Update_does_not_run_until_the_next_frame()
    {
        var list = new ScreenComponentList();
        var latecomer = new FakeComponent { Name = "latecomer" };
        var host = list.Add(new FakeComponent { Name = "host" });
        bool added = false;
        host.OnUpdate = () => { if (added) return; added = true; list.Add(latecomer); };

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());
        Assert.Equal(1, latecomer.LoadCount);    // Add loaded it immediately
        Assert.Equal(0, latecomer.UpdateCount);  // but the frame in flight iterates a scratch copy

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());
        Assert.Equal(1, latecomer.UpdateCount);
    }

    // ---- Default interface members --------------------------------------------------------------------

    [Fact]
    public void A_component_that_declares_neither_load_nor_unload_works_through_the_whole_lifecycle()
    {
        // Would fail to compile (or throw) if LoadContent/UnloadContent were required members rather than
        // default interface members.
        var list = new ScreenComponentList();
        var minimal = list.Add(new MinimalComponent());

        list.Update(0.016f, receivesInput: true, Bounds, NewInput());
        list.Draw(null!, Bounds);
        Assert.True(list.Remove(minimal));

        list.Add(new MinimalComponent());
        list.Clear();

        Assert.Equal(1, minimal.UpdateCount);
        Assert.Equal(1, minimal.DrawCount);
        Assert.Equal(0, list.Count);
    }
}
