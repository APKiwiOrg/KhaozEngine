using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A responsive <see cref="IDesignViewport"/>: the design <b>height</b> is fixed (so vertical layout and anchors
    /// stay constant) while the design <b>width</b> tracks the window's aspect ratio, with a uniform height-fit
    /// scale and <b>no letterbox</b>. The whole UI fills the window at any aspect instead of being pillarboxed;
    /// layout that is expressed relative to <see cref="Width"/>/<see cref="Height"/> (full-width bars, Width-relative
    /// grids, centered content) adapts automatically. Width never drops below the reference width, so a
    /// narrower-than-design window keeps the design's minimum rather than squishing.
    /// <para>
    /// Contrast with <see cref="DesignViewport"/>, which keeps a fixed reference size and letterboxes/pillarboxes
    /// to preserve it. Use <see cref="AdaptiveViewport"/> for a fixed-height design (e.g. mobile-portrait UI) that
    /// should fill a resizable desktop window edge-to-edge. Pure math (no window/GPU dependency) - headless-testable;
    /// drive it with <see cref="Update"/> each frame and pass it to <c>SpriteBatch.Begin(IDesignViewport)</c> /
    /// <c>Pointer.Update(InputState, IDesignViewport)</c> like any design viewport.
    /// </para>
    /// <para>
    /// <see cref="WithMinimumCanvas"/> opts into a second policy where both axes adapt. The scale is
    /// <c>min(framebufferHeight / referenceHeight * ScaleMultiplier, framebufferWidth / minimumWidth,
    /// framebufferHeight / minimumHeight)</c>, and <see cref="Width"/>/<see cref="Height"/> are the framebuffer divided
    /// by that scale, so the canvas never drops below the minimum and never extends past the window. The constructor
    /// keeps the original fixed-height policy.
    /// </para>
    /// <para>
    /// A minimum-canvas transform is representable when its scale is finite and positive and its visible
    /// <see cref="Width"/> and <see cref="Height"/> are each at most 16,777,216 (2^24), the range in which every
    /// integer design coordinate is exact as a float. <see cref="WithMinimumCanvas"/>, the
    /// <see cref="ScaleMultiplier"/> setter and <see cref="Update"/> validate the transform before publishing any
    /// state and throw <see cref="ArgumentOutOfRangeException"/> for one that is not, leaving the previous multiplier,
    /// cached framebuffer size and transform unchanged. Nothing is clamped.
    /// </para>
    /// </summary>
    public sealed class AdaptiveViewport : IDesignViewport
    {
        // The largest design extent whose every integer coordinate is exact as a float.
        const int MaxDesignExtent = 1 << 24;

        readonly int _referenceWidth;
        readonly int _referenceHeight;
        // Zero in the original fixed-height policy, positive in the minimum-canvas policy.
        readonly int _minimumWidth;
        readonly int _minimumHeight;
        float _scaleMultiplier = 1f;
        // The last positive framebuffer size, so a multiplier change recomputes the same transform Update would.
        int _framebufferWidth;
        int _framebufferHeight;

        /// <summary>Design-space height. Fixed at the reference height in the original policy. The visible height
        /// in the minimum-canvas policy.</summary>
        public int Height { get; private set; }

        /// <summary>Design-space width; recomputed from the window aspect each <see cref="Update"/> (floored at the
        /// reference width in the original policy, at the minimum width in the minimum-canvas policy).</summary>
        public int Width { get; private set; }

        public float ScaleX { get; private set; } = 1f;
        public float ScaleY { get; private set; } = 1f;

        /// <summary>Always 0 - the design fills the window width, so there is no horizontal letterbox.</summary>
        public float OffsetX => 0f;
        /// <summary>Always 0 - the design is height-fit, so there is no vertical letterbox.</summary>
        public float OffsetY => 0f;

        /// <summary>
        /// <paramref name="referenceWidth"/> is the design width at the design's own aspect (and the minimum width);
        /// <paramref name="referenceHeight"/> is the fixed design height.
        /// </summary>
        public AdaptiveViewport(int referenceWidth, int referenceHeight)
        {
            _referenceWidth = referenceWidth;
            _referenceHeight = referenceHeight;
            Height = referenceHeight;
            Width = referenceWidth;
            Update(referenceWidth, referenceHeight);
        }

        AdaptiveViewport(int referenceWidth, int referenceHeight, int minimumWidth, int minimumHeight,
            float scaleMultiplier)
        {
            _referenceWidth = referenceWidth;
            _referenceHeight = referenceHeight;
            _minimumWidth = minimumWidth;
            _minimumHeight = minimumHeight;
            // Every input shapes the initial canvas, so no single parameter is named.
            FitMinimumCanvas(referenceWidth, referenceHeight, scaleMultiplier, paramName: null);
        }

        /// <summary>
        /// Create a viewport with the minimum-canvas policy. <paramref name="referenceHeight"/> sets the base
        /// height-fit scale, <paramref name="minimumWidth"/> x <paramref name="minimumHeight"/> is the smallest design
        /// canvas the window may show, and <paramref name="scaleMultiplier"/> enlarges the UI until that minimum binds.
        /// The initial state fits the reference size. All sizes must be positive, the minimum sizes at most 2^24 and the
        /// multiplier positive and finite, and the initial transform must be representable (see the class remarks),
        /// otherwise <see cref="ArgumentOutOfRangeException"/> is thrown.
        /// </summary>
        public static AdaptiveViewport WithMinimumCanvas(int referenceWidth, int referenceHeight,
            int minimumWidth, int minimumHeight, float scaleMultiplier = 1f)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceHeight);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumHeight);
            // The visible canvas is never smaller than the minimum, so a larger minimum can never be representable.
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumWidth, MaxDesignExtent);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumHeight, MaxDesignExtent);
            ValidateMultiplier(scaleMultiplier);
            return new AdaptiveViewport(referenceWidth, referenceHeight, minimumWidth, minimumHeight, scaleMultiplier);
        }

        /// <summary>
        /// The UI size multiplier of the minimum-canvas policy, applied to the height-fit scale before the minimum
        /// canvas clamps it. Setting it recomputes the transform from the last framebuffer size at once, so drawing
        /// and hit testing agree before the next <see cref="Update"/>. Must be positive and finite, and the transform it
        /// gives at the cached framebuffer size must be representable (see the class remarks), otherwise
        /// <see cref="ArgumentOutOfRangeException"/> is thrown and nothing changes. Always 1 for the original policy,
        /// where setting it throws <see cref="InvalidOperationException"/>.
        /// </summary>
        public float ScaleMultiplier
        {
            get => _scaleMultiplier;
            set
            {
                if (_minimumWidth == 0)
                    throw new InvalidOperationException(
                        "ScaleMultiplier needs a viewport created by AdaptiveViewport.WithMinimumCanvas.");
                ValidateMultiplier(value);
                FitMinimumCanvas(_framebufferWidth, _framebufferHeight, value, nameof(ScaleMultiplier));
            }
        }

        /// <summary>Recompute the scale and the adaptive design size from the window size. Ignores non-positive sizes.
        /// In the minimum-canvas policy a size whose transform is not representable (see the class remarks) throws
        /// <see cref="ArgumentOutOfRangeException"/> and keeps the previous transform and cached size.</summary>
        public void Update(int windowWidth, int windowHeight)
        {
            if (windowWidth <= 0 || windowHeight <= 0) return;
            if (_minimumWidth > 0)
            {
                // Either axis can make the canvas unrepresentable, so no single parameter is named.
                FitMinimumCanvas(windowWidth, windowHeight, _scaleMultiplier, paramName: null);
                return;
            }
            float scale = windowHeight / (float)Height;
            ScaleX = ScaleY = scale;
            Width = Math.Max(_referenceWidth, (int)MathF.Round(windowWidth / scale));
        }

        // Computes the candidate transform, validates it and only then publishes it with its inputs, so a rejected
        // framebuffer size or multiplier leaves every member as it was.
        void FitMinimumCanvas(int framebufferWidth, int framebufferHeight, float multiplier, string? paramName)
        {
            float heightFit = framebufferHeight / (float)_referenceHeight * multiplier;
            float minimumFit = MathF.Min(framebufferWidth / (float)_minimumWidth,
                framebufferHeight / (float)_minimumHeight);
            float scale = MathF.Min(heightFit, minimumFit);
            // Validate the exact quotients that become Width and Height. An underflowed or tiny scale makes them
            // infinite or huge, which the int cast would otherwise saturate.
            float width = framebufferWidth / scale;
            float height = framebufferHeight / scale;
            if (!float.IsFinite(scale) || scale <= 0f || !(width <= MaxDesignExtent) || !(height <= MaxDesignExtent))
                throw new ArgumentOutOfRangeException(paramName,
                    $"A {framebufferWidth}x{framebufferHeight} framebuffer at scale multiplier {multiplier} gives "
                    + $"scale {scale} and a {width}x{height} design canvas. The scale must be finite and positive "
                    + $"and each design extent at most {MaxDesignExtent}.");

            _framebufferWidth = framebufferWidth;
            _framebufferHeight = framebufferHeight;
            _scaleMultiplier = multiplier;
            ScaleX = ScaleY = scale;
            Width = VisibleExtent(width);
            Height = VisibleExtent(height);
        }

        // Round down so a control anchored at the far edge stays inside the framebuffer. The small tolerance keeps an
        // exact quotient such as 1280 / (4 / 3) from losing a unit to float error.
        static int VisibleExtent(float points) => (int)MathF.Floor(points + 1e-3f);

        static void ValidateMultiplier(float multiplier)
        {
            if (!float.IsFinite(multiplier) || multiplier <= 0f)
                throw new ArgumentOutOfRangeException(nameof(ScaleMultiplier), multiplier,
                    "The scale multiplier must be positive and finite.");
        }

        /// <summary>Design rect covering the whole (current) design space: (0, 0, Width, Height).</summary>
        public Rect DesignBounds => new(0, 0, Width, Height);

        /// <summary>The window-pixel rect the design space is drawn into (the whole window; no letterbox bars).</summary>
        public Rect ContentBounds => new(0, 0, Width * ScaleX, Height * ScaleY);

        /// <summary>Equals <see cref="DesignBounds"/>: the design fills the window edge-to-edge, so there is no bar to cover.</summary>
        public Rect WindowBounds => DesignBounds;

        public Vector2 DesignToScreen(Vector2 design) => new(design.X * ScaleX, design.Y * ScaleY);
        public Vector2 ScreenToDesign(Vector2 screen) => new(screen.X / ScaleX, screen.Y / ScaleY);

        /// <summary>
        /// Design-coordinates-to-clip-space transform for <c>SpriteBatch.Begin</c>: the uniform scale folded into a
        /// y-down ortho (no letterbox offset). Mirrors <see cref="DesignViewport.GetClipProjection"/>.
        /// </summary>
        public Matrix4x4 GetClipProjection(int viewportWidth, int viewportHeight)
        {
            var design = Matrix4x4.CreateScale(ScaleX, ScaleY, 1f);
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, viewportWidth, viewportHeight, 0, -1, 1);
            return design * ortho;
        }
    }
}
