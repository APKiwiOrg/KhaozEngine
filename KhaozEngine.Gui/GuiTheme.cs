using System.Numerics;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// The central semantic palette every retained widget reads for its default colours: a crisp, modern
    /// neutral-dark theme with a single blue accent, subtle 3px corners, and 1px hairline borders (no shadow /
    /// gradient / glow). Widgets capture <see cref="Default"/> at construction, so setting <see cref="Default"/>
    /// ONCE at startup rebrands the whole UI (change the accent, go light, etc.). <see cref="Legacy"/> reproduces
    /// the pre-10.11.0 flat blue-grey look for a one-line revert.
    /// </summary>
    public struct GuiTheme
    {
        /// <summary>App background (behind everything).</summary>
        public Vector4 Background;
        /// <summary>Resting control / panel fill.</summary>
        public Vector4 Surface;
        /// <summary>Control fill on hover.</summary>
        public Vector4 SurfaceHover;
        /// <summary>Control fill while pressed.</summary>
        public Vector4 SurfacePress;
        /// <summary>Control fill when disabled.</summary>
        public Vector4 SurfaceDisabled;
        /// <summary>Hairline border, resting.</summary>
        public Vector4 Border;
        /// <summary>Hairline border on hover.</summary>
        public Vector4 BorderHover;
        /// <summary>Hairline border when disabled.</summary>
        public Vector4 BorderDisabled;
        /// <summary>Primary accent (fills, active state).</summary>
        public Vector4 Accent;
        /// <summary>Bright accent (focus rings, active text, open-border highlight).</summary>
        public Vector4 AccentBright;
        /// <summary>Primary preset resting fill.</summary>
        public Vector4 PrimaryFill;
        /// <summary>Primary preset hover fill.</summary>
        public Vector4 PrimaryHover;
        /// <summary>Primary preset pressed fill.</summary>
        public Vector4 PrimaryPress;
        /// <summary>Primary preset border.</summary>
        public Vector4 PrimaryBorder;
        /// <summary>Primary preset selected fill.</summary>
        public Vector4 PrimarySelectedFill;
        /// <summary>Active preset resting fill.</summary>
        public Vector4 ActiveFill;
        /// <summary>Active preset hover fill.</summary>
        public Vector4 ActiveHover;
        /// <summary>Active preset pressed fill.</summary>
        public Vector4 ActivePress;
        /// <summary>Active preset border.</summary>
        public Vector4 ActiveBorder;
        /// <summary>Active preset text.</summary>
        public Vector4 ActiveText;
        /// <summary>Active preset selected fill.</summary>
        public Vector4 ActiveSelectedFill;
        /// <summary>
        /// Fill under a SELECTED row: the current item of a list, a dropdown's chosen option. An accent-muted
        /// surface rather than the accent itself, so a whole row of it sits under text without shouting.
        /// </summary>
        public Vector4 SelectionFill;
        /// <summary>
        /// Fill under the KEYBOARD CURSOR row, a shade under <see cref="SelectionFill"/> so a cursor sitting on
        /// the selected row still reads as two states rather than one.
        /// </summary>
        public Vector4 FocusFill;
        /// <summary>Default text.</summary>
        public Vector4 Text;
        /// <summary>Muted / secondary text.</summary>
        public Vector4 TextMuted;
        /// <summary>Disabled text.</summary>
        public Vector4 TextDisabled;
        /// <summary>Destructive accent.</summary>
        public Vector4 Danger;
        /// <summary>Bright destructive accent (danger text).</summary>
        public Vector4 DangerBright;
        /// <summary>Corner radius in draw units for the crisp look (0 = hard corners).</summary>
        public float CornerRadius;
        /// <summary>Border thickness in pixels.</summary>
        public float BorderThickness;

        // The four below are PROPERTIES over nullable backing fields rather than plain fields like everything
        // above, so an unset one falls back to a value DERIVED from the palette that is already there. They were
        // added after games were building GuiTheme values of their own by object initializer, and a plain field
        // would have defaulted those themes to transparent black, which draws as a hole. Setting one still works
        // exactly like setting a field, in an initializer or through `with`.
        Vector4? _borderShadow, _titleFill, _tabFill, _tabActiveFill;

        /// <summary>
        /// The dark hairline under a raised border, where a bevelled band meets the surface it frames (see
        /// <see cref="PanelFrame.DrawFrame"/>). Unset, it is <see cref="Border"/> at 55% brightness, alpha kept.
        /// </summary>
        public Vector4 BorderShadow
        {
            get => _borderShadow ?? Shade(Border, 0.55f);
            set => _borderShadow = value;
        }

        /// <summary>
        /// Fill behind a window's title row. Unset, it is <see cref="Surface"/>: a title row that is the body
        /// colour with a line of text on it, rather than a second frame inside the frame.
        /// </summary>
        public Vector4 TitleFill
        {
            get => _titleFill ?? Surface;
            set => _titleFill = value;
        }

        /// <summary>
        /// Fill behind an INACTIVE tab in a flat tab strip. Unset, it is <see cref="SurfaceHover"/>, one step off
        /// the panel body so a resting tab still reads as a tab.
        /// </summary>
        public Vector4 TabFill
        {
            get => _tabFill ?? SurfaceHover;
            set => _tabFill = value;
        }

        /// <summary>Fill behind the ACTIVE tab in a flat tab strip. Unset, it is <see cref="ActiveFill"/>.</summary>
        public Vector4 TabActiveFill
        {
            get => _tabActiveFill ?? ActiveFill;
            set => _tabActiveFill = value;
        }

        // Scale a colour's RGB toward black, keeping its alpha: the one derivation the fallbacks above need.
        static Vector4 Shade(Vector4 color, float factor) =>
            new(color.X * factor, color.Y * factor, color.Z * factor, color.W);

        /// <summary>
        /// The ambient theme new widgets read at construction. Defaults to <see cref="Crisp"/>. Set it once at
        /// startup (before building widgets) to rebrand the whole UI; changing it later does not restyle widgets
        /// already constructed. Set it to <see cref="Legacy"/> to keep the pre-10.11.0 look.
        /// </summary>
        public static GuiTheme Default { get; set; } = Crisp;

        /// <summary>The crisp neutral-dark palette with a blue accent (the 10.11.0 default look).</summary>
        public static GuiTheme Crisp => new()
        {
            Background = new(0.047f, 0.047f, 0.078f, 1f),   // #0c0c14
            Surface = new(0.098f, 0.098f, 0.137f, 1f),      // #191923
            SurfaceHover = new(0.137f, 0.137f, 0.196f, 1f), // #232332
            SurfacePress = new(0.071f, 0.071f, 0.098f, 1f), // #121219
            SurfaceDisabled = new(0.118f, 0.118f, 0.137f, 1f), // #1e1e23
            Border = new(0.176f, 0.176f, 0.216f, 1f),       // #2d2d37
            BorderHover = new(0.235f, 0.255f, 0.314f, 1f),  // #3c4150
            BorderDisabled = new(0.157f, 0.157f, 0.196f, 1f), // #282832
            Accent = new(0.157f, 0.431f, 0.706f, 1f),       // #286eb4
            AccentBright = new(0.392f, 0.784f, 1f, 1f),      // #64c8ff
            PrimaryFill = new(0.137f, 0.216f, 0.353f, 1f),
            PrimaryHover = new(0.176f, 0.275f, 0.451f, 1f),
            PrimaryPress = new(0.098f, 0.157f, 0.275f, 1f),
            PrimaryBorder = new(0.235f, 0.353f, 0.588f, 1f),
            PrimarySelectedFill = new(0.157f, 0.235f, 0.353f, 1f),
            ActiveFill = new(0.157f, 0.235f, 0.353f, 1f),
            ActiveHover = new(0.196f, 0.294f, 0.431f, 1f),
            ActivePress = new(0.118f, 0.176f, 0.275f, 1f),
            ActiveBorder = new(0.314f, 0.549f, 0.863f, 1f),
            ActiveText = new(0.549f, 0.784f, 1f, 1f),
            ActiveSelectedFill = new(0.196f, 0.294f, 0.431f, 1f),
            SelectionFill = new(0.157f, 0.235f, 0.353f, 1f), // #283c5a accent-muted
            FocusFill = new(0.137f, 0.216f, 0.353f, 1f),     // #23375a, one shade under the selection
            Text = new(0.95f, 0.96f, 0.98f, 1f),
            TextMuted = new(0.627f, 0.627f, 0.667f, 1f),    // #a0a0aa
            TextDisabled = new(0.275f, 0.275f, 0.314f, 1f), // #464650
            Danger = new(0.706f, 0.196f, 0.196f, 1f),       // #b43232
            DangerBright = new(1f, 0.392f, 0.392f, 1f),      // #ff6464
            CornerRadius = 3f,
            BorderThickness = 1f,
        };

        /// <summary>
        /// The pre-10.11.0 flat blue-grey palette. Assign <c>GuiTheme.Default = GuiTheme.Legacy;</c> at startup
        /// to keep the old aesthetic. A coherent approximation of the old per-widget defaults (the old widgets did
        /// not share one palette), so the look reverts but individual widgets are not guaranteed byte-identical;
        /// for a byte-exact old Button use <see cref="GuiStyle.Legacy"/>.
        /// </summary>
        public static GuiTheme Legacy => new()
        {
            Background = new(0.07f, 0.09f, 0.13f, 1f),
            Surface = new(0.12f, 0.13f, 0.17f, 1f),
            SurfaceHover = new(0.16f, 0.18f, 0.24f, 1f),
            SurfacePress = new(0.09f, 0.10f, 0.14f, 1f),
            SurfaceDisabled = new(0.10f, 0.10f, 0.12f, 1f),
            Border = new(0.22f, 0.24f, 0.30f, 1f),
            BorderHover = new(0.30f, 0.40f, 0.55f, 1f),
            BorderDisabled = new(0.18f, 0.18f, 0.22f, 1f),
            Accent = new(0.16f, 0.39f, 0.70f, 1f),
            AccentBright = new(0.39f, 0.70f, 1f, 1f),
            PrimaryFill = new(0.137f, 0.216f, 0.353f, 1f),
            PrimaryHover = new(0.176f, 0.275f, 0.451f, 1f),
            PrimaryPress = new(0.098f, 0.157f, 0.275f, 1f),
            PrimaryBorder = new(0.235f, 0.353f, 0.588f, 1f),
            PrimarySelectedFill = new(0.157f, 0.235f, 0.353f, 1f),
            ActiveFill = new(0.157f, 0.235f, 0.353f, 1f),
            ActiveHover = new(0.196f, 0.294f, 0.431f, 1f),
            ActivePress = new(0.118f, 0.176f, 0.275f, 1f),
            ActiveBorder = new(0.314f, 0.549f, 0.863f, 1f),
            ActiveText = new(0.549f, 0.784f, 1f, 1f),
            ActiveSelectedFill = new(0.196f, 0.294f, 0.431f, 1f),
            // The pre-theme widgets hardcoded these two, so the legacy palette carries the same pair as Crisp:
            // reverting the theme must not move a selected or cursored row.
            SelectionFill = new(0.157f, 0.235f, 0.353f, 1f),
            FocusFill = new(0.137f, 0.216f, 0.353f, 1f),
            Text = Vector4.One,
            TextMuted = new(0.78f, 0.80f, 0.84f, 1f),
            TextDisabled = new(0.5f, 0.5f, 0.55f, 1f),
            Danger = new(0.70f, 0.20f, 0.20f, 1f),
            DangerBright = new(1f, 0.4f, 0.4f, 1f),
            CornerRadius = 0f,
            BorderThickness = 1f,
        };
    }
}
