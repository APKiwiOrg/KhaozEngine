using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

// Console logging so the OpenAL music + SFX backends report which backend loaded (proves the real OpenAL SFX
// path is exercised in a headless KE_MAX_FRAMES smoke run, not the silent Null fallback).
var loggerOptions = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Info };
loggerOptions.Sinks.Add(new ConsoleSink());
Log.Configure(loggerOptions);

// Proves the windowing + input foundation, now with the input-breadth additions: a GestureRecognizer
// (drag / tap / long-press) over the design-space Pointer, and a GameClock (pause / time-scale) driving an
// animation independently of real time. All authored in a 960x540 DesignViewport so it scales/letterboxes on
// resize and gestures stay aligned. No MonoGame.
var window = new AppWindow("KhaozEngine.Windowing - input demo", 960, 540) { ClearColor = new Vector4(0.03f, 0.04f, 0.06f, 1f) };
var surface = new Render2DSurface(window);
var font = surface.LoadFont("/System/Library/Fonts/Supplemental/Arial.ttf", 26f);
var white = surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);

var viewport = new DesignViewport(960, 540, ScaleMode.Fit);
var pointer = new Pointer();
var gestures = new GestureRecognizer();
var clock = new GameClock();

var box = new Vector2(300, 300);
bool grabbed = false;
float orbit = 0f;
var marks = new List<(Vector2 pos, float life)>();

// SFX: synth a couple of placeholder sounds into a temp dir, then load + play through the real OpenAL path.
// Z plays a non-positional blip; X plays a positional thud off to the right (attenuated by the listener pose).
// Falls back to a silent backend if no audio device is present (headless), so this never crashes the sample.
var sfxDir = Path.Combine(Path.GetTempPath(), "ke-windowing-sfx");
Directory.CreateDirectory(sfxDir);
WavSynth.WriteTone(Path.Combine(sfxDir, "blip.wav"), 880f, 0.12f, Waveform.Sine);
WavSynth.WriteNoise(Path.Combine(sfxDir, "thud.wav"), 0.20f);
var audio = new AudioSystem();
audio.RegisterSfxes(new[] { "blip", "thud" });
audio.LoadContent(sfxDir);
audio.SetListener(Vector3.Zero, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
string lastSfx = "none";

// When running headless under KE_MAX_FRAMES (no interactive keypresses), auto-fire the SFX so the OpenAL
// Play / Play3D path is exercised before the smoke run exits.
bool autoSmoke = int.TryParse(Environment.GetEnvironmentVariable("KE_MAX_FRAMES"), out int _);
int frameNo = 0;

window.Run(frame =>
{
    var input = frame.Input;
    if (input.WasPressed(Key.Escape)) window.Close();

    viewport.Update(frame.Width, frame.Height);
    pointer.Update(input, viewport);
    gestures.Update(pointer, frame.Dt);          // gestures use REAL dt

    // Clock controls: Space pauses, 1/2/3 set slow/normal/fast.
    if (input.WasPressed(Key.Space)) { if (clock.IsPaused) clock.Resume(); else clock.Pause(); }
    if (input.WasPressed(Key.D1)) clock.TimeScale = 0.5f;
    if (input.WasPressed(Key.D2)) clock.TimeScale = 1f;
    if (input.WasPressed(Key.D3)) clock.TimeScale = 2f;

    // SFX one-shots: Z = non-positional blip, X = positional thud 8 units to the listener's right.
    if (input.WasPressed(Key.Z)) { audio.PlaySfx("blip"); lastSfx = "blip"; }
    if (input.WasPressed(Key.X)) { audio.PlaySfx3D("thud", new Vector3(8, 0, 0)); lastSfx = "thud (3D)"; }
    if (autoSmoke)
    {
        frameNo++;
        if (frameNo == 10) { audio.PlaySfx("blip"); lastSfx = "blip (auto)"; Console.WriteLine("smoke: PlaySfx blip"); }
        if (frameNo == 30) { audio.PlaySfx3D("thud", new Vector3(8, 0, 0)); lastSfx = "thud (auto 3D)"; Console.WriteLine("smoke: PlaySfx3D thud"); }
    }
    audio.Update();
    clock.Update(frame.Dt);
    orbit += clock.ScaledDeltaSeconds * 1.6f;     // animation runs on SCALED time (freezes when paused)

    // Gamepad (best-effort): left stick nudges the box, A resets it. No-op when no controller is connected.
    var pad = input.PrimaryGamepad;
    if (pad.IsConnected)
    {
        box += pad.LeftStickDeadzoned(0.2f) * (260f * frame.Dt);
        if (pad.WasPressed(GamepadButton.A)) box = new Vector2(300, 300);
    }

    // Gesture handling.
    var boxRect = new Rect(box.X - 45, box.Y - 45, 90, 90);
    if (gestures.DragStarted && boxRect.Contains(gestures.DragStart)) grabbed = true;
    if (grabbed && gestures.IsDragging) box += gestures.DragDelta;
    if (gestures.DragEnded) grabbed = false;
    if (gestures.Tapped) marks.Add((gestures.TapPosition, 1f));
    if (gestures.LongPressed) box = new Vector2(300, 300);   // long-press resets the box

    for (int i = marks.Count - 1; i >= 0; i--)
    {
        var m = marks[i]; m.life -= frame.Dt * 1.2f;
        if (m.life <= 0f) marks.RemoveAt(i); else marks[i] = m;
    }

    surface.NewFrame(frame);
    surface.Batch.Begin(viewport);

    surface.Batch.Draw(white, new Vector4(0, 0, 960, 540), new Color(0.07f, 0.09f, 0.13f, 1f));   // design bg

    // Orbiting dot (pause/time-scale made visible).
    var c = new Vector2(700, 180);
    var dot = c + new Vector2(MathF.Cos(orbit), MathF.Sin(orbit)) * 90f;
    surface.Batch.Draw(white, new Vector4(c.X - 92, c.Y - 92, 184, 184), new Color(0.10f, 0.12f, 0.17f, 1f));
    surface.Batch.Draw(white, new Vector4(dot.X - 10, dot.Y - 10, 20, 20), new Color(0.95f, 0.75f, 0.35f, 1f));

    // Tap marks (fade out).
    foreach (var (pos, life) in marks)
        surface.Batch.Draw(white, new Vector4(pos.X - 6, pos.Y - 6, 12, 12), new Color(0.4f, 0.95f, 0.7f, life));

    // Draggable box.
    var boxColor = grabbed ? new Vector4(0.30f, 0.55f, 0.75f, 1f) : new Vector4(0.18f, 0.34f, 0.5f, 1f);
    surface.Batch.Draw(white, new Vector4(boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height), (Color)boxColor);
    surface.Batch.DrawString(font, "drag me", new Vector2(box.X - 40, box.Y - 13), new Color(0.95f, 0.97f, 1f, 1f));

    // Pointer marker.
    surface.Batch.Draw(white, new Vector4(pointer.Position.X - 3, pointer.Position.Y - 3, 6, 6), new Color(0.4f, 0.95f, 0.7f, 1f));

    string gstate = gestures.IsDragging ? "dragging" : "idle";
    string padInfo = pad.IsConnected ? $"pad: stick {pad.LeftStick.X:0.0},{pad.LeftStick.Y:0.0}" : "pad: none";
    surface.Batch.DrawString(font, "Drag box  -  tap  -  long-press reset  -  Space pause  -  1/2/3 speed  -  Z blip  -  X 3D thud", new Vector2(20, 18), new Color(0.92f, 0.96f, 1f, 1f));
    surface.Batch.DrawString(font,
        $"gesture: {gstate}    clock: {(clock.IsPaused ? "PAUSED" : $"x{clock.TimeScale:0.0}")}    sim t={clock.ElapsedScaledSeconds:0.0}s    sfx: {lastSfx}    {padInfo}",
        new Vector2(20, 500), new Color(0.7f, 0.85f, 1f, 1f));

    surface.Batch.End();
});

audio.Dispose();
surface.Dispose();
window.Dispose();
