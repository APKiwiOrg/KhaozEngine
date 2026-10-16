using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageArtifacts
{
    internal static void Write(CoverageCapture capture)
    {
        string directory = DirectoryPath();
        Directory.CreateDirectory(directory);
        string root = Path.Combine(directory, capture.Artifact + "." + capture.Backend);
        Save(root + ".colour.png", PngWriter.Encode(capture.Colour, CoverageGpuScene.Width, CoverageGpuScene.Height));
        if (capture.Motion is { } motion)
            Target(root, "motion", new CoverageTargetReadback(motion.Width, motion.Height, 2,
                MemoryMarshal.Cast<System.Numerics.Vector2, float>(motion.Motion.AsSpan()).ToArray()));
        Target(root, "reconstruction-source", capture.ReconstructionColour);
        Target(root, "depth", capture.Depth);
        Target(root, "normal", capture.Normal);
        Target(root, "cascade", capture.Cascades);
        Target(root, "point-base", capture.Points);
        Target(root, "point-transient", capture.TransientPoints);
        Target(root, "outline-full", capture.FullOutline);
        Target(root, "outline-visible", capture.VisibleOutline);
        Save(root + ".json", System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Backend = capture.Backend.ToString(),
            capture.Device,
            capture.SubmittedSurfaceSignature,
            capture.Artifact,
            MetalCache = Environment.GetEnvironmentVariable("KE_METAL_MSL_CACHE") ?? "default",
            SpirvCache = Environment.GetEnvironmentVariable("KE_SPIRV_CACHE") ?? "default",
            DrawCalls = capture.FrameStats.DrawCalls,
            TemporalFrame = capture.Temporal.FrameIndex,
            JitterX = capture.Jitter.X,
            JitterY = capture.Jitter.Y,
            PointBaseRow = capture.Points?.SourceRow,
            PointTransientRow = capture.TransientPoints?.SourceRow,
            Width = CoverageGpuScene.Width,
            Height = CoverageGpuScene.Height,
        }, new JsonSerializerOptions { WriteIndented = true })));
        Console.WriteLine($"coverage artifact={root} backend={capture.Backend} device={capture.Device}");
    }

    internal static string DirectoryPath([CallerFilePath] string source = "") =>
        Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR")
        ?? Path.Combine(Path.GetDirectoryName(source)!, "goldens-evidence");

    static void Save(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path);
    }

    static void Target(string root, string pass, CoverageTargetReadback? target)
    {
        if (target is null) return;
        Save(root + "." + pass + ".f32", MemoryMarshal.AsBytes(target.Values.AsSpan()).ToArray());
        var rgba = new byte[target.Width * target.Height * 4];
        for (int i = 0; i < target.Width * target.Height; i++)
        {
            for (int c = 0; c < 3; c++)
                rgba[i * 4 + c] = (byte)(Math.Clamp(target.Values[i * target.Channels + Math.Min(c, target.Channels - 1)], 0, 1) * 255);
            rgba[i * 4 + 3] = 255;
        }
        Save(root + "." + pass + ".png", PngWriter.Encode(rgba, target.Width, target.Height));
    }
}
