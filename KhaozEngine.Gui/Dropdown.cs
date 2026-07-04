using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>A single option in a <see cref="Dropdown"/>.</summary>
    public readonly record struct DropdownOption(string Label, int Value);

    /// <summary>
    /// A selector over <see cref="Pointer"/>: the trigger shows the current option; a tap opens a list below it;
    /// tapping an option selects + closes; a release outside dismisses. Because the open list extends past the
    /// trigger, drawing is split: <see cref="Draw"/> renders the trigger (inside any clip), <see cref="DrawOverlay"/>
    /// renders the open list (call last / unclipped so it sits on top).
    /// </summary>
    public sealed class Dropdown
    {
        readonly List<DropdownOption> _options;

        /// <summary>Trigger button bounds; option rows render below it. Update before <see cref="Update"/> if it moves.</summary>
        public Rect TriggerBounds;
        public bool IsOpen { get; private set; }
        /// <summary>True on the frame the selection changed.</summary>
        public bool WasChanged { get; private set; }
        public int SelectedIndex { get; private set; }
        public int SelectedValue => _options[SelectedIndex].Value;
        public string SelectedLabel => _options[SelectedIndex].Label;
        public IReadOnlyList<DropdownOption> Options => _options;

        public Vector4 Background = new(0.10f, 0.10f, 0.14f, 1f);
        public Vector4 Border = new(0.18f, 0.18f, 0.22f, 1f);
        public Vector4 OpenBorder = new(0.31f, 0.55f, 0.86f, 1f);
        public Vector4 ListBackground = new(0.07f, 0.07f, 0.11f, 1f);
        public Vector4 HoverColor = new(0.11f, 0.13f, 0.18f, 1f);
        public Vector4 SelectedColor = new(0.14f, 0.20f, 0.29f, 1f);
        public Vector4 TextColor = new(0.78f, 0.80f, 0.84f, 1f);
        public Vector4 SelectedTextColor = new(0.55f, 0.78f, 1f, 1f);

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the trigger and the open list container; defaults
        /// to the flat <see cref="GuiStyle.Default"/> so the dropdown renders byte-identically to pre-7.8.0. The
        /// dropdown keeps its own colours; only the affordance knobs are read, and the option-row highlights stay
        /// flat. Set <c>Style = GuiStyle.Modern</c> to opt in.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>
        /// Opt-in: draw a chevron caret on the right of the trigger that points down when closed and up when open.
        /// Defaults to <c>false</c> so existing callers render byte-identically. Colour is <see cref="ChevronColor"/>.
        /// </summary>
        public bool ShowChevron = false;
        public Vector4 ChevronColor = new(0.47f, 0.49f, 0.55f, 1f);

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the whole
        /// dropdown in/out with a host transition (e.g. an <see cref="ScrollablePanel"/> sliding up). Default 1 is a no-op.
        /// </summary>
        public float Opacity = 1f;

        public Dropdown(IReadOnlyList<DropdownOption> options, Rect triggerBounds)
        {
            if (options == null || options.Count == 0) throw new ArgumentException("At least one option is required.", nameof(options));
            _options = new List<DropdownOption>(options);
            TriggerBounds = triggerBounds;
        }

        /// <summary>Select the option with this value; no-op if not found. Does not set <see cref="WasChanged"/>.</summary>
        public void SelectByValue(int value)
        {
            for (int i = 0; i < _options.Count; i++)
                if (_options[i].Value == value) { SelectedIndex = i; return; }
        }

        /// <summary>Hit-test open/close/select/dismiss. Returns true if the selection changed.</summary>
        public bool Update(Pointer pointer)
        {
            WasChanged = false;
            // Reserve the trigger (closed) or the whole expanded list (open) for click-through, so a layer
            // beneath can't be clicked through the dropdown.
            pointer.BlockRegion(IsOpen ? FullBounds() : TriggerBounds);
            if (!IsOpen)
            {
                if (pointer.IsTapIn(TriggerBounds)) IsOpen = true;
                return false;
            }

            if (pointer.IsTapIn(TriggerBounds)) { IsOpen = false; return false; }

            for (int i = 0; i < _options.Count; i++)
            {
                if (!pointer.IsTapIn(OptionBounds(i))) continue;
                if (SelectedIndex != i) { SelectedIndex = i; WasChanged = true; }
                IsOpen = false;
                return WasChanged;
            }

            if (pointer.IsReleasedOutside(FullBounds())) IsOpen = false;
            return false;
        }

        /// <summary>The bounds of option row <paramref name="i"/> in the open list.</summary>
        public Rect OptionBounds(int i) =>
            new(TriggerBounds.X, TriggerBounds.Bottom + i * TriggerBounds.Height, TriggerBounds.Width, TriggerBounds.Height);

        Rect FullBounds() =>
            new(TriggerBounds.X, TriggerBounds.Y, TriggerBounds.Width, TriggerBounds.Height * (1 + _options.Count));

        /// <summary>Draw the trigger (current label, and a chevron when <see cref="ShowChevron"/>). Safe to call inside a clip region.</summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
        {
            if (IsOpen) GuiDraw.HoverGlow(batch, white, TriggerBounds, Style);
            GuiDraw.FillStyled(batch, white, TriggerBounds, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(Background, Opacity), GuiDraw.WithOpacity(IsOpen ? OpenBorder : Border, Opacity));
            float ty = TriggerBounds.Y + (TriggerBounds.Height - font.LineHeight) * 0.5f;
            batch.DrawString(font, SelectedLabel, new Vector2(MathF.Floor(TriggerBounds.X + 6f), MathF.Floor(ty)),
                (Color)GuiDraw.WithOpacity(TextColor, Opacity));

            if (ShowChevron)
            {
                var center = new Vector2(TriggerBounds.Right - 12f, TriggerBounds.Y + TriggerBounds.Height * 0.5f);
                GuiDraw.Caret(batch, white, center, halfWidth: 4f, halfHeight: 2f, pointingUp: IsOpen,
                    thickness: 1.5f, GuiDraw.WithOpacity(ChevronColor, Opacity));
            }
        }

        /// <summary>Draw the open option list. Call last (unclipped) so it overlays other content.</summary>
        public void DrawOverlay(SpriteBatch batch, Texture2D white, SpriteFont font, Pointer pointer)
        {
            if (!IsOpen) return;
            var list = new Rect(TriggerBounds.X, TriggerBounds.Bottom, TriggerBounds.Width, TriggerBounds.Height * _options.Count);
            GuiDraw.FillStyled(batch, white, list, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(ListBackground, Opacity), GuiDraw.WithOpacity(Border, Opacity));

            for (int i = 0; i < _options.Count; i++)
            {
                Rect r = OptionBounds(i);
                bool selected = i == SelectedIndex;
                if (selected) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(SelectedColor, Opacity));
                else if (pointer.IsPointerIn(r)) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(HoverColor, Opacity));
                float ty = r.Y + (r.Height - font.LineHeight) * 0.5f;
                batch.DrawString(font, _options[i].Label, new Vector2(MathF.Floor(r.X + 6f), MathF.Floor(ty)),
                    (Color)GuiDraw.WithOpacity(selected ? SelectedTextColor : TextColor, Opacity));
            }
        }
    }
}
