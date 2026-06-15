using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A vertically-scrolling list region over <see cref="Pointer"/>: the wheel (while hovering) and dragging
    /// inside scroll a fixed-height item list, clamped to range. The owner draws each item itself, positioned via
    /// <see cref="ItemBounds"/>, between <see cref="BeginClip"/> and <see cref="EndClip"/> (which set/clear the
    /// SpriteBatch scissor so rows are clipped to <see cref="Bounds"/>). Hit-test rows with
    /// <see cref="TappedItemIndex"/>. Ported from the 4.x <c>UI.ScrollablePanel</c> (game nav-bar/VirtualResolution
    /// coupling dropped; clipping now via the engine's <see cref="SpriteBatch"/> scissor instead of MonoGame's).
    /// </summary>
    public sealed class ScrollablePanel
    {
        public Rect Bounds;
        public int ItemCount;
        public float ItemHeight = 40f;
        public float ItemSpacing = 4f;
        /// <summary>Pixels scrolled per wheel notch.</summary>
        public float WheelSpeed = 30f;
        /// <summary>When true, <see cref="Update"/> reserves <see cref="Bounds"/> on the pointer (so layers beneath skip it).</summary>
        public bool BlocksPointer = true;

        public Vector4 Background = new(0.047f, 0.047f, 0.078f, 0.96f);
        public Vector4 Border = new(0.24f, 0.24f, 0.31f, 1f);

        public float ScrollOffset { get; private set; }
        public float Stride => ItemHeight + ItemSpacing;
        public float ContentHeight => ItemCount * Stride;
        public float MaxScroll => MathF.Max(0f, ContentHeight - Bounds.Height);

        public ScrollablePanel(Rect bounds) { Bounds = bounds; }

        /// <summary>Jump to a scroll offset (clamped to range).</summary>
        public void ScrollTo(float offset) => ScrollOffset = Math.Clamp(offset, 0f, MaxScroll);

        /// <summary>Apply wheel + drag scrolling for this frame and (optionally) reserve the pointer region.</summary>
        public void Update(Pointer pointer, InputState input)
        {
            if (BlocksPointer) pointer.BlockRegion(Bounds);

            if (pointer.IsPointerIn(Bounds) && input.ScrollDelta != 0f)
                ScrollOffset -= input.ScrollDelta * WheelSpeed;

            float dragY = pointer.GetDragDelta(Bounds).Y;
            if (dragY != 0f)
                ScrollOffset -= dragY;

            ScrollOffset = Math.Clamp(ScrollOffset, 0f, MaxScroll);
        }

        /// <summary>The on-screen bounds of item <paramref name="index"/> (accounting for scroll). May lie outside <see cref="Bounds"/>.</summary>
        public Rect ItemBounds(int index) =>
            new(Bounds.X, Bounds.Y + index * Stride - ScrollOffset, Bounds.Width, ItemHeight);

        /// <summary>The item index under a tap (release inside the panel and on a row, not a gap), or -1.</summary>
        public int TappedItemIndex(Pointer pointer)
        {
            if (!pointer.IsTapIn(Bounds)) return -1;
            float rel = pointer.Position.Y - Bounds.Y + ScrollOffset;
            int idx = (int)(rel / Stride);
            if (idx < 0 || idx >= ItemCount) return -1;
            float top = idx * Stride;
            if (rel < top || rel > top + ItemHeight) return -1;   // in the spacing gap
            return idx;
        }

        /// <summary>Draw the panel background + border. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void DrawBackground(SpriteBatch batch, Texture2D white)
        {
            GuiDraw.Fill(batch, white, Bounds, Background);
            GuiDraw.Border(batch, white, Bounds, 1f, Border);
        }

        /// <summary>Flush the current batch and clip subsequent draws to <see cref="Bounds"/>. Draw items, then call <see cref="EndClip"/>.</summary>
        public void BeginClip(SpriteBatch batch)
        {
            batch.End();
            batch.SetScissor(Bounds);
            batch.Begin();
        }

        /// <summary>Flush the clipped draws and restore the full viewport.</summary>
        public void EndClip(SpriteBatch batch)
        {
            batch.End();
            batch.ClearScissor();
            batch.Begin();
        }
    }
}
