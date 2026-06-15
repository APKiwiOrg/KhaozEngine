using System.Numerics;

namespace KhaozEngine.Gui
{
    /// <summary>Horizontal text alignment within a box. Text is always vertically centered in the rect.</summary>
    public enum GuiAlign { Left, Center, Right }

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
        };
    }
}
