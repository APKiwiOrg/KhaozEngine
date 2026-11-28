using System;
using System.IO;
using KhaozEngine.Imaging;

namespace KhaozEngine.Tests.GpuCore;

/// <summary>
/// Compares a native core capture with the committed <c>vulkan-native</c> golden grid, read only. The grids are
/// owned by the managed golden suite and are never written from here.
/// </summary>
internal static class CoreGoldenCheck
{
    public const float Tolerance = 0.01f;

    public static GoldenGridComparison Compare(string scene, byte[] rgba, int width, int height)
    {
        string path = Path.Combine(RepositoryRoot(), "KhaozEngine.Render.Tests", "Gpu", "goldens",
            scene + ".vulkan-native.txt");
        float[] got = GoldenGrid.Downsample(rgba, width, height);
        float[] want = GoldenGrid.Deserialize(File.ReadAllText(path));
        return GoldenGrid.Compare(got, want, Tolerance);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("KhaozEngine.slnx was not found above the test output.");
    }
}
