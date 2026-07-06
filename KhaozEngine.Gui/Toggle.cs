using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A two-state switch over <see cref="Pointer"/>: a valid tap (press and release both inside
    /// <see cref="Bounds"/>, the click-through invariant) flips <see cref="IsOn"/> and fires
    /// <see cref="OnChanged"/>. Drawn as a track with a thumb that slides to the on/off side. Call
    /// <see cref="Update(Pointer)"/> then <see cref="Draw"/> each frame. Keyboard/gamepad control is opt-in via
    /// the <see cref="Update(InputManager, bool, PlayerIndex?)"/> overload (and the <see cref="Flip"/>/<see cref="Set"/> primitives).
    /// </summary>
    public sealed class Toggle
    {
        public Rect Bounds;
        public bool IsOn;
        /// <summary>When false, taps are ignored.</summary>
        public bool Enabled = true;
        public Action<bool>? OnChanged;

        public Vector4 OnColor = GuiTheme.Default.Accent;
        public Vector4 OffColor = GuiTheme.Default.Surface;
        public Vector4 BorderColor = GuiTheme.Default.AccentBright;
        public Vector4 OffBorderColor = GuiTheme.Default.Border;
        public Vector4 ThumbColor = Vector4.One;
        public Vector4 DisabledColor = GuiTheme.Default.SurfaceDisabled;

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

        /// <summary>
        /// Pointer hit-test (as <see cref="Update(Pointer)"/>) plus keyboard/gamepad control when
        /// <paramref name="focused"/>: menu-select (Enter/Space/A/Start) flips the switch, "select next"
        /// (Right/D-pad right) forces it on, "select previous" (Left/D-pad left) forces it off. Opt-in and
        /// additive - the pointer-only overload is unchanged, so existing callers are unaffected. Returns true
        /// if the state changed this frame by either path. <paramref name="player"/> scopes gamepad input
        /// (null = any player).
        /// </summary>
        public bool Update(InputManager input, bool focused, PlayerIndex? player = null)
        {
            bool changed = Update(input.Pointer);
            if (!focused || !Enabled) return changed;
            if (input.IsMenuSelect(player, out _)) { if (Flip()) changed = true; }
            else if (input.IsSelectNext(player)) { if (Set(true)) changed = true; }
            else if (input.IsSelectPrevious(player)) { if (Set(false)) changed = true; }
            return changed;
        }

        /// <summary>
        /// Flip <see cref="IsOn"/> for keyboard/gamepad control, independent of the pointer. No-op (returns
        /// false) when disabled. Fires <see cref="OnChanged"/> on a real change and returns whether it changed.
        /// The pure primitive behind the wired <see cref="Update(InputManager, bool, PlayerIndex?)"/>.
        /// </summary>
        public bool Flip()
        {
            if (!Enabled) return false;
            IsOn = !IsOn;
            OnChanged?.Invoke(IsOn);
            return true;
        }

        /// <summary>
        /// Set <see cref="IsOn"/> to <paramref name="value"/> explicitly (for keyboard/gamepad). No-op when
        /// disabled or already at <paramref name="value"/>. Fires <see cref="OnChanged"/> only on a real change
        /// and returns whether it changed.
        /// </summary>
        public bool Set(bool value)
        {
            if (!Enabled || IsOn == value) return false;
            IsOn = value;
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
