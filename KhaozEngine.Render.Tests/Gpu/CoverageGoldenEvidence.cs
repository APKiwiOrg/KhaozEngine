using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Imaging;

namespace KhaozEngine.Tests.Gpu;

/// <summary>No-bake candidate packet. Original full-resolution captures are paired with it during exact delta review.</summary>
internal static class CoverageGoldenEvidence
{
    internal static void Write(string name, CoverageCapture capture)
    {
        if (CoverageArtifacts.DirectoryPath is null) return;
        string backend = GoldenCompare.GoldenBackendToken(capture.Backend);
        string stem = $"golden-review/{name}.{backend}";
        byte[] reference = File.ReadAllBytes(GoldenCompare.GoldenPath(name));
        float[] actual = GoldenCompare.Downsample(capture.Colour, CoverageGpuScene.Width, CoverageGpuScene.Height);
        float[] expected = GoldenGrid.Deserialize(Encoding.UTF8.GetString(reference));
        float worst = GoldenGrid.Compare(actual, expected, GoldenCompare.Tolerance).WorstDiff;
        byte[] grid = Encoding.UTF8.GetBytes(GoldenGrid.Serialize(actual, GoldenCompare.GridW, GoldenCompare.GridH));
        byte[] png = PngWriter.Encode(capture.Colour, CoverageGpuScene.Width, CoverageGpuScene.Height);
        CoverageArtifacts.WriteFile(stem + ".candidate.png", png);
        CoverageArtifacts.WriteFile(stem + ".candidate.txt", grid);
        CoverageArtifacts.WriteFile(stem + ".candidate-grid.f32", MemoryMarshal.AsBytes(actual.AsSpan()).ToArray());
        CoverageArtifacts.WriteJson(stem + ".json", new
        {
            Backend = backend,
            View = name,
            capture.Artifact,
            capture.SubmittedSurfaceSignature,
            ReferenceGridSha256 = Hash(reference),
            CandidateGridSha256 = Hash(grid),
            CandidateImageSha256 = Hash(png),
            CandidateRgbaSha256 = Hash(capture.Colour),
            WorstDelta = worst,
            Tolerance = GoldenCompare.Tolerance,
            BakeRequested = Environment.GetEnvironmentVariable("KE_UPDATE_GOLDENS") == "1",
            Explanation = worst == 0 ? "Unchanged grid" : "Requires comparison with the original full-resolution capture and source-supported disposition",
        });
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
