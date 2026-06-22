using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

// Minimal repro of SpaceGame's render pattern, ENGINE CODE ONLY: a 3D world rendered to an OFFSCREEN
// Render3DPreview at 2x supersample (its own _gd.Submit), then composited to the swapchain as a 2D textured
// quad (a second submit), then present. Cores = rigid boxes; tentacles = many RestPose skinned tubes (the
// exact mesh BuildTube(0.2,1.3,24,8,8,X), the case SpaceGame says renders as a screen-spanning garbage
// triangle live). If the engine multi-submit path is the bug, the tentacles garbage here too.
// Pass --shot to auto-capture the OFFSCREEN preview texture to /tmp/slath_offscreen.ppm on frame 30 and exit
// (lets the investigating session iterate via PNG instead of manual screenshots, and shows whether the garbage
// is in the offscreen render itself or only the swapchain composite).
bool shot = args.Contains("--shot");
var opts = new GameAppOptions { Title = "Slath Skinned Multi-Submit Repro", Width = 1280, Height = 720 };
using var app = new SlathReproApp(opts, shot);
app.Run();

sealed class SlathReproApp : GameApp
{
    Render3DPreview _preview = null!;
    MeshHandle _box;
    SkinnedMeshHandle _tube;
    SkinnedGltfMesh _tubeMesh = null!;
    readonly bool _shot;
    int _frame;

    public SlathReproApp(in GameAppOptions o, bool shot) : base(o) { _shot = shot; }

    protected override void OnLoad()
    {
        // Offscreen world at 2x supersample, MatchViewport + transparent (SpaceGame's MeshSceneRenderer config).
        _preview = new Render3DPreview(Window, 2560, 1440);
        _preview.Scene.Post.RenderScale = RenderScale.MatchViewport;
        _preview.Scene.Post.TransparentBackground = true;
        _preview.Scene.Post.LightDirection = new Vector3(-0.12f, -0.98f, -0.15f);

        _box = _preview.Scene.LoadMesh(MeshPrimitives.Box(1.0f));        // the "cores" (rigid)
        _tubeMesh = SkinnedMeshBuilder.BuildTube(0.2f, 1.3f, 24, 8, 8, Axis.X); // the game's exact tentacle mesh
        _tube = _preview.Scene.LoadSkinnedMesh(_tubeMesh);
        _preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(10f, 7f, 7f));
    }

    // SUBMIT #1: render the offscreen world (Render3DPreview.Capture submits its own command list).
    protected override void OnRenderWorld(Frame frame)
    {
        _preview.Capture(s =>
        {
            // rigid cores (top row) - these render fine in SpaceGame
            for (int i = 0; i < 6; i++)
                s.Draw(_box, Matrix4x4.CreateTranslation(-4f + 1.6f * i, 3f, 0), new Color(0.4f, 0.7f, 1f, 1f));
            // skinned tentacles - many, distinct positions, RestPose (the garbage case)
            for (int i = 0; i < 80; i++)
            {
                float x = -4.5f + 9f * (i % 10) / 10f;
                float y = -3f + 4f * ((i / 10) % 8) / 8f;
                var model = Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateTranslation(x, y, 0);
                s.DrawSkinned(_tube, _tubeMesh.RestPose, model, new Color(0.3f, 0.9f, 0.4f, 1f));
            }
        });
    }

    // SUBMIT #2: composite the offscreen world to the swapchain as a full-screen textured quad, then present.
    protected override void OnDraw2D(SpriteBatch batch)
    {
        batch.Draw(_preview.Texture, new Vector4(0, 0, Viewport.Width, Viewport.Height), Color.White);

        if (_shot && ++_frame == (int.TryParse(System.Environment.GetEnvironmentVariable("SLATH_SHOT_FRAME"), out var sf) ? sf : 30))
        {
            // Read back the OFFSCREEN preview texture (what the skinned multi-submit produced) and dump a PPM.
            int w = _preview.Width, h = _preview.Height;
            byte[] px = _preview.ReadbackRgba();
            using (var fs = new FileStream("/tmp/slath_offscreen.ppm", FileMode.Create))
            {
                byte[] hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
                fs.Write(hdr, 0, hdr.Length);
                byte[] rgb = new byte[w * h * 3];
                for (int i = 0; i < w * h; i++) { rgb[i*3]=px[i*4]; rgb[i*3+1]=px[i*4+1]; rgb[i*3+2]=px[i*4+2]; }
                fs.Write(rgb, 0, rgb.Length);
            }
            int colored = 0; for (int p = 0; p < w*h; p++) if (px[p*4]>20||px[p*4+1]>20||px[p*4+2]>20) colored++;
            Console.WriteLine($"SHOT: offscreen {w}x{h} colored={colored} -> /tmp/slath_offscreen.ppm");
            Quit();
        }
    }

    protected override void OnDispose() => _preview.Dispose();
}
