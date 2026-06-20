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
        /// <summary>Outline color.</summary>
        public Vector4 Border;
        /// <summary>Text color.</summary>
        public Vector4 Text;
        /// <summary>Fill when disabled.</summary>
        public Vector4 DisabledFill;
        /// <summary>Text color when disabled.</summary>
        public Vector4 DisabledText;
        /// <summary>Fill when selected.</summary>
        public Vector4 SelectedFill;
        /// <summary>Outline color when selected.</summary>
        public Vector4 SelectedBorder;
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
        /// True when every modern knob is at its off default, so <see cref="GuiDraw"/> takes the plain
        /// single-quad path that renders byte-identically to pre-7.4.0.
        /// </summary>
        public bool IsFlat =>
            CornerRadius == 0f && ShadowSize == 0f && FillMode == GuiFill.Solid && GlowSize == 0f;

        /// <summary>The default blue-grey palette, matching the retained <see cref="Button"/> defaults.</summary>
        public static GuiStyle Default => new()
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
        /// The default palette with modern affordances switched on: rounded corners, a soft drop shadow, a subtle
        /// vertical gradient, and a hover glow. Opt in with <c>ui.Style = GuiStyle.Modern</c>; games tune the palette.
        /// </summary>
        public static GuiStyle Modern
        {
            get
            {
                var s = Default;
                s.CornerRadius = 7f;
                s.ShadowSize = 8f;
                s.ShadowColor = new Vector4(0f, 0f, 0f, 0.40f);
                s.ShadowOffset = new Vector2(0f, 3f);
                s.FillMode = GuiFill.VerticalGradient;
                s.GradientTopScale = 1.12f;
                s.GradientBottomScale = 0.85f;
                s.GlowColor = new Vector4(0.55f, 0.80f, 1f, 0.35f);
                s.GlowSize = 10f;
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
