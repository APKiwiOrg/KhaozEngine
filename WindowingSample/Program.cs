using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
// Render2D still ships its own Key/FrameInfo/Render2DHost (standalone path); Windowing now provides the
// shared ones. Alias to Windowing's here until Render2DHost is folded into the Windowing path (follow-up).
using Key = KhaozEngine.Windowing.Key;
using MouseButton = KhaozEngine.Windowing.MouseButton;

// Proves the windowing + input foundation end-to-end: an AppWindow (SDL2/Metal) drives a Render2D scene
// that responds to keyboard + mouse — no MonoGame anywhere.
var window = new AppWindow("KhaozEngine.Windowing — input demo", 960, 540) { ClearColor = new Vector4(0.08f, 0.10f, 0.14f, 1f) };
var surface = new Render2DSurface(window);
var font = surface.LoadFont("/System/Library/Fonts/Supplemental/Arial.ttf", 26f);
var white = surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);

var box = new Vector2(440, 240);

window.Run(frame =>
{
    var input = frame.Input;
    if (input.WasPressed(Key.Escape)) window.Close();

    float speed = 320f * frame.Dt;
    if (input.IsDown(Key.Left) || input.IsDown(Key.A)) box.X -= speed;
    if (input.IsDown(Key.Right) || input.IsDown(Key.D)) box.X += speed;
    if (input.IsDown(Key.Up) || input.IsDown(Key.W)) box.Y -= speed;
    if (input.IsDown(Key.Down) || input.IsDown(Key.S)) box.Y += speed;
    if (input.WasPressed(MouseButton.Left)) box = input.MousePosition;

    surface.NewFrame(frame);
    surface.Batch.Begin();
    surface.Batch.Draw(white, new Vector4(box.X - 40, box.Y - 40, 80, 80), new Vector4(0.90f, 0.60f, 0.30f, 1f));
    surface.Batch.Draw(white, new Vector4(input.MousePosition.X - 3, input.MousePosition.Y - 3, 6, 6), new Vector4(0.4f, 0.95f, 0.7f, 1f));
    surface.Batch.DrawString(font, "WASD / arrows move the box  •  click to teleport  •  Esc to quit", new Vector2(20, 18), new Vector4(0.92f, 0.96f, 1f, 1f));
    surface.Batch.DrawString(font, $"mouse {(int)input.MousePosition.X},{(int)input.MousePosition.Y}    box {(int)box.X},{(int)box.Y}", new Vector2(20, 502), new Vector4(0.7f, 0.85f, 1f, 1f));
    surface.Batch.End();
});

surface.Dispose();
window.Dispose();
