using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Snapshot;

namespace SnapshotTool;

/// <summary>
/// Canonical reference for the KhaozEngine snapshot harness (the shape every game's tools/SnapshotTool
/// mirrors). The default form is register-the-shots:
/// <see cref="SnapshotHost"/> resolves the output dir from <c>args[0]</c> (a temp default otherwise), runs
/// each shot headless (no window), writes a PNG per shot, logs each path, and prints the final summary.
/// Needs a GPU device (the underlying captures use Veldrid/Metal).
/// Run: <c>dotnet run --project SnapshotTool -- /tmp/ke-snapshot-demo</c>
/// <para>
/// Two GPU-free subcommands sit in front of the default render form (see <see cref="DiffCommands"/>):
/// <c>diff &lt;a.png&gt; &lt;b.png&gt;</c> compares two rendered PNGs and <c>score &lt;image.png&gt;
/// &lt;golden.txt&gt;</c> compares a PNG against a committed golden grid. Both exit 0 within tolerance, 1 over,
/// 2 on usage/IO error. Anything else is the original render form, unchanged.
/// </para>
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "diff":
                    return DiffCommands.Diff(args[1..], Console.WriteLine);
                case "score":
                    return DiffCommands.Score(args[1..], Console.WriteLine);
            }
        }
        return SnapshotHost.Main(args, Register);
    }

    static void Register(SnapshotRunner runner)
    {
        // One 2D shot: a couple of flat coloured rects (no font/asset -> self-contained + deterministic).
        runner.Shot2D("hello2d", 320, 200, new Color(0.08f, 0.09f, 0.12f, 1f), ctx =>
        {
            Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            ctx.Batch.Begin();
            ctx.Batch.Draw(white, new Vector4(30, 30, 120, 70), new Color(0.85f, 0.25f, 0.25f, 1f));
            ctx.Batch.Draw(white, new Vector4(170, 100, 110, 70), new Color(0.25f, 0.7f, 0.4f, 1f));
            ctx.Batch.End();
        });

        // One 3D shot (the Shot3D extension lives in KhaozEngine.Snapshot.Render3D): a single lit box.
        MeshHandle box = default;
        runner.Shot3D("hello3d", 320, 200,
            setup: scene =>
            {
                box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                scene.Camera.Frame(Vector3.Zero, new Vector3(3f, 3f, 3f));
            },
            drawFrame: scene => scene.Draw(box, Matrix4x4.Identity, new Color(0.3f, 0.55f, 0.9f, 1f)),
            frames: 2);
    }
}
