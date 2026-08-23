using System;
using System.IO;
using System.Runtime.CompilerServices;
using KhaozEngine.Imaging;
using Xunit;

namespace KhaozEngine.Tests.Imaging
{
    public class GoldenGridTests
    {
        // A tiny synthetic RGBA buffer: 4x2 with distinct rows, enough to exercise downsample averaging.
        static byte[] SampleRgba() => new byte[]
        {
            255, 0,   0,   255,   0,   255, 0,   255,   200, 100, 50,  255,   10, 20, 30, 255, // row 0
            0,   0,   255, 255,   255, 255, 0,   255,   40,  40,  40,  255,   90, 80, 70, 255, // row 1
        };

        [Fact]
        public void Downsample_averages_source_pixels_per_cell()
        {
            // 4x2 source into a 2x1 grid: each cell averages 2x2 = 4 pixels.
            float[] grid = GoldenGrid.Downsample(SampleRgba(), 4, 2, 2, 1);
            Assert.Equal(2 * 1 * 3, grid.Length);
            // Left cell R: (255 + 0 + 0 + 255) / 4 / 255 = 0.5
            Assert.Equal(0.5f, grid[0], 3);
        }

        [Fact]
        public void Downsample_default_grid_size_is_32x18()
        {
            byte[] rgba = new byte[16 * 9 * 4];
            float[] grid = GoldenGrid.Downsample(rgba, 16, 9);
            Assert.Equal(GoldenGrid.DefaultGridW * GoldenGrid.DefaultGridH * 3, grid.Length);
        }

        [Fact]
        public void Compare_flags_only_over_tolerance_channels_and_reports_worst()
        {
            float[] a = { 0.10f, 0.20f, 0.30f };
            float[] b = { 0.10f, 0.29f, 0.50f }; // G diff 0.09, B diff 0.20
            var r = GoldenGrid.Compare(a, b, 0.06f);
            Assert.False(r.Passed);
            Assert.Equal(2, r.Offenders.Count);
            Assert.Equal(0.20f, r.WorstDiff, 3);
            // Sorted worst-first: B (channel 2) is the largest.
            Assert.Equal(2, r.Offenders[0].Channel);
            Assert.Equal(0.20f, r.Offenders[0].Diff, 3);
        }

        [Fact]
        public void Compare_passes_when_within_tolerance()
        {
            float[] a = { 0.10f, 0.20f, 0.30f };
            float[] b = { 0.12f, 0.18f, 0.34f };
            var r = GoldenGrid.Compare(a, b, 0.06f);
            Assert.True(r.Passed);
            Assert.Empty(r.Offenders);
            Assert.Equal(0.04f, r.WorstDiff, 3);
        }

        [Fact]
        public void Serialize_then_Deserialize_round_trips_values()
        {
            float[] grid = GoldenGrid.Downsample(SampleRgba(), 4, 2, 2, 1);
            string text = GoldenGrid.Serialize(grid, 2, 1);
            float[] back = GoldenGrid.Deserialize(text);
            Assert.Equal(grid.Length, back.Length);
            for (int i = 0; i < grid.Length; i++)
                Assert.Equal(grid[i], back[i], 4); // 4-decimal text precision
        }

        [Fact]
        public void Serialize_header_is_the_canonical_line()
        {
            string text = GoldenGrid.Serialize(new float[GoldenGrid.DefaultGridW * GoldenGrid.DefaultGridH * 3]);
            string firstLine = text.Split('\n')[0];
            Assert.Equal("# KhaozEngine golden grid 32x18 (one line per cell: r g b, row-major)", firstLine);
        }

        /// <summary>
        /// Cardinal constraint: the committed golden text must stay byte-identical through the promoted code.
        /// Deserialize the real committed scene3d.metal-native.txt then Serialize it back and assert the
        /// text reproduces the file exactly (header included). Guards both the format and the 4-decimal
        /// rounding.
        /// The strict compare relies on the goldens checking out with LF endings on every OS, which
        /// .gitattributes pins (a CRLF autocrlf checkout on Windows broke this test in CI once).
        /// </summary>
        [Fact]
        public void Serialize_reproduces_committed_golden_byte_for_byte()
        {
            string path = CommittedGoldenPath("scene3d.metal-native.txt");
            Assert.True(File.Exists(path), $"expected committed golden at {path}");
            string original = File.ReadAllText(path);
            float[] grid = GoldenGrid.Deserialize(original);
            string reserialized = GoldenGrid.Serialize(grid, GoldenGrid.DefaultGridW, GoldenGrid.DefaultGridH);
            Assert.Equal(original, reserialized);
        }

        static string CommittedGoldenPath(string file, [CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Gpu", "goldens", file);
    }
}
