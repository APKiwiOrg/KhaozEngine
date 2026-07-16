using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>
    /// Boot-screen demo: drives a real <see cref="BootPipeline"/> of fake, delayed loading steps (one determinate,
    /// one indeterminate, one determinate) through the same <see cref="BootScreenRenderer"/> the turn-key
    /// <see cref="BootScreen"/> uses. Enter replays the sequence, F toggles a forced failure to show the error +
    /// retry state, Escape returns to the showcase menu (the controls hint is the shared chrome's bottom band now,
    /// not drawn here). Like the other rooms the app injects the texture / font (a <see cref="GameScene"/> cannot
    /// reach the device). The boot screen itself needs only those, no game assets.
    /// </summary>
    public sealed class RoomBoot : GameScene, IShowcaseRoom
    {
        static readonly StringId[] Hints = { ShowcaseStrings.ControlsBoot };

        public StringId Title => ShowcaseStrings.RoomBootTitle;
        public IReadOnlyList<StringId> ControlsHints => Hints;

        Texture2D _white = null!;
        DpiFont _font = null!;
        GuiSurface _gui = null!;
        readonly BootScreenTheme _theme = BootScreenTheme.Default;

        BootPipeline _pipeline = null!;
        float _elapsed;
        bool _forceFail;

        /// <summary>Wire in the texture/font created on the app's Surface2D. Call once, right after construction.</summary>
        public RoomBoot Init(Texture2D white, DpiFont font)
        {
            _white = white;
            _font = font;
            _gui = new GuiSurface(white);
            return this;
        }

        public override void OnEnter() => Rebuild();

        public override void OnExit() => _pipeline.Cancel();

        void Rebuild()
        {
            _pipeline = new BootPipeline(BuildSteps(_forceFail));
            _pipeline.Start();
            _elapsed = 0f;
        }

        static IReadOnlyList<IBootStep> BuildSteps(bool forceFail)
        {
            var steps = new List<IBootStep>
            {
                DelayStep(ShowcaseStrings.BootStepAssets, weight: 2f, seconds: 1.4f, indeterminate: false),
                DelayStep(ShowcaseStrings.BootStepAudio, weight: 1f, seconds: 0.8f, indeterminate: true),
            };
            if (forceFail)
                steps.Add(BootStep.Create(ShowcaseStrings.BootStepWorld, 2f,
                    (p, ct) => throw new BootStepException(BootStrings.ErrorServerUnavailable)));
            else
                steps.Add(DelayStep(ShowcaseStrings.BootStepWorld, weight: 2f, seconds: 1.2f, indeterminate: false));
            return steps;
        }

        static IBootStep DelayStep(LocalizedText name, float weight, float seconds, bool indeterminate)
            => BootStep.Create(name, weight, async (progress, ct) =>
            {
                if (indeterminate)
                {
                    progress.ReportIndeterminate();
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                    return;
                }
                const int slices = 24;
                for (int i = 0; i <= slices; i++)
                {
                    progress.Report(i / (float)slices);
                    await Task.Delay(TimeSpan.FromSeconds(seconds / slices), ct);
                }
            });

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            if (m.Input.WasPressed(Key.Escape)) { m.Pop(); return; }

            _elapsed += dt;
            _pipeline.Pump();

            BootState state = _pipeline.State;
            if (state == BootState.Failed && m.Input.WasPressed(Key.Enter))
                _pipeline.Retry();
            else if ((state == BootState.Completed || state == BootState.Restarting) && m.Input.WasPressed(Key.Enter))
                Rebuild();

            if (m.Input.WasPressed(Key.F))
            {
                _forceFail = !_forceFail;
                Rebuild();
            }
        }

        public override void OnDrawUi(SpriteBatch batch)
        {
            var m = Manager!;

            // Point-space layout + DPI scale from the UiViewport (the batch was begun in it): FrameWidth/Height are
            // device pixels and would push the centered content off-screen on HiDPI. dpiScale drives the crisp bake.
            UiViewport? ui = m.UiViewport;
            var bounds = ui is not null
                ? new Rect(0f, 0f, ui.Width, ui.Height)
                : new Rect(0f, 0f, m.FrameWidth, m.FrameHeight);
            float dpiScale = ui?.DpiScale ?? 1f;

            _gui.Begin(batch, m.UiPointer ?? new Pointer());
            BootScreenRenderer.Draw(batch, _gui, _white, _font, dpiScale, bounds, _pipeline.Snapshot(), _theme,
                allowRetry: true, allowQuit: false, _elapsed, out bool retry, out _);
            if (retry) _pipeline.Retry();
            // The controls hint (Enter replay / F toggle a failing step / Esc menu) is the shared chrome's bottom band.
        }
    }
}
