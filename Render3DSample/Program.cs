using System;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;

static string FindModel()
{
    string a = Path.Combine(AppContext.BaseDirectory, "assets", "testmodel.glb");
    if (File.Exists(a)) return a;
    string b = Path.GetFullPath("KhaozEngine.Render3D/assets/testmodel.glb");
    return b;
}

if (args.Contains("--smoke"))
{
    var mesh = GltfLoader.Load(FindModel());
    Console.WriteLine($"mesh: verts={mesh.Vertices.Length} tris={mesh.TriangleCount} v0.Color={mesh.Vertices[0].Color} v0.N={mesh.Vertices[0].Normal}");
    int w = 320, h = 180;
    bool raw = args.Contains("--raw");
    byte[] rgba = Render3DSnapshot.Capture(mesh, s =>
    {
        s.Camera.OrthoSize = 3.2f;
        s.Post.LowResWidth = w; s.Post.LowResHeight = h;
        s.Post.Quantize = !raw && !args.Contains("--noq");
        s.Post.Dither = !raw && !args.Contains("--nod");
        s.Post.Outline = !raw && !args.Contains("--noe");
        s.Post.CelBands = raw ? 0 : 4;
        s.Post.ActivePalette = Palettes.Ember8;
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
sc.LoadModel(GltfLoader.Load(FindModel()));
sc.Camera.OrthoSize = 3.2f;
int palIdx = 2;
sc.Post.ActivePalette = Palettes.All[palIdx];
PrintHelp();

host.Run(f =>
{
    sc.Spin(f.Dt);
    if (f.Pressed.Contains(Key.Q)) sc.Post.Quantize = !sc.Post.Quantize;
    if (f.Pressed.Contains(Key.D)) sc.Post.Dither = !sc.Post.Dither;
    if (f.Pressed.Contains(Key.O)) sc.Post.Outline = !sc.Post.Outline;
    if (f.Pressed.Contains(Key.C)) sc.Post.CelBands = sc.Post.CelBands == 0 ? 4 : 0;
    if (f.Pressed.Contains(Key.P)) { palIdx = (palIdx + 1) % Palettes.All.Length; sc.Post.ActivePalette = Palettes.All[palIdx]; Console.WriteLine("palette: " + sc.Post.ActivePalette.Name); }
    if (f.Down.Contains(Key.Up)) sc.Camera.Elevation += 1.5f * f.Dt;
    if (f.Down.Contains(Key.Down)) sc.Camera.Elevation -= 1.5f * f.Dt;
    if (f.Down.Contains(Key.Left)) sc.Camera.Azimuth -= 1.5f * f.Dt;
    if (f.Down.Contains(Key.Right)) sc.Camera.Azimuth += 1.5f * f.Dt;
    if (f.Pressed.Contains(Key.Number1)) { sc.Post.LowResWidth = 160; sc.Post.LowResHeight = 90; }
    if (f.Pressed.Contains(Key.Number2)) { sc.Post.LowResWidth = 320; sc.Post.LowResHeight = 180; }
    if (f.Pressed.Contains(Key.Number3)) { sc.Post.LowResWidth = 640; sc.Post.LowResHeight = 360; }
});
host.Dispose();
return 0;

static void PrintHelp() => Console.WriteLine(
    "Q quantize | D dither | O outline | C cel | P palette | arrows angle | 1/2/3 low-res | Esc quit");

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
