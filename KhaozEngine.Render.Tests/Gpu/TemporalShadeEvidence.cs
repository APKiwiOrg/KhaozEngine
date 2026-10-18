using System;
using System.IO;
using KhaozEngine.Imaging;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Optional paired frames for attributing a guard change to pixels rather than widening its bound.</summary>
internal static class TemporalShadeEvidence
{
    internal static void Save(string name, byte[][] frames, byte[][] reference, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("KE_TEMPORAL_SHADE_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        for (int i = 0; i < frames.Length; i++)
        {
            PngWriter.Save(Path.Combine(directory, $"{name}-{i:00}.png"), frames[i], width, height);
            PngWriter.Save(Path.Combine(directory, $"{name}-ref-{i:00}.png"), reference[i], width, height);
        }
    }
}
