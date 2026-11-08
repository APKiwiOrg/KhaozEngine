using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KhaozEngine.Imaging;

namespace KhaozEngine.Tests.Gpu;

internal static class RigidPointShadowEvidence
{
    internal sealed record Pixel(int X, int Y, int[] Split, int[] Full, int[] Delta);
    internal sealed record PixelReport(int ChangedPixels, int ChangedChannels, int MaximumDelta, Pixel[] FirstPixels);
    internal sealed record Atlas(int Width, int Height, int FaceResolution, int[] Rows, float[] Values)
    {
        internal float Read(int light, int face, int x, int y) =>
            Values[(Rows[light] * FaceResolution + y) * Width + face * FaceResolution + x];
    }
    internal sealed record Texel(int Light, int Face, int X, int Y, uint BaseBits, uint? TransientBits, uint CombinedBits, uint FullBits);
    internal sealed record DepthReport(int ComparedTexels, int MismatchedTexels, int ClearCoveredDisagreements,
        double MaximumFiniteAbsoluteDelta, ulong MaximumFiniteUlpDelta, int BaseNonfinite, int TransientNonfinite,
        int CombinedNonfinite, int FullNonfinite, Texel[] FirstTexels);
    internal sealed record ReceiverSite(int X, int Y, double NominalWeight);
    internal static ReceiverSite[] ReceiverFootprint(int x, int y, int outputWidth, int outputHeight,
        int sourceWidth, int sourceHeight, bool flipV)
    {
        double u = (x + 0.5) / outputWidth, v = (y + 0.5) / outputHeight;
        double sx = u * sourceWidth - 0.5, sy = (flipV ? 1 - v : v) * sourceHeight - 0.5;
        int left = (int)Math.Floor(sx), top = (int)Math.Floor(sy);
        double fx = sx - left, fy = sy - top;
        return
        [
            Site(0, 0, (1 - fx) * (1 - fy)), Site(1, 0, fx * (1 - fy)),
            Site(0, 1, (1 - fx) * fy), Site(1, 1, fx * fy),
        ];
        ReceiverSite Site(int dx, int dy, double weight) =>
            new(Math.Clamp(left + dx, 0, sourceWidth - 1), Math.Clamp(top + dy, 0, sourceHeight - 1), weight);
    }

    internal static PixelReport ComparePixels(byte[] split, byte[] full, int width, int height)
    {
        if (split.Length != checked(width * height * 4) || full.Length != split.Length)
            throw new ArgumentException("pixel buffers do not match the image dimensions");
        int pixels = 0, channels = 0, maximum = 0;
        var first = new List<Pixel>();
        for (int p = 0; p < width * height; p++)
        {
            int before = channels;
            for (int c = 0; c < 4; c++)
            {
                int delta = Math.Abs(split[p * 4 + c] - full[p * 4 + c]);
                maximum = Math.Max(maximum, delta);
                if (delta == 0) continue;
                channels++;
            }
            if (channels == before) continue;
            pixels++;
            if (first.Count < 16)
            {
                int[] delta = new int[4];
                for (int c = 0; c < 4; c++) delta[c] = Math.Abs(split[p * 4 + c] - full[p * 4 + c]);
                first.Add(new(p % width, p / width, split.AsSpan(p * 4, 4).ToArray().Select(value => (int)value).ToArray(),
                    full.AsSpan(p * 4, 4).ToArray().Select(value => (int)value).ToArray(), delta));
            }
        }
        return new(pixels, channels, maximum, first.ToArray());
    }

    internal static DepthReport CompareDepth(Atlas split, Atlas? transient, Atlas full, int retain = 16)
    {
        Validate(split);
        Validate(full);
        if (transient is not null) Validate(transient);
        if (full.FaceResolution != split.FaceResolution || full.Rows.Length != split.Rows.Length
            || (transient is not null && (transient.FaceResolution != split.FaceResolution || transient.Rows.Length != split.Rows.Length)))
            throw new ArgumentException("canonical atlas layouts do not match");
        int compared = 0, mismatched = 0, clearCovered = 0, baseNonfinite = 0, transientNonfinite = 0;
        int combinedNonfinite = 0, fullNonfinite = 0;
        double maxAbsolute = 0;
        ulong maxUlp = 0;
        var first = new List<Texel>();
        for (int light = 0; light < split.Rows.Length; light++)
        {
            if (split.Rows[light] < 0 || full.Rows[light] < 0) continue;
            for (int face = 0; face < 6; face++)
                for (int y = 0; y < split.FaceResolution; y++)
                    for (int x = 0; x < split.FaceResolution; x++)
                    {
                        float b = split.Read(light, face, x, y), f = full.Read(light, face, x, y);
                        float? t = transient is not null && transient.Rows[light] >= 0 ? transient.Read(light, face, x, y) : null;
                        float combined = t is float depth ? MathF.Min(b, depth) : b;
                        compared++;
                        if (!float.IsFinite(b)) baseNonfinite++;
                        if (t is float current && !float.IsFinite(current)) transientNonfinite++;
                        if (!float.IsFinite(combined)) combinedNonfinite++;
                        if (!float.IsFinite(f)) fullNonfinite++;
                        if ((combined == 1f && float.IsFinite(f) && f < 1f)
                            || (f == 1f && float.IsFinite(combined) && combined < 1f)) clearCovered++;
                        if (float.IsFinite(combined) && float.IsFinite(f))
                        {
                            maxAbsolute = Math.Max(maxAbsolute, Math.Abs((double)combined - f));
                            ulong a = Ordered(combined), z = Ordered(f);
                            maxUlp = Math.Max(maxUlp, a >= z ? a - z : z - a);
                        }
                        if (Bits(combined) == Bits(f)) continue;
                        mismatched++;
                        if (first.Count < retain)
                            first.Add(new(light, face, x, y, Bits(b), t is float value ? Bits(value) : null, Bits(combined), Bits(f)));
                    }
        }
        return new(compared, mismatched, clearCovered, maxAbsolute, maxUlp, baseNonfinite, transientNonfinite,
            combinedNonfinite, fullNonfinite, first.ToArray());
    }

    // Gzip payload: little-endian int32 version, width, height, face size, assigned-light count.
    // Each assigned light then stores int32 light index and source row, followed by six faces of uint32 float
    // bits in face/Y/X order. Unassigned and spare rows have no records.
    internal static void WriteCells(string file, Atlas atlas)
    {
        Validate(atlas);
        using var zipped = new GZipStream(File.Create(file), CompressionLevel.SmallestSize);
        using var writer = new BinaryWriter(zipped);
        writer.Write(1);
        writer.Write(atlas.Width);
        writer.Write(atlas.Height);
        writer.Write(atlas.FaceResolution);
        writer.Write(atlas.Rows.Count(row => row >= 0));
        for (int light = 0; light < atlas.Rows.Length; light++)
        {
            if (atlas.Rows[light] < 0) continue;
            writer.Write(light);
            writer.Write(atlas.Rows[light]);
            for (int face = 0; face < 6; face++)
                for (int y = 0; y < atlas.FaceResolution; y++)
                    for (int x = 0; x < atlas.FaceResolution; x++) writer.Write(Bits(atlas.Read(light, face, x, y)));
        }
    }

    internal static string Begin(string backend, string filter, string testCase, string scenario,
        byte[] split, byte[] full, int width, int height, object metadata, [CallerFilePath] string source = "")
    {
        string root = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR")
            ?? Path.Combine(Path.GetDirectoryName(source)!, "goldens-evidence");
        string directory = Path.Combine(root, $"rigid-point.{backend}.{filter}.{testCase}.{scenario}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        PixelReport pixels = ComparePixels(split, full, width, height);
        WriteJson(Path.Combine(directory, "pixels.json"), new { Backend = backend, Filter = filter, TestCase = testCase, Scenario = scenario, Width = width, Height = height, Pixels = pixels, Metadata = metadata });
        PngWriter.Save(Path.Combine(directory, "split.png"), split, width, height);
        PngWriter.Save(Path.Combine(directory, "full.png"), full, width, height);
        var difference = new byte[split.Length];
        for (int p = 0; p < width * height; p++)
        {
            for (int c = 0; c < 3; c++) difference[p * 4 + c] = (byte)Math.Min(255, Math.Abs(split[p * 4 + c] - full[p * 4 + c]) * 32);
            difference[p * 4 + 3] = 255;
        }
        PngWriter.Save(Path.Combine(directory, "difference-x32.png"), difference, width, height);
        return directory;
    }

    internal static void Complete(string directory, Atlas split, Atlas? transient, Atlas full, object receivers)
    {
        WriteCells(Path.Combine(directory, "split-base.r32.gz"), split);
        if (transient is not null) WriteCells(Path.Combine(directory, "split-transient.r32.gz"), transient);
        WriteCells(Path.Combine(directory, "full-base.r32.gz"), full);
        WriteJson(Path.Combine(directory, "depth.json"), new
        {
            Schema = "r32-gzip-v1: int32LE version,width,height,faceResolution,assignedCount then light,row and uint32LE bits in face/Y/X order",
            Comparison = "bit equality of full depth versus MathF.Min(base,assigned transient), base only when unassigned",
            UlpOrder = "negative float words inverted, nonnegative words sign-bit flipped, signed zeros one word apart",
            Split = Describe(split),
            Transient = transient is null ? null : Describe(transient),
            Full = Describe(full),
            Depth = CompareDepth(split, transient, full),
            Receivers = receivers,
        });
    }

    internal static void WriteJson(string file, object value) => File.WriteAllText(file,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));

    static object Describe(Atlas atlas) => new { atlas.Width, atlas.Height, atlas.FaceResolution, atlas.Rows };
    static uint Bits(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));
    static uint Ordered(float value) { uint bits = Bits(value); return (bits & 0x80000000) != 0 ? ~bits : bits ^ 0x80000000; }

    static void Validate(Atlas atlas)
    {
        if (atlas.FaceResolution <= 0 || atlas.Width != 6 * atlas.FaceResolution || atlas.Height % atlas.FaceResolution != 0
            || atlas.Values.Length != checked(atlas.Width * atlas.Height)
            || Array.Exists(atlas.Rows, row => row < -1 || row >= atlas.Height / atlas.FaceResolution))
            throw new ArgumentException("atlas dimensions or assigned rows are invalid");
    }
}
