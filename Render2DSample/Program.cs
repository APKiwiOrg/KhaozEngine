using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

static byte[] Checker(int size)
{
    var px = new byte[size * size * 4];
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool on = ((x / 8) + (y / 8)) % 2 == 0;
            int i = (y * size + x) * 4;
            byte r = on ? (byte)240 : (byte)200, g = on ? (byte)215 : (byte)100, b = on ? (byte)130 : (byte)60;
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
        }
    return px;
}

// Build the demo scene through the public API. Works for both the headless snapshot and the live window.
static void Scene(SpriteBatch batch, Texture2D white, Texture2D checker, SpriteFont big, SpriteFont small, int w)
{
    batch.Begin(); // screen space
    batch.Draw(white, new Vector4(40, 30, w - 80, 90), new Color(0.18f, 0.22f, 0.30f, 0.92f));
    for (int i = 0; i < 6; i++)
    {
        float s = 60 + i * 14;
        batch.Draw(checker, new Vector4(60 + i * 130, 170, s, s), new Color(1f, 1f, 1f, 1f));
    }
    batch.DrawString(big, "KhaozEngine.Render2D", new Vector2(60, 40), new Color(0.95f, 0.97f, 1f, 1f));
    batch.DrawString(small, "SpriteBatch + Camera2D + Texture2D + runtime TTF text, all on Veldrid.", new Vector2(60, 300), new Color(0.8f, 0.85f, 0.95f, 1f));
    batch.DrawString(small, "The quick brown fox jumps over the lazy dog. 0123456789 !?@#", new Vector2(60, 340), new Color(0.9f, 0.8f, 0.6f, 1f));
    batch.DrawString(small, "Alpha blending, tinting, batched quads. Press Esc to quit.", new Vector2(60, 380), new Color(0.7f, 0.95f, 0.8f, 1f));
    batch.End();
}

if (args.Contains("--smoke"))
{
    int w = 960, h = 540;
    byte[] rgba = Render2DSnapshot.Capture(w, h, new Color(0.10f, 0.12f, 0.16f, 1f), ctx =>
    {
        var white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        var checker = ctx.CreateTexture(Checker(64), 64, 64);
        var big = ctx.LoadDefaultFont(40f);
        var small = ctx.LoadDefaultFont(22f);
        Scene(ctx.Batch, white, checker, big, small, ctx.Width);
    });
    int nonBg = 0;
    for (int p = 0; p < w * h; p++) if (rgba[p * 4] + rgba[p * 4 + 1] + rgba[p * 4 + 2] > 120) nonBg++;
    string outPath = Path.Combine(AppContext.BaseDirectory, "render2d.bmp");
    WriteBmp(outPath, w, h, rgba);
    Console.WriteLine($"wrote {outPath}, non-bg px = {nonBg}");
    Console.WriteLine(nonBg > 1000 ? "SMOKE PASS" : "SMOKE FAIL");
    return nonBg > 1000 ? 0 : 1;
}

var window = new KhaozEngine.Windowing.AppWindow("KhaozEngine.Render2D — sample", 960, 540);
var surface = new Render2DSurface(window);
var whiteT = surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
var checkerT = surface.CreateTexture(Checker(64), 64, 64);
var bigF = surface.LoadDefaultFont(40f);
var smallF = surface.LoadDefaultFont(22f);
Console.WriteLine("Esc quit");
window.Run(frame =>
{
    if (frame.Input.WasPressed(KhaozEngine.Windowing.Key.Escape)) window.Close();
    surface.NewFrame(frame);
    Scene(surface.Batch, whiteT, checkerT, bigF, smallF, frame.Width);
});
surface.Dispose();
window.Dispose();
return 0;

static void WriteBmp(string path, int w, int h, byte[] rgba)
{
    int rowSize = (w * 3 + 3) & ~3, imgSize = rowSize * h;
    using var bw = new BinaryWriter(new FileStream(path, FileMode.Create));
    bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + imgSize); bw.Write(0); bw.Write(54);
    bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)24);
    bw.Write(0); bw.Write(imgSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    byte[] row = new byte[rowSize];
    for (int y = h - 1; y >= 0; y--)
    {
        Array.Clear(row);
        for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; row[x * 3] = rgba[i + 2]; row[x * 3 + 1] = rgba[i + 1]; row[x * 3 + 2] = rgba[i]; }
        bw.Write(row);
    }
}
