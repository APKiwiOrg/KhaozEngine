using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

// Proves the windowing + input foundation end-to-end: an AppWindow (SDL2/Metal) drives a Render2D scene
// that responds to keyboard + mouse, plus a clickable button via the bounds-aware Pointer (IsTapIn) with
// region-blocking so the box-teleport beneath respects the button (the click-through fix). No MonoGame.
var window = new AppWindow("KhaozEngine.Windowing — input demo", 960, 540) { ClearColor = new Vector4(0.08f, 0.10f, 0.14f, 1f) };
var surface = new Render2DSurface(window);
var pointer = new Pointer();
var font = surface.LoadFont("/System/Library/Fonts/Supplemental/Arial.ttf", 26f);
var white = surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);

var box = new Vector2(440, 240);
var button = new Rect(720, 430, 220, 70);
int clicks = 0;

window.Run(frame =>
{
    var input = frame.Input;
    pointer.Update(input);
    pointer.BlockRegion(button);              // the button overlay reserves its region this frame
    if (input.WasPressed(Key.Escape)) window.Close();

    // Button (uses the press-origin IsTapIn invariant — a click that began elsewhere can't trigger it).
    if (pointer.IsTapIn(button)) clicks++;

    float speed = 320f * frame.Dt;
    if (input.IsDown(Key.Left) || input.IsDown(Key.A)) box.X -= speed;
    if (input.IsDown(Key.Right) || input.IsDown(Key.D)) box.X += speed;
    if (input.IsDown(Key.Up) || input.IsDown(Key.W)) box.Y -= speed;
    if (input.IsDown(Key.Down) || input.IsDown(Key.S)) box.Y += speed;
    // Teleport only when the click did NOT land on the button (click-through prevention).
    if (input.WasPressed(MouseButton.Left) && !pointer.IsBlocked(pointer.Position)) box = input.MousePosition;

    surface.NewFrame(frame);
    surface.Batch.Begin();
    surface.Batch.Draw(white, new Vector4(box.X - 40, box.Y - 40, 80, 80), new Vector4(0.90f, 0.60f, 0.30f, 1f));
    surface.Batch.Draw(white, new Vector4(pointer.Position.X - 3, pointer.Position.Y - 3, 6, 6), new Vector4(0.4f, 0.95f, 0.7f, 1f));

    var btnColor = pointer.IsPressingIn(button) ? new Vector4(0.20f, 0.40f, 0.55f, 1f)
        : pointer.IsHoveringIn(button) ? new Vector4(0.26f, 0.50f, 0.66f, 1f)
        : new Vector4(0.18f, 0.30f, 0.42f, 1f);
    surface.Batch.Draw(white, new Vector4(button.X, button.Y, button.Width, button.Height), btnColor);
    surface.Batch.DrawString(font, $"Click me ({clicks})", new Vector2(button.X + 24, button.Y + 20), new Vector4(0.95f, 0.97f, 1f, 1f));

    surface.Batch.DrawString(font, "WASD/arrows move  •  click empty space to teleport  •  click the button  •  Esc to quit", new Vector2(20, 18), new Vector4(0.92f, 0.96f, 1f, 1f));
    surface.Batch.DrawString(font, $"mouse {(int)pointer.Position.X},{(int)pointer.Position.Y}    box {(int)box.X},{(int)box.Y}", new Vector2(20, 502), new Vector4(0.7f, 0.85f, 1f, 1f));
    surface.Batch.End();
});

surface.Dispose();
window.Dispose();
