using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Construction options for a <see cref="GameApp"/>: window title/size, the fixed design space (0 size =
    /// 1:1 with the window), the design <see cref="ScaleMode"/>, the per-frame clear colour, and whether to
    /// stand up a 3D scene. Use <see cref="For"/> for sensible defaults, then tweak the fields you need.
    /// </summary>
    public struct GameAppOptions
    {
        /// <summary>Window title.</summary>
        public string Title;
        /// <summary>Window width in points.</summary>
        public int Width;
        /// <summary>Window height in points.</summary>
        public int Height;
        /// <summary>Design-space width; 0 uses <see cref="Width"/> (1:1 design space).</summary>
        public int DesignWidth;
        /// <summary>Design-space height; 0 uses <see cref="Height"/> (1:1 design space).</summary>
        public int DesignHeight;
        /// <summary>How the design space maps onto the window (default <see cref="ScaleMode.Fit"/>).</summary>
        public ScaleMode ScaleMode;
        /// <summary>Background colour cleared each frame (default dark).</summary>
        public Vector4 ClearColor;
        /// <summary>When true, build a <see cref="Render3D.Render3DSurface"/> + <see cref="Render3D.Scene3D"/>.</summary>
        public bool Enable3D;

        /// <summary>Resolved design width: <see cref="DesignWidth"/>, or <see cref="Width"/> when it is 0.</summary>
        internal int ResolvedDesignWidth => DesignWidth == 0 ? Width : DesignWidth;
        /// <summary>Resolved design height: <see cref="DesignHeight"/>, or <see cref="Height"/> when it is 0.</summary>
        internal int ResolvedDesignHeight => DesignHeight == 0 ? Height : DesignHeight;

        /// <summary>Sensible defaults: Fit scaling, 1:1 design space, dark clear colour, 2D only.</summary>
        public static GameAppOptions For(string title, int width, int height) => new()
        {
            Title = title,
            Width = width,
            Height = height,
            DesignWidth = 0,
            DesignHeight = 0,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Vector4(0.10f, 0.12f, 0.16f, 1f),
            Enable3D = false,
        };
    }
}
