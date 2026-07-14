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
    /// <c>OnLoad</c>. Build it with <see cref="Create"/>.
    /// <para>
    /// Honest latency note: process start and window creation still precede this scene. The instant-on guarantee is
    /// that NO game asset loading happens before the bar is on screen - the heavy work is deferred into the pipeline,
    /// which runs while this scene renders.
    /// </para>
    /// </summary>
    public sealed class BootScreen : GameScene
    {
        readonly Texture2D _white;
        readonly SpriteFont _font;
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
        /// Construct a boot screen over a ready <paramref name="pipeline"/>. Most games use <see cref="Create"/>
        /// instead, which builds the pipeline from a <see cref="BootOptions"/>. <paramref name="white"/> and
        /// <paramref name="font"/> are created by the app from its <c>Surface2D</c> (a scene cannot reach the device).
        /// The font may be the engine default (<c>Surface2D.LoadDefaultFont</c>). <paramref name="firstScene"/> builds
        /// the game's first real scene on success. <paramref name="onQuit"/> is invoked by the quit affordance (wire
        /// it to the app's <c>Quit</c>).
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
        {
            _white = white ?? throw new ArgumentNullException(nameof(white));
            _font = font ?? throw new ArgumentNullException(nameof(font));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _firstScene = firstScene ?? throw new ArgumentNullException(nameof(firstScene));
            _theme = theme ?? BootScreenTheme.Default;
            _onQuit = onQuit;
            _allowRetry = allowRetry;
            _allowQuit = allowQuit;
            _gui = new GuiSurface(_white);
        }

        /// <summary>
        /// Build a boot screen from <paramref name="options"/>: assembles the pipeline (update -&gt; server-status -&gt;
        /// game steps) and wires the theme + failure affordances. The single call a game makes in <c>OnLoad</c> after
        /// creating <paramref name="white"/> / <paramref name="font"/> from its <c>Surface2D</c>.
        /// </summary>
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
            var bounds = new Rect(0f, 0f, Manager.FrameWidth, Manager.FrameHeight);
            Pointer pointer = Manager.UiPointer ?? _fallbackPointer;

            _gui.Begin(batch, pointer);
            BootScreenRenderer.Draw(batch, _gui, _white, _font, bounds, _pipeline.Snapshot(), _theme,
                _allowRetry, _allowQuit, _elapsed, out bool retryClicked, out bool quitClicked);

            if (retryClicked) _pipeline.Retry();
            else if (quitClicked) _onQuit?.Invoke();
        }
    }
}
