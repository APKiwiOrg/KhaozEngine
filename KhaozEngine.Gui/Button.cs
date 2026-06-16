using System;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A bounds-aware button over <see cref="Pointer"/>: clicks fire through the press-origin
    /// <see cref="Pointer.IsTapIn"/> invariant (a click that began elsewhere can't trigger it), with
    /// hover/press/disabled/selected visuals driven by <see cref="GuiStyle"/> (shared with the immediate
    /// <see cref="GuiSurface"/>). Call <see cref="Update"/> then <see cref="Draw"/> each frame; <see cref="Update"/>
    /// reserves the rect on the pointer (the click-through gate), so a layer beneath can check
    /// <see cref="Pointer.IsBlocked"/>.
    /// </summary>
    public sealed class Button
    {
        public Rect Bounds;
        public string Label;
        public SpriteFont Font;
        public Action? OnClick;

        /// <summary>The palette driving the button's visual states; defaults to <see cref="GuiStyle.Default"/>.</summary>
        public GuiStyle Style = GuiStyle.Default;
        /// <summary>When false, the button draws disabled and never fires <see cref="OnClick"/> (still reserves its rect).</summary>
        public bool Enabled = true;
        /// <summary>When true, the button draws in its selected state.</summary>
        public bool Selected;

        bool _hover, _press;

        public Button(Rect bounds, string label, SpriteFont font, Action? onClick = null)
        {
            Bounds = bounds; Label = label; Font = font; OnClick = onClick;
        }

        /// <summary>
        /// Reserve the rect for click-through (<see cref="Pointer.BlockRegion"/>) and hit-test against the pointer.
        /// Fires <see cref="OnClick"/> and returns true only on a valid press-origin tap AND when <see cref="Enabled"/>;
        /// a disabled button still reserves its rect but never fires.
        /// </summary>
        public bool Update(Pointer pointer)
        {
            pointer.BlockRegion(Bounds);
            _hover = pointer.IsHoveringIn(Bounds);
            _press = pointer.IsPressingIn(Bounds);
            if (Enabled && pointer.IsTapIn(Bounds)) { OnClick?.Invoke(); return true; }
            return false;
        }

        /// <summary>Draw the button via the shared <see cref="GuiDraw.DrawButton"/>. <paramref name="white"/> is a
        /// 1x1 white texture for the fill.</summary>
        public void Draw(SpriteBatch batch, Texture2D white) =>
            GuiDraw.DrawButton(batch, white, Font, Bounds, Label, Style, Enabled, Selected, _hover, _press);
    }
}
