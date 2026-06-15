using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

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
    clock.Update(frame.Dt);
    orbit += clock.ScaledDeltaSeconds * 1.6f;     // animation runs on SCALED time (freezes when paused)

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

    surface.Batch.Draw(white, new Vector4(0, 0, 960, 540), new Vector4(0.07f, 0.09f, 0.13f, 1f));   // design bg

    // Orbiting dot (pause/time-scale made visible).
    var c = new Vector2(700, 180);
    var dot = c + new Vector2(MathF.Cos(orbit), MathF.Sin(orbit)) * 90f;
    surface.Batch.Draw(white, new Vector4(c.X - 92, c.Y - 92, 184, 184), new Vector4(0.10f, 0.12f, 0.17f, 1f));
    surface.Batch.Draw(white, new Vector4(dot.X - 10, dot.Y - 10, 20, 20), new Vector4(0.95f, 0.75f, 0.35f, 1f));

    // Tap marks (fade out).
    foreach (var (pos, life) in marks)
        surface.Batch.Draw(white, new Vector4(pos.X - 6, pos.Y - 6, 12, 12), new Vector4(0.4f, 0.95f, 0.7f, life));

    // Draggable box.
    var boxColor = grabbed ? new Vector4(0.30f, 0.55f, 0.75f, 1f) : new Vector4(0.18f, 0.34f, 0.5f, 1f);
    surface.Batch.Draw(white, new Vector4(boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height), boxColor);
    surface.Batch.DrawString(font, "drag me", new Vector2(box.X - 40, box.Y - 13), new Vector4(0.95f, 0.97f, 1f, 1f));

    // Pointer marker.
    surface.Batch.Draw(white, new Vector4(pointer.Position.X - 3, pointer.Position.Y - 3, 6, 6), new Vector4(0.4f, 0.95f, 0.7f, 1f));

    string gstate = gestures.IsDragging ? "dragging" : "idle";
    surface.Batch.DrawString(font, "Drag the box  -  tap empty space  -  long-press to reset  -  Space pause  -  1/2/3 speed", new Vector2(20, 18), new Vector4(0.92f, 0.96f, 1f, 1f));
    surface.Batch.DrawString(font,
        $"gesture: {gstate}    clock: {(clock.IsPaused ? "PAUSED" : $"x{clock.TimeScale:0.0}")}    sim t={clock.ElapsedScaledSeconds:0.0}s",
        new Vector2(20, 500), new Vector4(0.7f, 0.85f, 1f, 1f));

    surface.Batch.End();
});

surface.Dispose();
window.Dispose();
