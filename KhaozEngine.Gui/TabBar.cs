using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A horizontal tab bar (segmented control) over <see cref="Pointer"/>: N evenly-split tabs within
    /// <see cref="Bounds"/>, exactly one active at a time. Clicking a tab makes it active and raises
    /// <see cref="ChangedThisFrame"/> for that one frame, so the caller swaps the panel body only on an actual
    /// change. Tabs are drawn through the shared <see cref="GuiDraw.DrawButton"/>, so hover/press feedback and
    /// theming match <see cref="Button"/>: the active tab uses <see cref="ActiveStyle"/> (the bright-accent
    /// <see cref="GuiStyle.Active"/> look), inactive tabs use the muted <see cref="InactiveStyle"/>
    /// (<see cref="GuiStyle.Secondary"/>). Labels are <see cref="LocalizedText"/> (localized copy or a raw escape
    /// hatch). Call <see cref="Update"/> then <see cref="Draw"/> each frame; <see cref="Update"/> reserves
    /// <see cref="Bounds"/> on the pointer (the click-through gate) so a layer beneath can check
    /// <see cref="Pointer.IsBlocked"/>.
    /// </summary>
    public sealed class TabBar
    {
        /// <summary>The bar rectangle the tabs are evenly split across; assign per frame from the panel layout.</summary>
        public Rect Bounds;

        /// <summary>The font the tab labels draw with (centred per tab).</summary>
        public SpriteFont? Font;

        /// <summary>The style for the active tab; defaults to the bright-accent <see cref="GuiStyle.Active"/> look.</summary>
        public GuiStyle ActiveStyle = GuiStyle.Active;

        /// <summary>The style for the inactive tabs; defaults to the muted <see cref="GuiStyle.Secondary"/> look.</summary>
        public GuiStyle InactiveStyle = GuiStyle.Secondary;

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the whole
        /// bar in/out with a host transition. Default 1 is a no-op. Mirrors <see cref="PopupPanel.Opacity"/>.
        /// </summary>
        public float Opacity = 1f;

        readonly LocalizedText[] _labels;
        int _activeIndex;
        int _hoverIndex = -1, _pressIndex = -1;

        /// <summary>The tab labels, in layout order (left to right).</summary>
        public IReadOnlyList<LocalizedText> Labels => _labels;

        /// <summary>The number of tabs.</summary>
        public int Count => _labels.Length;

        /// <summary>
        /// The active tab index. Settable so the caller can restore or persist the selection; setting it directly
        /// does NOT raise <see cref="ChangedThisFrame"/> (only an input tap does). Clamped to a valid index.
        /// </summary>
        public int ActiveIndex
        {
            get => _activeIndex;
            set => _activeIndex = Math.Clamp(value, 0, _labels.Length - 1);
        }

        /// <summary>True only on the frame the active tab changed via an input tap (never via the setter).</summary>
        public bool ChangedThisFrame { get; private set; }

        /// <summary>Create a tab bar from at least one localized label.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="tabLabels"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="tabLabels"/> is empty.</exception>
        public TabBar(IReadOnlyList<LocalizedText> tabLabels, SpriteFont? font = null, Rect bounds = default)
        {
            if (tabLabels == null) throw new ArgumentNullException(nameof(tabLabels));
            if (tabLabels.Count == 0) throw new ArgumentException("A TabBar needs at least one tab.", nameof(tabLabels));
            _labels = new LocalizedText[tabLabels.Count];
            for (int i = 0; i < tabLabels.Count; i++) _labels[i] = tabLabels[i];
            Font = font;
            Bounds = bounds;
        }

        /// <summary>
        /// The rectangle of tab <paramref name="index"/> within <see cref="Bounds"/>, evenly split. Uses fractional
        /// edges (<c>X + Width * i/N</c> .. <c>X + Width * (i+1)/N</c>) so tabs abut with no cumulative rounding gap
        /// and the last tab's right edge equals <see cref="Bounds"/>.Right exactly. Pure math: headless-testable.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside [0, Count).</exception>
        public Rect TabRect(int index)
        {
            if (index < 0 || index >= _labels.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            float left = Bounds.X + Bounds.Width * index / _labels.Length;
            float right = Bounds.X + Bounds.Width * (index + 1) / _labels.Length;
            return new Rect(left, Bounds.Y, right - left, Bounds.Height);
        }

        /// <summary>
        /// Reserve <see cref="Bounds"/> for click-through (<see cref="Pointer.BlockRegion"/>) and hit-test each tab.
        /// A valid press-origin tap (<see cref="Pointer.IsTapIn"/>) on a tab OTHER than the active one makes it
        /// active, sets <see cref="ChangedThisFrame"/>, and returns true. A tap on the already-active tab, or
        /// anywhere outside the bar, changes nothing and returns false.
        /// </summary>
        public bool Update(Pointer pointer)
        {
            ChangedThisFrame = false;
            pointer.BlockRegion(Bounds);
            _hoverIndex = _pressIndex = -1;
            bool changed = false;
            for (int i = 0; i < _labels.Length; i++)
            {
                Rect r = TabRect(i);
                if (pointer.IsHoveringIn(r)) _hoverIndex = i;
                if (pointer.IsPressingIn(r)) _pressIndex = i;
                if (pointer.IsTapIn(r) && i != _activeIndex)
                {
                    _activeIndex = i;
                    ChangedThisFrame = true;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Draw every tab via the shared <see cref="GuiDraw.DrawButton"/>. <paramref name="white"/> is a
        /// 1x1 white texture for the fill. Hover/press visuals are the ones cached by the last <see cref="Update"/>
        /// (matching <see cref="Button"/>). Requires <see cref="Font"/> (a no-op when unset).</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            if (Font == null) return;
            for (int i = 0; i < _labels.Length; i++)
            {
                Rect r = TabRect(i);
                bool active = i == _activeIndex;
                GuiStyle style = FadedStyle(active ? ActiveStyle : InactiveStyle, Opacity);
                GuiDraw.DrawButton(batch, white, Font, r, _labels[i], style,
                    enabled: true, selected: active, hover: _hoverIndex == i, press: _pressIndex == i);
            }
        }

        // A copy of the style with every colour's alpha scaled by opacity, so the whole bar fades uniformly.
        static GuiStyle FadedStyle(GuiStyle s, float opacity)
        {
            if (opacity >= 1f) return s;
            s.Fill = GuiDraw.WithOpacity(s.Fill, opacity);
            s.Hover = GuiDraw.WithOpacity(s.Hover, opacity);
            s.Press = GuiDraw.WithOpacity(s.Press, opacity);
            s.Border = GuiDraw.WithOpacity(s.Border, opacity);
            s.Text = GuiDraw.WithOpacity(s.Text, opacity);
            s.DisabledFill = GuiDraw.WithOpacity(s.DisabledFill, opacity);
            s.DisabledText = GuiDraw.WithOpacity(s.DisabledText, opacity);
            s.SelectedFill = GuiDraw.WithOpacity(s.SelectedFill, opacity);
            s.SelectedBorder = GuiDraw.WithOpacity(s.SelectedBorder, opacity);
            return s;
        }
    }
}
