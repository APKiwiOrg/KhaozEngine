using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using KhaozEngine.Gpu.TestKit;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// On-demand contact sheets of the tile-world river look with caustics on, so the owner can pick the river's strength.
/// Each cell is a top-down daylight capture of a flat sandy bed at one depth and one strength, at two effect times half
/// a second apart so the pattern reads as moving. The numbers beside each cell are measured against the same look with
/// the caustic opt-in off, which is the strength-0 reference.
/// </summary>
public sealed class WaterCausticsContactSheetGpuTests(WaterColumnRiverScene scene, ITestOutputHelper output)
    : IClassFixture<WaterColumnRiverScene>
{
    const int Border = 8;
    static readonly float[] Depths = [0.375f, 0.75f, 1.5f];
    static readonly float[] Strengths = [1f, 2f, 4f, 6f, 8f];
    static readonly float[] Times = [0f, 0.5f];

    public static string OutputDirectory => Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
        "khaozengine-water-optics", "m3-contact");

    [Trait("Category", "Diagnostic")]
    [WaterCausticsContactSheetFact]
    public void River_caustic_cells_are_written_and_carry_bands()
    {
        CreatePrivateDirectory(OutputDirectory);
        var cells = new List<Cell>();
        var written = new List<string>();
        var table = new List<string> { "strength\tdepth\trms_t0\trms_t05\tmean_t0\tmotion_rms\tfiles" };
        foreach (float depth in Depths)
        {
            float[][] reference = new float[Times.Length][];
            string[] referenceFiles = new string[Times.Length];
            for (int t = 0; t < Times.Length; t++)
            {
                WaterColumnImage off = scene.Capture(Case(depth, Times[t], strength: null));
                reference[t] = Luminance(off);
                referenceFiles[t] = Save(off, 0f, depth, Times[t]);
            }
            table.Add(Row(0f, depth, 0d, 0d, 1d, 0d, referenceFiles));
            written.AddRange(referenceFiles);
            foreach (float strength in Strengths)
            {
                double[] rms = new double[Times.Length], mean = new double[Times.Length];
                double[][] ratio = new double[Times.Length][];
                string[] files = new string[Times.Length];
                for (int t = 0; t < Times.Length; t++)
                {
                    WaterColumnImage on = scene.Capture(Case(depth, Times[t], strength));
                    files[t] = Save(on, strength, depth, Times[t]);
                    ratio[t] = Ratio(Luminance(on), reference[t]);
                    (rms[t], mean[t]) = (Rms(ratio[t], 1d), Mean(ratio[t]));
                }
                double motion = Rms(Difference(ratio[1], ratio[0]), 0d);
                table.Add(Row(strength, depth, rms[0], rms[1], mean[0], motion, files));
                output.WriteLine($"strength={strength}, depth={depth} m, rms={rms[0]:F4}/{rms[1]:F4}, mean={mean[0]:F4}, motion rms={motion:F4}");
                cells.Add(new Cell(strength, depth, Math.Min(rms[0], rms[1]), motion));
                written.AddRange(files);
            }
        }
        File.WriteAllLines(Path.Combine(OutputDirectory, "rms.tsv"), table);

        Assert.Equal(Depths.Length * (Strengths.Length + 1) * Times.Length, written.Count);
        Assert.All(written, file => Assert.True(new FileInfo(file).Length > 0, file));
        Assert.Equal(Depths.Length * Strengths.Length, cells.Count);
        Assert.All(cells, cell =>
        {
            Assert.True(cell.Rms > 0d, $"strength {cell.Strength} at {cell.Depth} m carries no bands");
            Assert.True(cell.Motion > 0d, $"strength {cell.Strength} at {cell.Depth} m does not move");
        });
    }

    static WaterColumnCase Case(float depth, float time, float? strength) => new()
    {
        Depth = depth,
        Time = time,
        SunElevationDegrees = 50f,
        Caster = false,
        TopDown = true,
        Look = RiverWithCaustics(strength),
    };

    /// <summary>A copy of the shared river look, which must never be mutated, with the column and caustics opted in.
    /// A null strength leaves caustics off for the reference.</summary>
    static WaterLook RiverWithCaustics(float? strength)
    {
        var look = new WaterLook();
        foreach (FieldInfo field in typeof(WaterLook).GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(look, field.GetValue(TileWaterLooks.River));
        look.WaterColumnLight = true;
        look.UnderwaterCaustics = strength.HasValue;
        look.CausticStrength = strength;
        return look;
    }

    string Save(WaterColumnImage image, float strength, float depth, float time)
    {
        string path = Path.Combine(OutputDirectory, string.Create(CultureInfo.InvariantCulture,
            $"s{strength:0}_d{depth:0.000}_t{time:0.0}.png"));
        PngWriter.Save(path, image.Rgba, image.Size, image.Size);
        return path;
    }

    static string Row(float strength, float depth, double rms0, double rms1, double mean, double motion, string[] files) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{strength:0}\t{depth:0.000}\t{rms0:F4}\t{rms1:F4}\t{mean:F4}\t{motion:F4}\t{string.Join(",", Array.ConvertAll(files, f => Path.GetFileName(f)))}");

    /// <summary>Rec. 709 luminance of the linear HDR colour, interior pixels only.</summary>
    static float[] Luminance(WaterColumnImage image)
    {
        int size = image.Size, width = size - 2 * Border;
        float[] luminance = new float[width * width];
        for (int y = 0; y < width; y++)
            for (int x = 0; x < width; x++)
                luminance[y * width + x] = WaterColumnGpuTests.L(image.Pixel(x + Border, y + Border));
        return luminance;
    }

    static double[] Ratio(float[] on, float[] off)
    {
        Assert.Equal(off.Length, on.Length);
        double[] ratio = new double[on.Length];
        for (int i = 0; i < on.Length; i++) ratio[i] = off[i] > 1e-6f ? on[i] / (double)off[i] : 1d;
        return ratio;
    }

    static double[] Difference(double[] a, double[] b)
    {
        double[] difference = new double[a.Length];
        for (int i = 0; i < a.Length; i++) difference[i] = a[i] - b[i];
        return difference;
    }

    static double Rms(double[] values, double centre)
    {
        double sum = 0d;
        foreach (double v in values) sum += (v - centre) * (v - centre);
        return Math.Sqrt(sum / values.Length);
    }

    static double Mean(double[] values)
    {
        double sum = 0d;
        foreach (double v in values) sum += v;
        return sum / values.Length;
    }

    static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    sealed record Cell(float Strength, float Depth, double Rms, double Motion);
}

/// <summary>Skipped unless both the GPU gate and <c>KE_WATER_CAUSTIC_CONTACT=1</c> are set, so default CI never runs it.</summary>
public sealed class WaterCausticsContactSheetFactAttribute : FactAttribute
{
    public WaterCausticsContactSheetFactAttribute()
    {
        string? gpu = GpuTestGate.SkipReason();
        if (gpu is not null) Skip = gpu;
        else if (Environment.GetEnvironmentVariable("KE_WATER_CAUSTIC_CONTACT") != "1")
            Skip = "Manual-only contact sheet. Set KE_WATER_CAUSTIC_CONTACT=1 with KE_GPU_TESTS.";
    }
}
