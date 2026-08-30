using System;
using KhaozEngine.App;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

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
        /// <summary>The (lazily resolved) button caption.</summary>
        public LocalizedText Content;
        public SpriteFont Font;
        public Action? OnClick;

        /// <summary>The palette driving the button's visual states; defaults to <see cref="GuiStyle.Default"/>.</summary>
        public GuiStyle Style = GuiStyle.Default;
        /// <summary>When false, the button draws disabled and never fires <see cref="OnClick"/> (still reserves its rect).</summary>
        public bool Enabled = true;
        /// <summary>When true, the button draws in its selected state.</summary>
        public bool Selected;

        /// <summary>
        /// Uniform scale for the caption glyphs and advances, applied about the button centre so the label
        /// stays centred. Defaults to <c>1f</c> (today's rendering, byte-for-byte). This scales the LABEL
        /// ONLY: <see cref="Bounds"/> and the press-origin hit-test are unchanged at any scale, so a compact
        /// button draws a smaller label inside the same rect. A scale large enough to overflow the rect is the
        /// caller's responsibility, exactly as for the immediate <see cref="GuiSurface.Button(SpriteFont, Rect, LocalizedText, GuiStyle, bool, bool, float)"/>.
        /// </summary>
        public float LabelScale = 1f;

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the button
        /// in / out with a host transition (a panel sliding away under it). Default 1 is a no-op. Mirrors
        /// <see cref="TabBar.Opacity"/>, and rides the same <see cref="GuiStyle.Faded"/> the tab bar uses, so the
        /// fill, the border, the label, the drop shadow and the hover glow all fade together.
        /// </summary>
        public float Opacity = 1f;

        bool _hover, _press;

        /// <summary>Create a button from localized text.</summary>
        public Button(Rect bounds, LocalizedText label, SpriteFont font, Action? onClick = null)
        {
            Bounds = bounds; Content = label; Font = font; OnClick = onClick;
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public Button(Rect bounds, string label, SpriteFont font, Action? onClick = null)
            : this(bounds, LocalizedText.Raw(label), font, onClick) { }

        /// <summary>Obsolete shim for the former string field.</summary>
        [Obsolete("Use Content (LocalizedText). Setting Label stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string Label
        {
            get => Content.Resolve();
            set => Content = LocalizedText.Raw(value);
        }

        /// <summary>The current resolved caption (for tests / measurement).</summary>
        public string Resolved => Content.Resolve();

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
            GuiDraw.DrawButton(batch, white, Font, Bounds, Content, Style.Faded(Opacity), Enabled, Selected, _hover, _press, LabelScale);
    }
}
