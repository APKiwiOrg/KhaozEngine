using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A two-state switch over <see cref="Pointer"/>: a valid tap (press and release both inside
    /// <see cref="Bounds"/>, the click-through invariant) flips <see cref="IsOn"/> and fires
    /// <see cref="OnChanged"/>. Drawn as a track with a thumb that slides to the on/off side. Call
    /// <see cref="Update"/> then <see cref="Draw"/> each frame. Ported from the 4.x <c>UI.Toggle</c>.
    /// </summary>
    public sealed class Toggle
    {
        public Rect Bounds;
        public bool IsOn;
        /// <summary>When false, taps are ignored.</summary>
        public bool Enabled = true;
        public Action<bool>? OnChanged;

        public Vector4 OnColor = new(0.16f, 0.39f, 0.70f, 1f);
        public Vector4 OffColor = new(0.16f, 0.16f, 0.20f, 1f);
        public Vector4 BorderColor = new(0.24f, 0.51f, 0.86f, 1f);
        public Vector4 OffBorderColor = new(0.22f, 0.22f, 0.26f, 1f);
        public Vector4 ThumbColor = Vector4.One;
        public Vector4 DisabledColor = new(0.10f, 0.10f, 0.12f, 1f);

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the track and thumb; defaults to the flat
        /// <see cref="GuiStyle.Default"/> so the toggle renders byte-identically to pre-7.8.0. The toggle keeps its
        /// own per-state colours (<see cref="OnColor"/>/<see cref="OffColor"/>/<see cref="ThumbColor"/> etc.); only
        /// the affordance knobs are read. Set <c>Style = GuiStyle.Modern</c> for a rounded pill.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        public Toggle(Rect bounds, bool isOn = false, Action<bool>? onChanged = null)
        {
            Bounds = bounds; IsOn = isOn; OnChanged = onChanged;
        }

        /// <summary>Hit-test against the pointer; flips on a valid tap. Returns true if the state changed.
        /// Always reserves its rect on the pointer (the click-through gate) - even when disabled - so a layer
        /// beneath can check <see cref="Pointer.IsBlocked"/>, matching the retained <see cref="Button"/>.</summary>
        public bool Update(Pointer pointer)
        {
            pointer.BlockRegion(Bounds);
            if (!Enabled) return false;
            if (!pointer.IsTapIn(Bounds)) return false;
            IsOn = !IsOn;
            OnChanged?.Invoke(IsOn);
            return true;
        }

        /// <summary>Draw the track + thumb. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            Vector4 track = !Enabled ? DisabledColor : IsOn ? OnColor : OffColor;
            Vector4 border = !Enabled ? OffBorderColor : IsOn ? BorderColor : OffBorderColor;
            // Track keeps its 1px border; the thumb has none (BorderThickness 0). Both flat by default
            // (byte-identical), rounded into a pill when a modern style is set.
            if (IsOn && Enabled) GuiDraw.HoverGlow(batch, white, Bounds, Style);
            GuiDraw.FillStyled(batch, white, Bounds, Style with { BorderThickness = 1f }, track, border);

            float pad = 2f;
            float thumbSize = Bounds.Height - pad * 2f;
            float thumbX = IsOn ? Bounds.Right - thumbSize - pad : Bounds.X + pad;
            GuiDraw.FillStyled(batch, white, new Rect(thumbX, Bounds.Y + pad, thumbSize, thumbSize),
                Style with { BorderThickness = 0f }, ThumbColor, default);
        }
    }
}
