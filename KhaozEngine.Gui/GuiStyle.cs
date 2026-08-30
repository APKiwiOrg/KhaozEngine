using System.Numerics;

namespace KhaozEngine.Gui
{
    /// <summary>Horizontal text alignment within a box. Text is always vertically centered in the rect.</summary>
    public enum GuiAlign { Left, Center, Right }

    /// <summary>How a widget body is filled. <see cref="Solid"/> is the flat default.</summary>
    public enum GuiFill { Solid, VerticalGradient }

    /// <summary>
    /// A palette of colors driving an immediate-mode <see cref="GuiSurface"/> widget's visual states. Use
    /// <see cref="Default"/> for a sensible blue-grey look matching the retained <see cref="Button"/> defaults,
    /// or build your own.
    /// </summary>
    public struct GuiStyle
    {
        /// <summary>Resting fill.</summary>
        public Vector4 Fill;
        /// <summary>Fill while the pointer hovers (not pressing).</summary>
        public Vector4 Hover;
        /// <summary>Fill while the pointer is pressing inside.</summary>
        public Vector4 Press;
        /// <summary>Outline color. The fallback for every unset per-state border tint below.</summary>
        public Vector4 Border;
        /// <summary>Text color. The fallback for every unset per-state text tint below.</summary>
        public Vector4 Text;
        /// <summary>Fill when disabled.</summary>
        public Vector4 DisabledFill;
        /// <summary>Text color when disabled.</summary>
        public Vector4 DisabledText;
        /// <summary>Fill when selected.</summary>
        public Vector4 SelectedFill;
        /// <summary>Outline color when selected.</summary>
        public Vector4 SelectedBorder;

        /// <summary>Outline color while the pointer hovers. <c>null</c> (default) falls back to <see cref="Border"/>.</summary>
        public Vector4? HoverBorder;
        /// <summary>Outline color while the pointer is pressing inside. <c>null</c> (default) falls back to <see cref="Border"/>.</summary>
        public Vector4? PressBorder;
        /// <summary>Outline color when disabled. <c>null</c> (default) falls back to <see cref="Border"/>.</summary>
        public Vector4? DisabledBorder;
        /// <summary>Text color while the pointer hovers. <c>null</c> (default) falls back to <see cref="Text"/>.</summary>
        public Vector4? HoverText;
        /// <summary>Text color while the pointer is pressing inside. <c>null</c> (default) falls back to <see cref="Text"/>.</summary>
        public Vector4? PressText;
        /// <summary>Outline thickness in pixels.</summary>
        public float BorderThickness;

        /// <summary>Corner radius in draw units. 0 (default) = hard corners (today's look).</summary>
        public float CornerRadius;
        /// <summary>Soft drop-shadow spread in draw units. 0 (default) = no shadow.</summary>
        public float ShadowSize;
        /// <summary>Drop-shadow colour (default transparent).</summary>
        public Vector4 ShadowColor;
        /// <summary>Drop-shadow offset in draw units (default (0,0)).</summary>
        public Vector2 ShadowOffset;
        /// <summary>Body fill mode (default <see cref="GuiFill.Solid"/>).</summary>
        public GuiFill FillMode;
        /// <summary>Top-edge RGB multiplier of the active state colour when <see cref="GuiFill.VerticalGradient"/> (default 1).</summary>
        public float GradientTopScale;
        /// <summary>Bottom-edge RGB multiplier of the active state colour when <see cref="GuiFill.VerticalGradient"/> (default 1).</summary>
        public float GradientBottomScale;
        /// <summary>Hover-glow colour (default transparent).</summary>
        public Vector4 GlowColor;
        /// <summary>Hover-glow spread in draw units. 0 (default) = no glow.</summary>
        public float GlowSize;

        /// <summary>
        /// Optional sprite skin (nine-slice frame). Default <c>null</c> = flat GuiDraw primitives (today's rendering,
        /// byte-for-byte). When set, <see cref="GuiDraw.FillStyled"/> paints the skin instead of the flat fill /
        /// procedural border, with the resolved state colour multiplied over the texture as a tint (the shadow still
        /// draws). Applies family-wide since every widget fills through <see cref="GuiDraw.FillStyled"/>.
        /// </summary>
        public GuiSkin? Skin;

        /// <summary>
        /// True when every modern knob is at its off default AND no <see cref="Skin"/> is set, so <see cref="GuiDraw"/>
        /// takes the plain single-quad path that renders byte-identically to pre-7.7.0.
        /// </summary>
        public bool IsFlat =>
            CornerRadius == 0f && ShadowSize == 0f && FillMode == GuiFill.Solid && GlowSize == 0f && Skin == null;

        /// <summary>
        /// The outline colour for one interaction state. <paramref name="selected"/> wins outright (the pre-existing
        /// rule), then disabled, press and hover in that order, each falling back to <see cref="Border"/> when its own
        /// tint is unset. With every per-state tint left at <c>null</c> this is exactly
        /// <c>selected ? SelectedBorder : Border</c>, so a style that sets none renders unchanged. Pure, headless-testable.
        /// </summary>
        public Vector4 ResolveBorder(bool enabled, bool selected, bool hover, bool press) =>
            selected ? SelectedBorder
            : !enabled ? DisabledBorder ?? Border
            : press ? PressBorder ?? Border
            : hover ? HoverBorder ?? Border
            : Border;

        /// <summary>
        /// The text colour for one interaction state. Disabled wins outright (the pre-existing rule), then press and
        /// hover, each falling back to <see cref="Text"/> when its own tint is unset. With every per-state tint left at
        /// <c>null</c> this is exactly <c>enabled ? Text : DisabledText</c>. Pure, headless-testable.
        /// </summary>
        public Vector4 ResolveText(bool enabled, bool hover, bool press) =>
            !enabled ? DisabledText
            : press ? PressText ?? Text
            : hover ? HoverText ?? Text
            : Text;

        /// <summary>
        /// A copy of this style with every colour's alpha scaled by <paramref name="opacity"/>, so a whole widget
        /// fades uniformly with a host transition. An <paramref name="opacity"/> of 1 or more returns the style
        /// unchanged (identity, not a rebuilt copy), which is what keeps an un-faded widget byte-identical. Covers the
        /// per-state tints (each only when it is set, so an unset tint stays unset and keeps falling back) and the
        /// shadow / glow colours, so a modern style at <c>0</c> really does paint nothing. This is the shared fade
        /// behind <see cref="Button.Opacity"/> and <see cref="TabBar.Opacity"/>, and is public so a consumer's own
        /// widget can fade a <see cref="GuiStyle"/> the same way instead of re-deriving the field list.
        /// </summary>
        public GuiStyle Faded(float opacity)
        {
            if (opacity >= 1f) return this;
            GuiStyle s = this;
            s.Fill = GuiDraw.WithOpacity(s.Fill, opacity);
            s.Hover = GuiDraw.WithOpacity(s.Hover, opacity);
            s.Press = GuiDraw.WithOpacity(s.Press, opacity);
            s.Border = GuiDraw.WithOpacity(s.Border, opacity);
            s.Text = GuiDraw.WithOpacity(s.Text, opacity);
            s.DisabledFill = GuiDraw.WithOpacity(s.DisabledFill, opacity);
            s.DisabledText = GuiDraw.WithOpacity(s.DisabledText, opacity);
            s.SelectedFill = GuiDraw.WithOpacity(s.SelectedFill, opacity);
            s.SelectedBorder = GuiDraw.WithOpacity(s.SelectedBorder, opacity);
            if (s.HoverBorder is { } hb) s.HoverBorder = GuiDraw.WithOpacity(hb, opacity);
            if (s.PressBorder is { } pb) s.PressBorder = GuiDraw.WithOpacity(pb, opacity);
            if (s.DisabledBorder is { } db) s.DisabledBorder = GuiDraw.WithOpacity(db, opacity);
            if (s.HoverText is { } ht) s.HoverText = GuiDraw.WithOpacity(ht, opacity);
            if (s.PressText is { } pt) s.PressText = GuiDraw.WithOpacity(pt, opacity);
            s.ShadowColor = GuiDraw.WithOpacity(s.ShadowColor, opacity);
            s.GlowColor = GuiDraw.WithOpacity(s.GlowColor, opacity);
            return s;
        }

        /// <summary>
        /// The frame insets interior content must clear for a widget drawn at <paramref name="bounds"/>, as
        /// (X=left, Y=top, Z=right, W=bottom). The shared seam for every widget's interior-content math: when
        /// <see cref="Skin"/> is set the skin owns the frame, so these are the skin's destination insets
        /// (<see cref="GuiSkin.DestinationInsets"/>, exactly what the nine-slice paints, including the clamp when
        /// the widget is smaller than two opposing corners). With no skin they are the uniform
        /// <see cref="BorderThickness"/> (clamped at 0), matching today's flat-border behaviour byte-for-byte.
        /// Pure geometry, headless-testable.
        /// </summary>
        public Vector4 ContentInsets(Primitives.Rect bounds)
        {
            if (Skin is { } skin) return skin.DestinationInsets(bounds);
            float bt = BorderThickness > 0f ? BorderThickness : 0f;
            return new Vector4(bt, bt, bt, bt);
        }

        /// <summary>
        /// <paramref name="bounds"/> shrunk by <see cref="ContentInsets"/> (width/height clamped at 0): the
        /// interior rect content may occupy without overpainting the frame (flat border or nine-slice skin).
        /// </summary>
        public Primitives.Rect ContentRect(Primitives.Rect bounds)
        {
            Vector4 i = ContentInsets(bounds);
            return new Primitives.Rect(
                bounds.X + i.X, bounds.Y + i.Y,
                System.MathF.Max(0f, bounds.Width - i.X - i.Z),
                System.MathF.Max(0f, bounds.Height - i.Y - i.W));
        }

        /// <summary>
        /// The crisp default button palette, derived from <see cref="GuiTheme.Default"/>: an accent-tinted fill,
        /// a 1px hairline border, and a subtle 3px corner radius, with no shadow/gradient/glow. This is the new
        /// out-of-box look (10.11.0) and equals <see cref="Primary"/>. For the pre-10.11.0 flat blue-grey button
        /// use <see cref="Legacy"/>.
        /// </summary>
        public static GuiStyle Default => Primary;

        /// <summary>The accent (primary) button: the crisp default. Fill/border are accent-tinted from the theme.</summary>
        public static GuiStyle Primary
        {
            get
            {
                var t = GuiTheme.Default;
                return new()
                {
                    Fill = new Vector4(0.137f, 0.216f, 0.353f, 1f),   // #233a5a accent-muted
                    Hover = new Vector4(0.176f, 0.275f, 0.451f, 1f),  // #2d4673
                    Press = new Vector4(0.098f, 0.157f, 0.275f, 1f),  // #192846
                    Border = new Vector4(0.235f, 0.353f, 0.588f, 1f), // #3c5a96
                    Text = t.Text,
                    DisabledFill = t.SurfaceDisabled,
                    DisabledText = t.TextDisabled,
                    SelectedFill = new Vector4(0.157f, 0.235f, 0.353f, 1f), // #283c5a (Active fill)
                    SelectedBorder = t.AccentBright,
                    BorderThickness = t.BorderThickness,
                    CornerRadius = t.CornerRadius,
                    FillMode = GuiFill.Solid,
                    GradientTopScale = 1f,
                    GradientBottomScale = 1f,
                };
            }
        }

        /// <summary>The muted secondary button: plain surface fill, hairline border, muted text.</summary>
        public static GuiStyle Secondary
        {
            get
            {
                var t = GuiTheme.Default;
                return new()
                {
                    Fill = t.Surface,
                    Hover = t.SurfaceHover,
                    Press = t.SurfacePress,
                    Border = t.Border,
                    Text = t.TextMuted,
                    DisabledFill = t.SurfaceDisabled,
                    DisabledText = t.TextDisabled,
                    SelectedFill = t.SurfaceHover,
                    SelectedBorder = t.BorderHover,
                    BorderThickness = t.BorderThickness,
                    CornerRadius = t.CornerRadius,
                    FillMode = GuiFill.Solid,
                    GradientTopScale = 1f,
                    GradientBottomScale = 1f,
                };
            }
        }

        /// <summary>The destructive button: dark red fill, red border, bright-red text.</summary>
        public static GuiStyle Danger
        {
            get
            {
                var t = GuiTheme.Default;
                return new()
                {
                    Fill = new Vector4(0.235f, 0.078f, 0.078f, 1f),   // #3c1414
                    Hover = new Vector4(0.314f, 0.098f, 0.098f, 1f),  // #501919
                    Press = new Vector4(0.176f, 0.059f, 0.059f, 1f),  // #2d0f0f
                    Border = t.Danger,
                    Text = t.DangerBright,
                    DisabledFill = t.SurfaceDisabled,
                    DisabledText = t.TextDisabled,
                    SelectedFill = new Vector4(0.314f, 0.098f, 0.098f, 1f),
                    SelectedBorder = t.DangerBright,
                    BorderThickness = t.BorderThickness,
                    CornerRadius = t.CornerRadius,
                    FillMode = GuiFill.Solid,
                    GradientTopScale = 1f,
                    GradientBottomScale = 1f,
                };
            }
        }

        /// <summary>The active / selected tab button: accent-tinted fill, bright border and text.</summary>
        public static GuiStyle Active
        {
            get
            {
                var t = GuiTheme.Default;
                return new()
                {
                    Fill = new Vector4(0.157f, 0.235f, 0.353f, 1f),   // #283c5a
                    Hover = new Vector4(0.196f, 0.294f, 0.431f, 1f),
                    Press = new Vector4(0.118f, 0.176f, 0.275f, 1f),
                    Border = new Vector4(0.314f, 0.549f, 0.863f, 1f), // #508cdc
                    Text = new Vector4(0.549f, 0.784f, 1f, 1f),        // #8cc8ff
                    DisabledFill = t.SurfaceDisabled,
                    DisabledText = t.TextDisabled,
                    SelectedFill = new Vector4(0.196f, 0.294f, 0.431f, 1f),
                    SelectedBorder = t.AccentBright,
                    BorderThickness = t.BorderThickness,
                    CornerRadius = t.CornerRadius,
                    FillMode = GuiFill.Solid,
                    GradientTopScale = 1f,
                    GradientBottomScale = 1f,
                };
            }
        }

        /// <summary>
        /// The pre-10.11.0 flat blue-grey button palette (hard corners, 1.5px border, no bloom). Byte-exact for a
        /// Button via <c>button.Style = GuiStyle.Legacy;</c>. This is what <see cref="Default"/> used to be.
        /// </summary>
        public static GuiStyle Legacy => new()
        {
            Fill = new Vector4(0.18f, 0.30f, 0.42f, 1f),
            Hover = new Vector4(0.26f, 0.50f, 0.66f, 1f),
            Press = new Vector4(0.20f, 0.40f, 0.55f, 1f),
            Border = new Vector4(0.30f, 0.38f, 0.52f, 1f),
            Text = Vector4.One,
            DisabledFill = new Vector4(0.14f, 0.15f, 0.18f, 0.9f),
            DisabledText = new Vector4(0.5f, 0.5f, 0.55f, 1f),
            SelectedFill = new Vector4(0.28f, 0.46f, 0.66f, 1f),
            SelectedBorder = new Vector4(0.55f, 0.80f, 1f, 1f),
            BorderThickness = 1.5f,
            FillMode = GuiFill.Solid,
            GradientTopScale = 1f,
            GradientBottomScale = 1f,
        };

        /// <summary>
        /// The legacy palette with modern affordances switched on: rounded corners, a soft drop shadow, a subtle
        /// vertical gradient, and a hover glow. Opt in with <c>ui.Style = GuiStyle.Modern</c>; games tune the
        /// palette. Built from <see cref="Legacy"/> (not the crisp <see cref="Default"/>) so its look is stable.
        /// </summary>
        public static GuiStyle Modern
        {
            get
            {
                var s = Legacy;
                s.CornerRadius = 7f;
                // Shadow/glow are now body-edge blooms (GuiDraw.SoftRoundedQuad): coverage peaks at 0.5 on the
                // outline and fades to zero over the given size. Alphas are raised vs the old truncated-rim look
                // to compensate for that 0.5 peak factor while staying gentle.
                s.ShadowSize = 8f;
                s.ShadowColor = new Vector4(0f, 0f, 0f, 0.55f);
                s.ShadowOffset = new Vector2(0f, 3f);
                s.FillMode = GuiFill.VerticalGradient;
                s.GradientTopScale = 1.12f;
                s.GradientBottomScale = 0.85f;
                s.GlowColor = new Vector4(0.55f, 0.80f, 1f, 0.5f);
                s.GlowSize = 11f;
                return s;
            }
        }

        /// <summary>
        /// Multiply RGB by <paramref name="scale"/> (clamped to [0,1] per channel), keeping alpha. Pure. When using
        /// a scale &gt; 1 (e.g. <see cref="GradientTopScale"/>), keep source channels below roughly <c>1 / scale</c>
        /// or the brightened channels hard-clip at 1, which can shift hue. The default palette stays in range.
        /// </summary>
        public static Vector4 ScaleRgb(Vector4 c, float scale) => new Vector4(
            System.Math.Clamp(c.X * scale, 0f, 1f),
            System.Math.Clamp(c.Y * scale, 0f, 1f),
            System.Math.Clamp(c.Z * scale, 0f, 1f),
            c.W);
    }
}
