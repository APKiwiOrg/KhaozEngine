using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>How a fixed design space is mapped onto an arbitrary window.</summary>
    public enum ScaleMode
    {
        /// <summary>Keep aspect, scale to fit inside the window, centered. Adds letterbox/pillarbox bars. (Default.)</summary>
        Fit,
        /// <summary>Keep aspect, scale to cover the window, centered. Crops the overflow.</summary>
        Fill,
        /// <summary>Scale each axis independently to fill the window exactly. Distorts aspect.</summary>
        Stretch,
    }

    /// <summary>
    /// The design-space viewport a frame draws into: a fixed reference size (e.g. 960x540) plus the
    /// scale + letterbox offset that maps it onto the current window for a chosen <see cref="ScaleMode"/>.
    /// Drive it with <see cref="Update"/> each frame (or on resize); pass it to
    /// <c>SpriteBatch.Begin(IDesignViewport)</c> to draw in design coordinates, and to
    /// <c>Pointer.Update(InputState, IDesignViewport)</c> so hit-testing lines up under scaling. Pure math
    /// (no window or GPU dependency), so it is headless-testable.
    /// </summary>
    public sealed class DesignViewport : IDesignViewport
    {
        /// <summary>Reference design width (design-space pixels).</summary>
        public int Width { get; }
        /// <summary>Reference design height (design-space pixels).</summary>
        public int Height { get; }
        /// <summary>Current scaling mode; changing it takes effect on the next <see cref="Update"/>.</summary>
        public ScaleMode Mode { get; set; }

        public float ScaleX { get; private set; } = 1f;
        public float ScaleY { get; private set; } = 1f;
        public float OffsetX { get; private set; }
        public float OffsetY { get; private set; }

        public DesignViewport(int width, int height, ScaleMode mode = ScaleMode.Fit)
        {
            Width = width;
            Height = height;
            Mode = mode;
            Update(width, height);
        }

        /// <summary>Recompute scale + centering offset from the current window size. Ignores non-positive sizes.</summary>
        public void Update(int windowWidth, int windowHeight)
        {
            if (windowWidth <= 0 || windowHeight <= 0) return;

            float sx = (float)windowWidth / Width, sy = (float)windowHeight / Height;
            switch (Mode)
            {
                case ScaleMode.Stretch: ScaleX = sx; ScaleY = sy; break;
                case ScaleMode.Fill: ScaleX = ScaleY = MathF.Max(sx, sy); break;
                case ScaleMode.Fit:
                default: ScaleX = ScaleY = MathF.Min(sx, sy); break;
            }
            OffsetX = (windowWidth - Width * ScaleX) * 0.5f;
            OffsetY = (windowHeight - Height * ScaleY) * 0.5f;
        }

        /// <summary>Design rect covering the whole design space: (0, 0, Width, Height).</summary>
        public Rect DesignBounds => new(0, 0, Width, Height);

        /// <summary>The window-pixel rect the design space is drawn into (excludes letterbox bars).</summary>
        public Rect ContentBounds => new(OffsetX, OffsetY, Width * ScaleX, Height * ScaleY);

        /// <summary>
        /// The whole window mapped back into design space: <see cref="DesignBounds"/> plus the letterbox/pillarbox
        /// bars. Fill this (not <see cref="Width"/>/<see cref="Height"/>) for a full-window scrim/background under
        /// <see cref="ScaleMode.Fit"/>, so the bars do not show the screen below. Reduces to <see cref="DesignBounds"/>
        /// when there is no bar. Matches <see cref="IDesignViewport.WindowBounds"/> (the centred letterbox is symmetric).
        /// </summary>
        public Rect WindowBounds =>
            new(-OffsetX / ScaleX, -OffsetY / ScaleY,
                Width + 2f * OffsetX / ScaleX, Height + 2f * OffsetY / ScaleY);

        public Vector2 DesignToScreen(Vector2 design) =>
            new(design.X * ScaleX + OffsetX, design.Y * ScaleY + OffsetY);

        public Vector2 ScreenToDesign(Vector2 screen) =>
            new((screen.X - OffsetX) / ScaleX, (screen.Y - OffsetY) / ScaleY);

        /// <summary>
        /// Design-coordinates-to-clip-space transform for <c>SpriteBatch.Begin</c>. Folds the design scale +
        /// letterbox offset into the viewport's y-down ortho. Mirrors <c>Camera2D.GetViewProjection</c>;
        /// <paramref name="viewportWidth"/>/<paramref name="viewportHeight"/> are the current window size in points.
        /// </summary>
        public Matrix4x4 GetClipProjection(int viewportWidth, int viewportHeight)
        {
            var design = Matrix4x4.CreateScale(ScaleX, ScaleY, 1f)
                       * Matrix4x4.CreateTranslation(OffsetX, OffsetY, 0f);
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, viewportWidth, viewportHeight, 0, -1, 1);
            return design * ortho;
        }
    }
}
