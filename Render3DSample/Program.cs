using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

if (args.Contains("--smoke"))
{
    var mesh = GltfLoader.Load(Render3DSampleApp.FindModel(args.Contains("--asteroid") ? "asteroid.glb" : "testmodel.glb"));
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

PrintHelp();
using (var app = new Render3DSampleApp())
    app.Run();
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

// Windowed 3D demo on the GameApp loop facade: orbit/zoom an iso camera over a 3x3 grid of the
// current model, toggle outline/starfield/retro/cel/palette. (The --smoke path above stays on
// Render3DSnapshot, unchanged.)
sealed class Render3DSampleApp : GameApp3D
{
    MeshHandle[] _handles = Array.Empty<MeshHandle>();
    MeshHandle _texturedPlane;
    int _modelIdx;
    int _palIdx = 2;

    /// <summary>A deterministic NxN checkerboard in two contrasting colours, as RGBA8 bytes.</summary>
    static byte[] Checkerboard(int n = 64, int cell = 8)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool a = ((x / cell) + (y / cell)) % 2 == 0;
                int i = (y * n + x) * 4;
                px[i + 0] = (byte)(a ? 235 : 30);
                px[i + 1] = (byte)(a ? 70 : 200);
                px[i + 2] = (byte)(a ? 40 : 220);
                px[i + 3] = 255;
            }
        return px;
    }

    public static string FindModel(string name = "testmodel.glb")
    {
        string a = Path.Combine(AppContext.BaseDirectory, "assets", name);
        if (File.Exists(a)) return a;
        return Path.GetFullPath("KhaozEngine.Render3D/assets/" + name);
    }

    public Render3DSampleApp()
        : base(new GameAppOptions
        {
            Title = "KhaozEngine Render3D - sample",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Vector4(0.10f, 0.12f, 0.16f, 1f),
        })
    { }

    protected override void OnLoad()
    {
        var sc = Scene!;
        string[] modelFiles = { FindModel("testmodel.glb"), FindModel("asteroid.glb") };
        _handles = modelFiles.Select(p => sc.LoadMesh(GltfLoader.Load(p))).ToArray();

        // Textured floor: a checkerboard-albedo plane drawn under the model grid, exercising the texturing path.
        Scene3D.TextureHandle checker = sc.LoadTexture(Checkerboard(), 64, 64);
        _texturedPlane = sc.LoadMesh(MeshPrimitives.Plane(12f, 12f), checker);

        sc.Camera.OrthoSize = 2.7f;
    }

    protected override void OnUpdate(float dt)
    {
        var sc = Scene!;

        if (Input.WasPressed(Key.Escape)) { Quit(); return; }

        if (Input.WasPressed(Key.Space)) _modelIdx = (_modelIdx + 1) % _handles.Length;
        if (Input.WasPressed(Key.O)) sc.Post.Outline = !sc.Post.Outline;
        if (Input.WasPressed(Key.A)) sc.Post.Starfield = !sc.Post.Starfield;
        if (Input.WasPressed(Key.R)) // toggle the retro/pixel look on/off
        {
            bool on = !sc.Post.Quantize;
            sc.Post.Quantize = sc.Post.Dither = sc.Post.Pixelated = on;
            sc.Post.CelBands = on ? 4 : 0;
            sc.Post.RenderWidth = on ? 320 : 1920; sc.Post.RenderHeight = on ? 180 : 1080;
        }
        if (Input.WasPressed(Key.C)) sc.Post.CelBands = sc.Post.CelBands == 0 ? 4 : 0;
        if (Input.WasPressed(Key.P))
        {
            _palIdx = (_palIdx + 1) % Palettes.All.Length;
            sc.Post.ActivePalette = Palettes.All[_palIdx];
            Console.WriteLine("palette: " + sc.Post.ActivePalette.Name);
        }
        if (Input.IsDown(Key.W)) sc.Camera.OrthoSize = MathF.Max(1f, sc.Camera.OrthoSize - 2f * dt);   // zoom in
        if (Input.IsDown(Key.S)) sc.Camera.OrthoSize = MathF.Min(12f, sc.Camera.OrthoSize + 2f * dt);  // zoom out
        if (Input.IsDown(Key.Up)) sc.Camera.Elevation += 1.5f * dt;
        if (Input.IsDown(Key.Down)) sc.Camera.Elevation -= 1.5f * dt;
        if (Input.IsDown(Key.Left)) sc.Camera.Azimuth -= 1.5f * dt;
        if (Input.IsDown(Key.Right)) sc.Camera.Azimuth += 1.5f * dt;
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        // Textured checkerboard floor under the grid.
        scene.Draw(_texturedPlane, Matrix4x4.CreateTranslation(0, -1.2f, 0));
        for (int gx = -1; gx <= 1; gx++)
            for (int gz = -1; gz <= 1; gz++)
                scene.Draw(_handles[_modelIdx], Matrix4x4.CreateTranslation(gx * 3f, 0, gz * 3f));
    }
}
