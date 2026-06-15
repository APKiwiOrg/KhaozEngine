using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

static string FindModel(string name = "testmodel.glb")
{
    string a = Path.Combine(AppContext.BaseDirectory, "assets", name);
    if (File.Exists(a)) return a;
    return Path.GetFullPath("KhaozEngine.Render3D/assets/" + name);
}

if (args.Contains("--smoke"))
{
    var mesh = GltfLoader.Load(FindModel(args.Contains("--asteroid") ? "asteroid.glb" : "testmodel.glb"));
    Console.WriteLine($"mesh: verts={mesh.Vertices.Length} tris={mesh.TriangleCount} v0.Color={mesh.Vertices[0].Color} v0.N={mesh.Vertices[0].Normal}");
    int w = 1280, h = 720;
    bool retro = args.Contains("--retro");
    byte[] rgba = Render3DSnapshot.Capture(mesh, s =>
    {
        s.Camera.OrthoSize = 2.7f;
        if (retro)
        {
            s.Post.RenderWidth = 320; s.Post.RenderHeight = 180;
            s.Post.Pixelated = true; s.Post.Quantize = true; s.Post.Dither = true; s.Post.CelBands = 4;
            s.Post.ActivePalette = args.Contains("--pico") ? Palettes.Pico8
                : args.Contains("--gb") ? Palettes.GameBoy : Palettes.Ember8;
        }
        else
        {
            s.Post.RenderWidth = 2560; s.Post.RenderHeight = 1440; // 2x SSAA -> output, smooth edges
        }
        if (args.Contains("--noe")) s.Post.Outline = false;
    }, w, h, 8);

    int nonBg = 0;
    for (int p = 0; p < w * h; p++)
        if (rgba[p * 4] + rgba[p * 4 + 1] + rgba[p * 4 + 2] > 30) nonBg++;
    int ci = (h / 2 * w + w / 2) * 4;
    Console.WriteLine($"center RGBA=({rgba[ci]},{rgba[ci + 1]},{rgba[ci + 2]}) corner RGBA=({rgba[0]},{rgba[1]},{rgba[2]})");
    string outPath = Path.Combine(AppContext.BaseDirectory, "smoke.bmp");
    WriteBmp(outPath, w, h, rgba);
    Console.WriteLine($"smoke: wrote {outPath}, non-background pixels = {nonBg}");
    Console.WriteLine(nonBg > 100 ? "SMOKE PASS" : "SMOKE FAIL");
    return nonBg > 100 ? 0 : 1;
}

var host = new Render3DHost("KhaozEngine Render3D — sample", 1280, 720);
var sc = host.Scene;
string[] modelFiles = { FindModel("testmodel.glb"), FindModel("asteroid.glb") };
MeshHandle[] handles = modelFiles.Select(p => sc.LoadMesh(GltfLoader.Load(p))).ToArray();
int modelIdx = 0;
sc.Camera.OrthoSize = 2.7f;
int palIdx = 2;
PrintHelp();

host.Run(f =>
{
    if (f.Pressed.Contains(Key.Space)) modelIdx = (modelIdx + 1) % handles.Length;
    if (f.Pressed.Contains(Key.O)) sc.Post.Outline = !sc.Post.Outline;
    if (f.Pressed.Contains(Key.A)) sc.Post.Starfield = !sc.Post.Starfield;
    if (f.Pressed.Contains(Key.R)) // toggle the retro/pixel look on/off
    {
        bool on = !sc.Post.Quantize;
        sc.Post.Quantize = sc.Post.Dither = sc.Post.Pixelated = on;
        sc.Post.CelBands = on ? 4 : 0;
        sc.Post.RenderWidth = on ? 320 : 1920; sc.Post.RenderHeight = on ? 180 : 1080;
    }
    if (f.Pressed.Contains(Key.C)) sc.Post.CelBands = sc.Post.CelBands == 0 ? 4 : 0;
    if (f.Pressed.Contains(Key.P)) { palIdx = (palIdx + 1) % Palettes.All.Length; sc.Post.ActivePalette = Palettes.All[palIdx]; Console.WriteLine("palette: " + sc.Post.ActivePalette.Name); }
    if (f.Down.Contains(Key.W)) sc.Camera.OrthoSize = MathF.Max(1f, sc.Camera.OrthoSize - 2f * f.Dt);   // zoom in
    if (f.Down.Contains(Key.S)) sc.Camera.OrthoSize = MathF.Min(12f, sc.Camera.OrthoSize + 2f * f.Dt);  // zoom out
    if (f.Down.Contains(Key.Up)) sc.Camera.Elevation += 1.5f * f.Dt;
    if (f.Down.Contains(Key.Down)) sc.Camera.Elevation -= 1.5f * f.Dt;
    if (f.Down.Contains(Key.Left)) sc.Camera.Azimuth -= 1.5f * f.Dt;
    if (f.Down.Contains(Key.Right)) sc.Camera.Azimuth += 1.5f * f.Dt;

    sc.Begin();
    for (int gx = -1; gx <= 1; gx++)
        for (int gz = -1; gz <= 1; gz++)
            sc.Draw(handles[modelIdx], Matrix4x4.CreateTranslation(gx * 3f, 0, gz * 3f));
});
host.Dispose();
return 0;

static void PrintHelp() => Console.WriteLine(
    "Space model | O outline | A starfield | R retro toggle | C cel | P palette | W/S zoom | arrows orbit | Esc quit");

// Minimal 24-bit bottom-up BMP (opens in Preview).
static void WriteBmp(string path, int w, int h, byte[] rgba)
{
    int rowSize = (w * 3 + 3) & ~3;
    int imgSize = rowSize * h;
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    bw.Write((byte)'B'); bw.Write((byte)'M');
    bw.Write(54 + imgSize); bw.Write(0); bw.Write(54);
    bw.Write(40); bw.Write(w); bw.Write(h);
    bw.Write((short)1); bw.Write((short)24);
    bw.Write(0); bw.Write(imgSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    byte[] row = new byte[rowSize];
    for (int y = h - 1; y >= 0; y--)
    {
        Array.Clear(row);
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            row[x * 3 + 0] = rgba[i + 2];
            row[x * 3 + 1] = rgba[i + 1];
            row[x * 3 + 2] = rgba[i + 0];
        }
        bw.Write(row);
    }
}
