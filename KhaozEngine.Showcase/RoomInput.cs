using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Audio;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Platform;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Ported from <c>WindowingSample/Program.cs</c>: a <see cref="GestureRecognizer"/> (drag / tap /
    /// long-press) over the design-space <see cref="SceneManager.Pointer"/>, a <see cref="GameClock"/> (pause /
    /// time-scale) driving an orbiting-dot animation independently of real time, a clipboard round-trip via
    /// <see cref="Clipboard"/>, and one-shot SFX (non-positional + positional 3D) via <see cref="AudioSystem"/>.
    /// The room owns no GPU device itself (a <see cref="GameScene"/> cannot reach one) - <see cref="ShowcaseApp"/>
    /// creates the texture/font on its <c>Surface2D</c> and hands them in via <see cref="Init"/> right after
    /// construction, keeping the constructor parameterless for the room registry's <c>Func&lt;GameScene&gt;</c>
    /// factory. Audio and clipboard are process-level (not app-instance-bound), so this room constructs its own
    /// <see cref="AudioSystem"/> and calls <see cref="Clipboard"/> directly, exactly as the sample did.
    /// <para>Key remap vs the sample: the sample used Escape to close the whole window. Here Escape returns to
    /// the showcase menu (see <see cref="ShowcaseApp"/>'s room convention), so nothing internal to this room used
    /// Escape for anything else and no remap was needed.</para></summary>
    public sealed class RoomInput : GameScene
    {
        Texture2D _white = null!;
        SpriteFont _font = null!;

        readonly DesignViewport _viewport = new(960, 540, ScaleMode.Fit);
        readonly GestureRecognizer _gestures = new();
        readonly GameClock _clock = new();
        readonly List<(Vector2 pos, float life)> _marks = new();

        Vector2 _box = new(300, 300);
        bool _grabbed;
        float _orbit;

        AudioSystem _audio = null!;
        string _lastSfx = "none";
        string _clipboardStatus = "clipboard: C = copy + verify round-trip,  V = paste from OS";
        string _padInfo = "pad: none";

        /// <summary>Wire in the texture/font created on the app's Surface2D. Call once, right after
        /// construction and before the room is pushed.</summary>
        public RoomInput Init(Texture2D white, SpriteFont font)
        {
            _white = white;
            _font = font;
            return this;
        }

        public override void OnEnter()
        {
            // SFX: synth a couple of placeholder sounds into a temp dir, then load + play through the real
            // OpenAL path (same recipe as WindowingSample). Falls back to a silent backend if no audio device
            // is present (headless), so this never crashes the room.
            string sfxDir = Path.Combine(Path.GetTempPath(), "ke-showcase-input-sfx");
            Directory.CreateDirectory(sfxDir);
            WavSynth.WriteTone(Path.Combine(sfxDir, "blip.wav"), 880f, 0.12f, Waveform.Sine);
            WavSynth.WriteNoise(Path.Combine(sfxDir, "thud.wav"), 0.20f);
            _audio = new AudioSystem();
            _audio.RegisterSfxes(new[] { "blip", "thud" });
            _audio.LoadContent(sfxDir);
            _audio.SetListener(Vector3.Zero, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        }

        public override void OnExit() => _audio.Dispose();

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            if (m.Input.WasPressed(Key.Escape)) { m.Pop(); return; }

            _viewport.Update(m.FrameWidth, m.FrameHeight);
            // m.Pointer is already updated by ShowcaseApp against the same design viewport it hands the scene
            // manager, so gestures line up with this room's own DesignViewport (both are 960x540 Fit).
            var pointer = m.Pointer!;
            _gestures.Update(pointer, dt);   // gestures use REAL dt

            // Clock controls: Space pauses, 1/2/3 set slow/normal/fast.
            if (m.Input.WasPressed(Key.Space)) { if (_clock.IsPaused) _clock.Resume(); else _clock.Pause(); }
            if (m.Input.WasPressed(Key.D1)) _clock.TimeScale = 0.5f;
            if (m.Input.WasPressed(Key.D2)) _clock.TimeScale = 1f;
            if (m.Input.WasPressed(Key.D3)) _clock.TimeScale = 2f;

            // SFX one-shots: Z = non-positional blip, X = positional thud 8 units to the listener's right.
            if (m.Input.WasPressed(Key.Z)) { _audio.PlaySfx("blip"); _lastSfx = "blip"; }
            if (m.Input.WasPressed(Key.X)) { _audio.PlaySfx3D("thud", new Vector3(8, 0, 0)); _lastSfx = "thud (3D)"; }

            // Clipboard: C = write a known string and read it back (self round-trip). V = paste the OS clipboard.
            if (m.Input.WasPressed(Key.C))
            {
                string payload = $"KhaozEngine clipboard {_clock.ElapsedScaledSeconds:0.0}s";
                bool setOk = Clipboard.TrySetClipboardText(payload);
                string readBack = Clipboard.TryGetClipboardText();
                bool roundTrip = setOk && readBack == payload;
                _clipboardStatus = $"copy {(setOk ? "ok" : "FAIL")}  |  round-trip {(roundTrip ? "PASS" : "FAIL")}: \"{readBack}\"";
            }
            if (m.Input.WasPressed(Key.V))
            {
                string pasted = Clipboard.TryGetClipboardText();
                _clipboardStatus = string.IsNullOrEmpty(pasted) ? "paste: <empty / unavailable>" : $"paste: \"{pasted}\"";
            }

            _audio.Update();
            _clock.Update(dt);
            _orbit += _clock.ScaledDeltaSeconds * 1.6f;   // animation runs on SCALED time (freezes when paused)

            // Gamepad (best-effort): left stick nudges the box, A resets it. No-op when no controller is connected.
            var pad = m.Input.PrimaryGamepad;
            if (pad.IsConnected)
            {
                _box += pad.LeftStickDeadzoned(0.2f) * (260f * dt);
                if (pad.WasPressed(GamepadButton.A)) _box = new Vector2(300, 300);
            }
            _padInfo = pad.IsConnected ? $"pad: stick {pad.LeftStick.X:0.0},{pad.LeftStick.Y:0.0}" : "pad: none";

            // Gesture handling.
            var boxRect = new Rect(_box.X - 45, _box.Y - 45, 90, 90);
            if (_gestures.DragStarted && boxRect.Contains(_gestures.DragStart)) _grabbed = true;
            if (_grabbed && _gestures.IsDragging) _box += _gestures.DragDelta;
            if (_gestures.DragEnded) _grabbed = false;
            if (_gestures.Tapped) _marks.Add((_gestures.TapPosition, 1f));
            if (_gestures.LongPressed) _box = new Vector2(300, 300);   // long-press resets the box

            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var mk = _marks[i];
                mk.life -= dt * 1.2f;
                if (mk.life <= 0f) _marks.RemoveAt(i); else _marks[i] = mk;
            }
        }

        // The "drag me" label and the controls hint are developer-facing demo chrome, not localizable player copy,
        // so the raw DrawString literals here are intentional (the KELOC003 escape hatch).
        [LocalizationExempt]
        public override void OnDraw2D(SpriteBatch batch)
        {
            batch.Draw(_white, new Vector4(0, 0, 960, 540), (Color)GuiTheme.Default.Background);   // design bg

            // Orbiting dot (pause/time-scale made visible).
            var c = new Vector2(700, 180);
            var dot = c + new Vector2(MathF.Cos(_orbit), MathF.Sin(_orbit)) * 90f;
            batch.Draw(_white, new Vector4(c.X - 92, c.Y - 92, 184, 184), (Color)GuiTheme.Default.Surface);
            batch.Draw(_white, new Vector4(dot.X - 10, dot.Y - 10, 20, 20), new Color(0.95f, 0.75f, 0.35f, 1f));

            // Tap marks (fade out).
            foreach (var (pos, life) in _marks)
                batch.Draw(_white, new Vector4(pos.X - 6, pos.Y - 6, 12, 12), new Color(0.4f, 0.95f, 0.7f, life));

            // Draggable box.
            var boxColor = _grabbed ? new Vector4(0.30f, 0.55f, 0.75f, 1f) : new Vector4(0.18f, 0.34f, 0.5f, 1f);
            batch.Draw(_white, new Vector4(_box.X - 45, _box.Y - 45, 90, 90), (Color)boxColor);
            batch.DrawString(_font, "drag me", new Vector2(_box.X - 40, _box.Y - 13), new Color(0.95f, 0.97f, 1f, 1f));

            // Pointer marker.
            var pointer = Manager!.Pointer;
            if (pointer is not null)
                batch.Draw(_white, new Vector4(pointer.Position.X - 3, pointer.Position.Y - 3, 6, 6), new Color(0.4f, 0.95f, 0.7f, 1f));

            string gstate = _gestures.IsDragging ? "dragging" : "idle";
            batch.DrawString(_font, "Drag box  -  tap  -  long-press reset  -  Space pause  -  1/2/3 speed  -  Z blip  -  X 3D thud  -  C/V clipboard  -  Esc for menu", new Vector2(20, 18), (Color)GuiTheme.Default.Text);
            batch.DrawString(_font, _clipboardStatus, new Vector2(20, 470), (Color)GuiTheme.Default.TextMuted);
            batch.DrawString(_font,
                $"gesture: {gstate}    clock: {(_clock.IsPaused ? "PAUSED" : $"x{_clock.TimeScale:0.0}")}    sim t={_clock.ElapsedScaledSeconds:0.0}s    sfx: {_lastSfx}    {_padInfo}",
                new Vector2(20, 500), (Color)GuiTheme.Default.TextMuted);
        }
    }
}
