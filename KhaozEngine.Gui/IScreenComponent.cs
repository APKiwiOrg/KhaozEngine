using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// One composable piece of a <see cref="Screen"/>: a HUD element, an overlay, an input controller, a
/// presenter. The unit below <see cref="Screen"/>, mirroring <c>KhaozEngine.Ecs.ISystem</c>'s role below
/// <c>World</c>. A screen holds a <see cref="ScreenComponentList"/> of these and fans out to it once per
/// lifecycle moment, so the screen's size stops being a function of how many collaborators it has.
/// <para>
/// NOT a widget and NOT a layout node. The retained widgets (<see cref="Button"/>, <see cref="Slider"/>,
/// <see cref="TabBar"/>, ...) and the immediate-mode <see cref="GuiSurface"/> are the leaf level and are
/// unchanged: a component typically OWNS several of them. There is no parent/child tree, no layout pass,
/// and no data binding here. Use <see cref="Layout.Resolve"/> to place things inside the <c>bounds</c>
/// handed to <see cref="Update"/> and <see cref="Draw"/>.
/// </para>
/// </summary>
public interface IScreenComponent
{
    /// <summary>
    /// Per-frame update. <paramref name="receivesInput"/> and the return value carry EXACTLY the
    /// <see cref="Screen.Update"/> contract one level down, so read that member's documentation for the
    /// full rules: a component still updates every frame regardless of whether it may act on input, and it
    /// MUST return false whenever <paramref name="receivesInput"/> is false.
    /// </summary>
    /// <param name="dt">Elapsed seconds since the last frame.</param>
    /// <param name="receivesInput">
    /// False when a component ABOVE this one (added later) already consumed input this frame. Read it before
    /// touching <paramref name="input"/>.
    /// </param>
    /// <param name="bounds">
    /// The region this component lays out within, in the same coordinate space the owning screen draws with
    /// (normally <c>IDesignViewport.WindowBounds</c> or <c>DesignBounds</c>). Passed fresh every frame
    /// rather than captured, so a window resize or a letterbox change is picked up with no resize hook.
    /// </param>
    /// <param name="input">
    /// This frame's input. Normally the owning stack's shared <c>Manager.InputManager</c>, whose
    /// <see cref="InputManager.Pointer"/> IS that stack's pointer, so click-through blocking composes across
    /// components and screens alike. Hit-test through its bounds helpers
    /// (<see cref="InputManager.IsTapIn"/> and friends), never raw position plus button.
    /// </param>
    /// <returns>
    /// Whether THIS component consumed input THIS frame. True stops components BELOW it (added earlier)
    /// receiving input, and bubbles up as the owning screen's own consumed flag. This is a DIFFERENT
    /// question from "am I visible": a component that is showing something but had nothing to do with this
    /// frame's input returns false. An always-mounted component that returns a bare true silently starves
    /// every component below it, and then every screen below its screen.
    /// </returns>
    bool Update(float dt, bool receivesInput, Rect bounds, InputManager input);

    /// <summary>
    /// Per-frame draw, into a batch whose <c>Begin</c> is already active. Components draw in registration
    /// order, so the first added draws underneath. <paramref name="bounds"/> is the same region handed to
    /// <see cref="Update"/> that frame.
    /// </summary>
    /// <param name="batch">The batch to draw into, already begun by the owning screen.</param>
    /// <param name="bounds">The region this component lays out within, as passed to <see cref="Update"/>.</param>
    void Draw(SpriteBatch batch, Rect bounds);

    /// <summary>
    /// Acquire owned assets. Called once by <see cref="ScreenComponentList.Add{T}(T)"/>, mirroring
    /// <see cref="ScreenStack.Add"/> calling <see cref="Screen.LoadContent"/>. A default no-op, so a
    /// component with nothing to load omits it entirely.
    /// <para>
    /// Being a default interface member, an omitted implementation is callable only through an
    /// <see cref="IScreenComponent"/> reference, never through the concrete type. That costs nothing here
    /// because <see cref="ScreenComponentList"/> is the only caller and always calls through the interface.
    /// A component that DOES declare it gets it callable both ways as normal.
    /// </para>
    /// </summary>
    void LoadContent() { }

    /// <summary>
    /// Release owned assets. Called once by <see cref="ScreenComponentList.Remove"/> and
    /// <see cref="ScreenComponentList.Clear"/>, mirroring <see cref="ScreenStack.Remove"/> calling
    /// <see cref="Screen.UnloadContent"/>. A default no-op, with the same default-interface-member note as
    /// <see cref="LoadContent"/>.
    /// </summary>
    void UnloadContent() { }
}
