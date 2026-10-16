using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The one writer for raw coverage evidence. Nothing is written unless <see cref="DirectoryVariable"/> names a
/// directory, so ordinary native runs keep every assertion and readback but leave no packet on disk. The golden
/// override <c>KE_GOLDEN_EVIDENCE_DIR</c> never enables it. Every file is promoted atomically from a temporary name.
/// </summary>
internal static class CoverageArtifacts
{
    internal const string DirectoryVariable = "KE_COVERAGE_EVIDENCE_DIR";

    internal static string? DirectoryPath => Resolve(Environment.GetEnvironmentVariable(DirectoryVariable));

    internal static string? Resolve(string? configured) => string.IsNullOrWhiteSpace(configured) ? null : configured;

    internal static void Write(CoverageCapture capture)
    {
        if (DirectoryPath is not { } directory) return;
        Directory.CreateDirectory(directory);
        string root = Path.Combine(directory, capture.Artifact + "." + capture.Backend);
        Save(root + ".colour.png", PngWriter.Encode(capture.Colour, CoverageGpuScene.Width, CoverageGpuScene.Height));
        if (capture.Motion is { } motion)
            Target(root, "motion", new CoverageTargetReadback(motion.Width, motion.Height, 2,
                MemoryMarshal.Cast<System.Numerics.Vector2, float>(motion.Motion.AsSpan()).ToArray()));
        if (capture.ResolvedColour is not null)
        {
            var uniforms = capture.ResolveUniforms;
            WriteRaw(Path.GetFileName(root) + ".resolve-uniforms.f32",
                MemoryMarshal.Cast<KhaozEngine.Render3D.Internal.TemporalResolveUniforms, float>(MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1)));
        }
        Target(root, "reconstruction-source", capture.ReconstructionColour);
        Target(root, "resolved-colour", capture.ResolvedColour);
        Target(root, "resolved-state", capture.ResolvedState);
        Target(root, "depth", capture.Depth);
        Target(root, "normal", capture.Normal);
        Target(root, "cascade", capture.Cascades);
        Target(root, "point-base", capture.Points);
        Target(root, "point-transient", capture.TransientPoints);
        Target(root, "outline-full", capture.FullOutline);
        Target(root, "outline-visible", capture.VisibleOutline);
        if (capture.RejectionMask is { } mask)
        {
            Save(root + ".rejection-mask.r8", mask.Values);
            var rgba = new byte[mask.Width * mask.Height * 4];
            for (int i = 0; i < mask.Values.Length; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = mask.Values[i];
                rgba[i * 4 + 3] = 255;
            }
            Save(root + ".rejection-mask.png", PngWriter.Encode(rgba, mask.Width, mask.Height));
        }
        WriteJson(Path.GetFileName(root) + ".json", new
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
            ResolveJitterX = capture.ResolveJitter.X,
            ResolveJitterY = capture.ResolveJitter.Y,
            capture.OutlineGroupsRendered,
            MaskWidth = capture.RejectionMask?.Width,
            MaskHeight = capture.RejectionMask?.Height,
            PointBaseRow = capture.Points?.SourceRow,
            PointTransientRow = capture.TransientPoints?.SourceRow,
            Width = CoverageGpuScene.Width,
            Height = CoverageGpuScene.Height,
        });
        Console.WriteLine($"coverage artifact={root} backend={capture.Backend} device={capture.Device}");
    }

    internal static void WriteJson(string name, object value)
    {
        if (DirectoryPath is not { } directory) return;
        Directory.CreateDirectory(directory);
        Save(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void WriteRaw(string name, ReadOnlySpan<float> values)
    {
        if (DirectoryPath is not { } directory) return;
        Directory.CreateDirectory(directory);
        Save(Path.Combine(directory, name), MemoryMarshal.AsBytes(values).ToArray());
    }

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
