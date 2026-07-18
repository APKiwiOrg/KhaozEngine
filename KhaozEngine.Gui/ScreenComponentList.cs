using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// An ordered set of <see cref="IScreenComponent"/>s that a host fans out to once per lifecycle moment,
/// turning an N-collaborators-by-M-lifecycle-moments cross product into M loops. Routes input the same way
/// <see cref="ScreenStack"/> routes it between screens, one level down: <see cref="Update"/> runs
/// top-down and stops input at the first component that consumes it, <see cref="Draw"/> runs bottom-up.
/// <para>
/// Registration order IS z order. The first component added draws first (underneath) and is offered input
/// last. The last added draws last (on top) and is offered input first. There is no separate sort key,
/// matching <c>KhaozEngine.Ecs.ISystem</c>'s registration-order rule.
/// </para>
/// <para>
/// Hosted by composition, not inheritance: a <see cref="Screen"/> (or a <c>GameScene</c>, or any other
/// host) holds one as a field and forwards to it from its own overrides. Nothing here requires a
/// <see cref="Screen"/> or a <see cref="ScreenStack"/>, which is what keeps it headless-testable.
/// </para>
/// </summary>
public sealed class ScreenComponentList
{
    readonly List<IScreenComponent> _items = new();
    // Reused per-frame copy, so a component can add or remove during Update without disturbing the
    // iteration. Same device, and the same reason, as ScreenStack's _updateScratch.
    readonly List<IScreenComponent> _scratch = new();

    /// <summary>The components, in registration (draw) order. The last element is the topmost.</summary>
    public IReadOnlyList<IScreenComponent> Items => _items;

    /// <summary>How many components are registered.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Registers a component on top of the existing ones and calls its
    /// <see cref="IScreenComponent.LoadContent"/>. Returns the component, so a host can register and keep a
    /// typed reference in one line. Loads BEFORE inserting, mirroring <see cref="ScreenStack.Add"/>, so a
    /// throwing <c>LoadContent</c> leaves no half-live component in the list.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="component"/> is null.</exception>
    public T Add<T>(T component) where T : class, IScreenComponent
    {
        ArgumentNullException.ThrowIfNull(component);
        component.LoadContent();
        _items.Add(component);
        return component;
    }

    /// <summary>
    /// Removes a component and calls its <see cref="IScreenComponent.UnloadContent"/>. Returns false (and
    /// unloads nothing) when it is not registered. Unloads BEFORE removing, mirroring
    /// <see cref="ScreenStack.Remove"/> and matching <see cref="Clear"/>, so a component tearing itself down
    /// still sees the list it is leaving.
    /// </summary>
    public bool Remove(IScreenComponent component)
    {
        // Contains-then-Remove rather than caching an index: UnloadContent may itself mutate the list, and a
        // cached index would then point at the wrong element. ScreenStack.Remove unloads unconditionally and
        // discards the List.Remove result, so the containment check is the one deviation, and it is what buys
        // the documented "not registered unloads nothing" answer on top of the same ordering.
        if (!_items.Contains(component)) return false;
        component.UnloadContent();
        _items.Remove(component);
        return true;
    }

    /// <summary>
    /// Removes every component, unloading each one, topmost first (the reverse of the order they were
    /// added and loaded in). Call this from the host's teardown, e.g. a screen's
    /// <see cref="Screen.UnloadContent"/>.
    /// </summary>
    public void Clear()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            _items[i].UnloadContent();
        _items.Clear();
    }

    /// <summary>
    /// Updates every component top-down, withholding input from the ones below the first that consumes it.
    /// Returns whether ANY component consumed input, which is what a hosting <see cref="Screen"/> returns
    /// from its own <see cref="Screen.Update"/>.
    /// </summary>
    /// <param name="dt">Elapsed seconds since the last frame.</param>
    /// <param name="receivesInput">
    /// Whether this host may act on input at all this frame. False withholds input from every component and
    /// forces a false return, exactly as a blocked <see cref="Screen"/> must return false.
    /// </param>
    /// <param name="bounds">The region the components lay out within, re-read fresh by the host each frame.</param>
    /// <param name="input">The shared input manager, normally the owning stack's <c>Manager.InputManager</c>.</param>
    public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input)
    {
        _scratch.Clear();
        _scratch.AddRange(_items);

        bool consumed = false;
        for (int i = _scratch.Count - 1; i >= 0; i--)
        {
            bool receives = receivesInput && !consumed;
            bool result = _scratch[i].Update(dt, receives, bounds, input);
            // Latch only when the component was actually OFFERED input: a component that breaks its
            // contract and returns true while blocked cannot then starve the ones below it. Same guard
            // ScreenStack applies at the screen level.
            if (receives && result) consumed = true;
        }
        return consumed;
    }

    /// <summary>
    /// Draws every component in registration order (first added underneath). Iterates the live list, not a
    /// copy: nothing should mutate the set during Draw, matching <see cref="ScreenStack.Draw"/>.
    /// </summary>
    /// <param name="batch">The batch to draw into, already begun by the host.</param>
    /// <param name="bounds">The same region handed to <see cref="Update"/> that frame.</param>
    public void Draw(SpriteBatch batch, Rect bounds)
    {
        for (int i = 0; i < _items.Count; i++)
            _items[i].Draw(batch, bounds);
    }
}
