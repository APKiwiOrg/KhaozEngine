using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Replay device readbacks through the same acceptance checks without creating a different GPU class.</summary>
public sealed class TemporalShadowFilterBaselineTests
{
    [Theory]
    [InlineData("hardware", "bilinear-metal.txt")]
    [InlineData("hardware", "bilinear-metal-hosted.txt")]
    [InlineData("WARP", "bilinear-d3d11.txt")]
    [InlineData("lavapipe", "bilinear-vulkan.txt")]
    public void Measured_bilinear_controls_pass_their_reference_and_rim_checks(string device, string file)
    {
        string[] rows = File.ReadLines(ControlPath(file)).Select(line => line.TrimStart())
            .Where(line => line.StartsWith("RIM ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(56, rows.Length);
        var names = new HashSet<string>();
        var failures = new List<string>();
        foreach (string row in rows)
        {
            string[] fields = row.Split('|');
            Assert.Equal(6, fields.Length);
            Assert.Equal($"RIM {device}", fields[0]);
            Assert.True(names.Add(fields[1]), $"Duplicate control {fields[1]}");
            double Parse(int i) => double.Parse(fields[i], CultureInfo.InvariantCulture);
            TemporalReviewBaseline.Check(device, "rim", fields[1], new(Parse(2), Parse(3), Parse(4), Parse(5)), failures);
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("unmeasured-device", "Native M 0.5 leading flat")]
    [InlineData("hardware", "unmeasured-case")]
    public void Missing_device_or_case_fails_without_borrowing_another_control(string device, string name)
    {
        var failures = new List<string>();
        TemporalReviewBaseline.Check(device, "rim", name, new(0, 0, 0, 0), failures);
        Assert.Single(failures);
        Assert.Contains("Missing", failures[0]);
    }

    [Fact]
    public void Historical_diagonal_checks_still_bound_raw_change()
    {
        var failures = new List<string>();
        TemporalReviewBaseline.Check("hardware", "diagonal", "Native 30deg 2h pan0.0",
            new(0.001098, 0.000193, 0.000193, 0), failures);
        Assert.Empty(failures);
        TemporalReviewBaseline.Check("hardware", "diagonal", "Native 30deg 2h pan0.0",
            new(0.001098, 0.000300, 0.000193, 0), failures);
        Assert.Single(failures);
        Assert.Contains("change", failures[0]);
    }

    static string ControlPath(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "measurements", "taa-shadow-visibility", file);
}
