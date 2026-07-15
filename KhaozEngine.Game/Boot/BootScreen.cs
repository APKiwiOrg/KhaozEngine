using System;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// The turn-key startup scene: it appears in the first frames after the window opens - rendered with only the
    /// engine-internal font + a 1x1 white texture, zero game assets - then runs a <see cref="BootPipeline"/> while a
    /// progress bar advances (update check + apply, server-status min-version gate, then the game's own loading
    /// steps). On success it replaces itself with the game's first scene (a factory the game provides). On failure it
    /// shows a localized error with retry / quit affordances. Push it as the FIRST scene from the game's
    /// <c>OnLoad</c>. Build it with <see cref="Create(Texture2D, DpiFont, BootOptions, System.Func{GameScene}, System.Action)"/>.
    /// <para>
    /// Honest latency note: process start and window creation still precede this scene. The instant-on guarantee is
    /// that NO game asset loading happens before the bar is on screen - the heavy work is deferred into the pipeline,
    /// which runs while this scene renders.
    /// </para>
    /// </summary>
    public sealed class BootScreen : GameScene
    {
        readonly Texture2D _white;
        // Exactly one of these is set. _dpiFont is the DPI-aware path (texel-crisp on HiDPI). _spriteFont is the
        // legacy fixed-atlas path kept for back-compat. OnDrawUi dispatches to the matching renderer overload.
        readonly DpiFont? _dpiFont;
        readonly SpriteFont? _spriteFont;
        readonly BootPipeline _pipeline;
        readonly BootScreenTheme _theme;
        readonly Func<GameScene> _firstScene;
        readonly Action? _onQuit;
        readonly bool _allowRetry;
        readonly bool _allowQuit;
        readonly GuiSurface _gui;
        readonly Pointer _fallbackPointer = new();

        float _elapsed;
        bool _handedOff;

        /// <summary>
        /// Construct a boot screen over a ready <paramref name="pipeline"/> with a DPI-aware font (the crisp path).
        /// Most games use <see cref="Create(Texture2D, DpiFont, BootOptions, Func{GameScene}, Action)"/> instead,
        /// which builds the pipeline from a <see cref="BootOptions"/>. <paramref name="white"/> and
        /// <paramref name="font"/> are created by the app from its <c>Surface2D</c> (a scene cannot reach the device).
        /// The font is a DPI-aware <see cref="DpiFont"/> (build it from the engine default face with
        /// <c>Surface2D.LoadDefaultDpiFont(pointSize, cacheSlots: 4)</c> - still zero game assets). The screen bakes
        /// each label at its exact device-pixel size so text stays crisp on HiDPI. <paramref name="firstScene"/> builds
        /// the game's first real scene on success. <paramref name="onQuit"/> is invoked by the quit affordance (wire
        /// it to the app's <c>Quit</c>).
        /// </summary>
        public BootScreen(
            Texture2D white,
            DpiFont font,
            BootPipeline pipeline,
            Func<GameScene> firstScene,
            BootScreenTheme? theme = null,
            Action? onQuit = null,
            bool allowRetry = true,
            bool allowQuit = true)
            : this(white, pipeline, firstScene, theme, onQuit, allowRetry, allowQuit)
        {
            _dpiFont = font ?? throw new ArgumentNullException(nameof(font));
        }

        /// <summary>
        /// Legacy overload taking a fixed <see cref="SpriteFont"/>. Kept for back-compat. Prefer the
        /// <see cref="BootScreen(Texture2D, DpiFont, BootPipeline, Func{GameScene}, BootScreenTheme, Action, bool, bool)"/>
        /// overload, whose atlas is baked at the device-pixel size so text is crisp on HiDPI (a fixed font is
        /// bilinear-resampled by the theme scales through the point-space pass).
        /// </summary>
        public BootScreen(
            Texture2D white,
            SpriteFont font,
            BootPipeline pipeline,
            Func<GameScene> firstScene,
            BootScreenTheme? theme = null,
            Action? onQuit = null,
            bool allowRetry = true,
            bool allowQuit = true)
            : this(white, pipeline, firstScene, theme, onQuit, allowRetry, allowQuit)
        {
            _spriteFont = font ?? throw new ArgumentNullException(nameof(font));
        }

        // Shared field wiring for both font overloads (the caller sets exactly one of _dpiFont / _spriteFont).
        BootScreen(
            Texture2D white,
            BootPipeline pipeline,
            Func<GameScene> firstScene,
            BootScreenTheme? theme,
            Action? onQuit,
            bool allowRetry,
            bool allowQuit)
        {
            _white = white ?? throw new ArgumentNullException(nameof(white));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _firstScene = firstScene ?? throw new ArgumentNullException(nameof(firstScene));
            _theme = theme ?? BootScreenTheme.Default;
            _onQuit = onQuit;
            _allowRetry = allowRetry;
            _allowQuit = allowQuit;
            _gui = new GuiSurface(_white);
        }

        /// <summary>
        /// Build a boot screen from <paramref name="options"/> with a DPI-aware font (the crisp path): assembles the
        /// pipeline (update -&gt; server-status -&gt; game steps) and wires the theme + failure affordances. The single
        /// call a game makes in <c>OnLoad</c> after creating <paramref name="white"/> / <paramref name="font"/> from
        /// its <c>Surface2D</c> (<c>Surface2D.LoadDefaultDpiFont(pointSize, cacheSlots: 4)</c>).
        /// </summary>
        public static BootScreen Create(
            Texture2D white,
            DpiFont font,
            BootOptions options,
            Func<GameScene> firstScene,
            Action? onQuit = null)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var pipeline = new BootPipeline(options.BuildSteps());
            return new BootScreen(white, font, pipeline, firstScene, options.Theme, onQuit,
                options.AllowRetryOnFailure, options.AllowQuitOnFailure);
        }

        /// <summary>Legacy overload taking a fixed <see cref="SpriteFont"/>. Prefer the <see cref="DpiFont"/> overload
        /// for crisp HiDPI text.</summary>
        public static BootScreen Create(
            Texture2D white,
            SpriteFont font,
            BootOptions options,
            Func<GameScene> firstScene,
            Action? onQuit = null)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var pipeline = new BootPipeline(options.BuildSteps());
            return new BootScreen(white, font, pipeline, firstScene, options.Theme, onQuit,
                options.AllowRetryOnFailure, options.AllowQuitOnFailure);
        }

        /// <summary>The pipeline this screen drives (for diagnostics / tests).</summary>
        public BootPipeline Pipeline => _pipeline;

        /// <inheritdoc />
        public override void OnEnter() => _pipeline.Start();

        /// <inheritdoc />
        public override void OnExit() => _pipeline.Cancel();

        /// <inheritdoc />
        public override void OnUpdate(float dt)
        {
            _elapsed += dt;
            _pipeline.Pump();

            BootState state = _pipeline.State;
            if (state == BootState.Completed && !_handedOff)
            {
                _handedOff = true;
                Manager?.Replace(_firstScene());
                return;
            }

            if (state == BootState.Failed && Manager is { } manager)
            {
                InputState input = manager.Input;
                if (_allowRetry && input.WasPressed(Key.Enter))
                    _pipeline.Retry();
                else if (_allowQuit && input.WasPressed(Key.Escape))
                    _onQuit?.Invoke();
            }
        }

        /// <inheritdoc />
        public override void OnDrawUi(SpriteBatch batch)
        {
            if (Manager is null) return;

            // Lay out in the point space of the batch the host began (the DPI-aware UiViewport), NOT the device-pixel
            // FrameWidth/FrameHeight: those are the framebuffer size (2x logical on Retina), which would push the
            // centered content off-screen through the point-to-device scale. The UiViewport carries the logical size
            // and the DPI scale the renderer bakes text at. Fall back to FrameWidth/Height (== logical at 1x) for a
            // host that draws the boot screen without a point-space viewport.
            UiViewport? ui = Manager.UiViewport;
            var bounds = ui is not null
                ? new Rect(0f, 0f, ui.Width, ui.Height)
                : new Rect(0f, 0f, Manager.FrameWidth, Manager.FrameHeight);
            float dpiScale = ui?.DpiScale ?? 1f;
            Pointer pointer = Manager.UiPointer ?? _fallbackPointer;

            _gui.Begin(batch, pointer);
            BootView snapshot = _pipeline.Snapshot();
            bool retryClicked, quitClicked;
            if (_dpiFont is not null)
                BootScreenRenderer.Draw(batch, _gui, _white, _dpiFont, dpiScale, bounds, snapshot, _theme,
                    _allowRetry, _allowQuit, _elapsed, out retryClicked, out quitClicked);
            else
                BootScreenRenderer.Draw(batch, _gui, _white, _spriteFont!, bounds, snapshot, _theme,
                    _allowRetry, _allowQuit, _elapsed, out retryClicked, out quitClicked);

            if (retryClicked) _pipeline.Retry();
            else if (quitClicked) _onQuit?.Invoke();
        }
    }
}
