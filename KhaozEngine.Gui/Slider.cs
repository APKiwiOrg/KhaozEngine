using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A horizontal slider over <see cref="Pointer"/>: <see cref="Bounds"/> is the interactive track. A press
    /// that begins inside the track starts a drag and jumps the value to the pointer; dragging keeps tracking
    /// (clamped 0..1) until release. A press that began elsewhere is ignored, like the click-through invariant.
    /// Call <see cref="Update"/> then <see cref="Draw"/> each frame.
    /// </summary>
    public sealed class Slider
    {
        public Rect Bounds;
        /// <summary>Current value, 0..1.</summary>
        public float Value;
        /// <summary>When false, the slider neither drags nor reports changes.</summary>
        public bool Enabled = true;

        public Vector4 TrackColor = new(0.12f, 0.12f, 0.16f, 1f);
        public Vector4 BorderColor = new(0.22f, 0.22f, 0.26f, 1f);
        public Vector4 FillColor = new(0.16f, 0.39f, 0.70f, 1f);
        public Vector4 ThumbColor = Vector4.One;
        public Vector4 ThumbDragColor = new(0.39f, 0.70f, 1f, 1f);
        public float ThumbWidth = 10f;

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the thumb; defaults to the flat
        /// <see cref="GuiStyle.Default"/> so the slider renders byte-identically to pre-7.8.0. The slider keeps its
        /// own per-element colours (<see cref="TrackColor"/>/<see cref="FillColor"/>/<see cref="ThumbColor"/>); the
        /// track and accent fill always stay flat and only the thumb takes the style (mirrors
        /// <c>GuiDraw.DrawSlider</c>). Set <c>Style = GuiStyle.Modern</c> to opt in.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        bool _dragging;

        public Slider(Rect bounds, float value = 0f) { Bounds = bounds; Value = Math.Clamp(value, 0f, 1f); }

        /// <summary>Hit-test against the pointer and update <see cref="Value"/>. Returns true if the value changed.</summary>
        public bool Update(Pointer pointer)
        {
            pointer.BlockRegion(Bounds); // reserve the track for click-through, even when disabled
            if (!Enabled) { _dragging = false; return false; }

            // Start a drag only if the press began inside the track (press-origin invariant), shared with
            // the immediate GuiSurface.Slider via Pointer.IsDragStartIn.
            if (pointer.IsDragStartIn(Bounds))
                _dragging = true;
            if (!pointer.IsDown)
                _dragging = false;

            if (!_dragging) return false;

            float t = Bounds.Width > 0f ? (pointer.Position.X - Bounds.X) / Bounds.Width : 0f;
            float newValue = Math.Clamp(t, 0f, 1f);
            if (newValue == Value) return false;
            Value = newValue;
            return true;
        }

        /// <summary>
        /// Adjust <see cref="Value"/> by <paramref name="delta"/> (clamped 0..1) for keyboard / gamepad control,
        /// where pointer dragging is not in play. No-op when disabled. Returns true if the value changed.
        /// </summary>
        public bool Nudge(float delta)
        {
            if (!Enabled || delta == 0f) return false;
            float newValue = Math.Clamp(Value + delta, 0f, 1f);
            if (newValue == Value) return false;
            Value = newValue;
            return true;
        }

        /// <summary>Draw the track, fill, and thumb. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            GuiDraw.Fill(batch, white, Bounds, TrackColor);
            GuiDraw.Border(batch, white, Bounds, 1f, BorderColor);

            float fillW = Bounds.Width * Value;
            if (fillW > 0f)
                GuiDraw.Fill(batch, white, new Rect(Bounds.X, Bounds.Y, fillW, Bounds.Height), FillColor);

            float thumbX = Bounds.X + fillW - ThumbWidth * 0.5f;
            var thumb = new Rect(thumbX, Bounds.Y - 3f, ThumbWidth, Bounds.Height + 6f);
            // Thumb is the one styled element; the thumb has no border today, so force BorderThickness 0 (the flat
            // default then collapses to the same single Fill - byte-identical). A modern style rounds/shadows it.
            if (_dragging) GuiDraw.HoverGlow(batch, white, thumb, Style);
            GuiDraw.FillStyled(batch, white, thumb, Style with { BorderThickness = 0f },
                _dragging ? ThumbDragColor : ThumbColor, default);
        }
    }
}
