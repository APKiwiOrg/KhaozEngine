using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Title-bar drag for a free-floating window: where the player has dragged it to, as an OFFSET from wherever
    /// the window's own layout would have put it. Pair it with <see cref="PanelFrame"/>, whose
    /// <see cref="PanelFrame.TitleRect"/> less its <see cref="PanelFrame.CloseRect"/> is the usual grip.
    /// </summary>
    /// <remarks>
    /// An offset rather than an absolute position, so a window keeps its own placement rule (centred over the
    /// world, docked at its own top) and the drag is a correction on top of it. A resize then moves an undragged
    /// window the way it always did, and moves a dragged one with its layout instead of stranding it.
    /// <para>The GRAB is LATCHED on the press and held until the button goes up, wherever the cursor goes in
    /// between. It carries the press-origin invariant (a press that began outside the grip never grabs, even once
    /// the cursor wanders over it), but it reads that invariant ONCE, on the frame
    /// <see cref="Pointer.IsPressOriginFresh"/> reports the origin was latched, rather than every frame. Re-reading
    /// it every frame is what breaks the drag: the grip travels with the window while the press-origin stays where
    /// the button went down, so a window dragged further than its title row is tall moves the grip out from under
    /// its own press and drops the grab part way. A slider handle never shows this, because a slider handle does
    /// not move away from the cursor.</para>
    /// <para>A held drag CLAIMS the gesture through <see cref="Pointer.ConsumeGesture"/>, so nothing under the
    /// window sees the press: without that, a drag that wandered off the window and released over the world
    /// finishes as a world click.</para>
    /// <para>This is a value the window owns, not a widget: it draws nothing and reserves nothing on the pointer.
    /// Which rect is the grip, and what the window's natural bounds are, stay with the caller.</para>
    /// </remarks>
    public sealed class WindowDrag
    {
        Vector2 _offset;

        /// <summary>How far the window has been dragged from its own layout, in design units.</summary>
        public Vector2 Offset => _offset;

        /// <summary>Whether the player is holding the window right now.</summary>
        public bool Dragging { get; private set; }

        /// <summary>Read this frame's pointer against the grip and move the offset by whatever the cursor moved.
        /// Call once per frame, before the layers underneath hit-test.</summary>
        /// <param name="pointer">The pointer, in the space the window is placed and drawn in.</param>
        /// <param name="grip">The rect a drag may START in, normally the title row less its close button.</param>
        /// <remarks>It does not read <see cref="Pointer.IsBlocked"/>. Two overlapping windows that both update
        /// will both grab a press that lands in both grips, so which window is on top is the caller's to settle:
        /// update the top one first and skip the rest once the gesture is consumed.</remarks>
        public void Update(Pointer pointer, in Rect grip)
        {
            if (pointer is null || !pointer.IsDown) { Dragging = false; return; }
            // The grab, read on the press frame and only on it: IsPressOriginFresh is the frame the origin was
            // latched, so a grip that later slides under a press it did not start in cannot pick the window up.
            if (!Dragging)
            {
                if (!pointer.IsPressOriginFresh || !grip.Contains(pointer.PressOrigin)) return;
                Dragging = true;
            }
            _offset += pointer.Delta;
            pointer.ConsumeGesture();
        }

        /// <summary>Where the window actually sits: its own layout plus the drag, CLAMPED so the whole of it stays
        /// inside the viewport.</summary>
        /// <param name="natural">The rect the window's own layout asked for.</param>
        /// <param name="viewport">The design viewport, as width and height.</param>
        /// <returns>The rect to draw and hit-test the window at this frame, the same size as
        /// <paramref name="natural"/>.</returns>
        /// <remarks>The clamped offset is written BACK, which is the half worth naming: without it a drag off the
        /// right of the screen banks distance the window has not moved, and the player then has to drag all of it
        /// back before the window budges. A viewport smaller than the window clamps to zero rather than to a
        /// negative, so the window's top left corner stays reachable.</remarks>
        public Rect Place(in Rect natural, Vector2 viewport)
        {
            float x = Math.Clamp(natural.X + _offset.X, 0f, MathF.Max(0f, viewport.X - natural.Width));
            float y = Math.Clamp(natural.Y + _offset.Y, 0f, MathF.Max(0f, viewport.Y - natural.Height));
            _offset = new Vector2(x - natural.X, y - natural.Y);
            return new Rect(x, y, natural.Width, natural.Height);
        }

        /// <summary>Let go of the window without moving it, for a close: a window that shut under a held button
        /// must not still be carried when the next one opens. The offset survives, so a window reopens where it
        /// was put.</summary>
        public void Release() => Dragging = false;

        /// <summary>Put the window back where its own layout wants it, forgetting the drag entirely.</summary>
        public void Reset()
        {
            _offset = Vector2.Zero;
            Dragging = false;
        }
    }
}
