using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.TestKit;
using KhaozEngine.Imaging;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    [Collection("GoldenImageEnvironmentSerial")]
    public sealed class GoldenImageTests
    {
        const int Width = GoldenGrid.DefaultGridW;
        const int Height = GoldenGrid.DefaultGridH;

        [Fact]
        public void Exact_byte_tolerance_boundary_passes()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);
            byte[] golden = Frame();
            WriteGolden(temp.Path, "still", "metal-native", golden);
            byte[] actual = Frame();
            SetChannel(actual, 4, 3, 0, 8);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "still", actual, Width, Height, 8, "metal-native");

            Assert.True(result.Pass, result.Detail);
            Assert.False(result.Rebaked);
            Assert.Null(result.SkipReason);
        }

        [Fact]
        public void Same_nonzero_source_passes_at_zero_tolerance()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);
            byte[] source = Frame(23, 45, 67);
            WriteGolden(temp.Path, "same", "metal-native", source);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "same", source, Width, Height, 0, "metal-native");

            Assert.True(result.Pass, result.Detail);
            Assert.False(result.Rebaked);
            Assert.Null(result.SkipReason);
        }

        [Fact]
        public void Difference_above_byte_tolerance_names_worst_cell_and_channel()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);
            WriteGolden(temp.Path, "changed", "vulkan-native", Frame());
            byte[] actual = Frame();
            SetChannel(actual, 5, 7, 1, 9);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "changed", actual, Width, Height, 8, "vulkan-native");

            Assert.False(result.Pass);
            Assert.False(result.Rebaked);
            Assert.Null(result.SkipReason);
            Assert.Contains("(5,7) G", result.Detail, StringComparison.Ordinal);
            Assert.Contains("tolerance 8/255", result.Detail, StringComparison.Ordinal);
            Assert.Contains("difference", result.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void Missing_backend_golden_returns_skip_reason()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "new-scene", Frame(), Width, Height, 8, "direct3d11-native");

            Assert.False(result.Pass);
            Assert.False(result.Rebaked);
            Assert.Contains("new-scene.direct3d11-native.txt", result.SkipReason, StringComparison.Ordinal);
            Assert.Contains("KE_UPDATE_GOLDENS=1", result.SkipReason, StringComparison.Ordinal);
        }

        [Fact]
        public void Only_exact_update_value_one_rebakes()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", "true");

            GoldenResult result = GoldenImage.Check(
                temp.Path, "guarded", Frame(), Width, Height, 8, "metal-native");

            Assert.False(result.Pass);
            Assert.False(result.Rebaked);
            Assert.False(File.Exists(Path.Combine(temp.Path, "guarded.metal-native.txt")));
        }

        [Fact]
        public void Rebake_writes_canonical_golden_then_normal_check_passes()
        {
            using var temp = new TempDirectory();
            byte[] actual = Frame(23, 45, 67);
            GoldenResult rebaked;
            using (new EnvironmentVariableScope("KE_UPDATE_GOLDENS", "1"))
            {
                rebaked = GoldenImage.Check(
                    temp.Path, "rebaked", actual, Width, Height, 0, "metal-native");
            }

            GoldenResult checkedResult;
            using (new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null))
            {
                checkedResult = GoldenImage.Check(
                    temp.Path, "rebaked", actual, Width, Height, 0, "metal-native");
            }

            Assert.True(rebaked.Pass, rebaked.Detail);
            Assert.True(rebaked.Rebaked);
            Assert.True(checkedResult.Pass, checkedResult.Detail);
            Assert.False(checkedResult.Rebaked);
        }

        [Fact]
        public void Backend_name_is_the_canonical_filename_suffix()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", "1");

            GoldenResult result = GoldenImage.Check(
                temp.Path, "suffix", Frame(), Width, Height, 0, "vulkan-native");

            Assert.True(result.Pass, result.Detail);
            Assert.True(File.Exists(Path.Combine(temp.Path, "suffix.vulkan-native.txt")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "suffix.vulkannative.txt")));
        }

        [Fact]
        public void Explicit_capture_backend_writes_only_its_golden_family()
        {
            using var temp = new TempDirectory();
            byte[] capture = Frame(23, 45, 67);
            WriteGolden(temp.Path, "explicit", "metal-native", Frame(200, 100, 50));
            string metalPath = Path.Combine(temp.Path, "explicit.metal-native.txt");
            string metalBefore = File.ReadAllText(metalPath);
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", "1");

            GoldenResult result = GoldenImage.Check(
                temp.Path, "explicit", capture, Width, Height, 0, GpuBackendKind.VulkanNative);

            Assert.True(result.Pass, result.Detail);
            Assert.True(result.Rebaked);
            Assert.True(File.Exists(Path.Combine(temp.Path, "explicit.vulkan-native.txt")));
            Assert.Equal(metalBefore, File.ReadAllText(metalPath));
        }

        [Fact]
        public void Explicit_capture_backend_compares_only_its_golden_family()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);
            byte[] capture = Frame(23, 45, 67);
            WriteGolden(temp.Path, "explicit", "metal-native", Frame(200, 100, 50));
            WriteGolden(temp.Path, "explicit", "vulkan-native", capture);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "explicit", capture, Width, Height, 0, GpuBackendKind.VulkanNative);

            Assert.True(result.Pass, result.Detail);
            Assert.False(result.Rebaked);
            Assert.Null(result.SkipReason);
        }

        [Fact]
        public void Unknown_capture_backend_is_rejected_instead_of_falling_back()
        {
            using var temp = new TempDirectory();

            NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
                GoldenImage.Check(
                    temp.Path, "unknown", Frame(), Width, Height, 0, (GpuBackendKind)9001));

            Assert.Contains("No golden family", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        [InlineData(-1, 1)]
        [InlineData(1, -1)]
        public void Non_positive_dimensions_are_rejected(int width, int height)
        {
            using var temp = new TempDirectory();

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                GoldenImage.Check(temp.Path, "scene", Array.Empty<byte>(), width, height, 0, "metal-native"));

            Assert.Contains("positive", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Invalid_input_is_rejected_before_backend_mapping()
        {
            using var temp = new TempDirectory();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GoldenImage.Check(
                    temp.Path, "scene", Array.Empty<byte>(), 0, 1, 0, (GpuBackendKind)9001));
        }

        [Fact]
        public void Wrong_rgba_byte_count_is_rejected()
        {
            using var temp = new TempDirectory();

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                GoldenImage.Check(temp.Path, "scene", new byte[3], 1, 1, 0, "metal-native"));

            Assert.Contains("4", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("../escape")]
        [InlineData("nested/scene")]
        [InlineData("nested\\scene")]
        [InlineData("scene:name")]
        [InlineData("scene?name")]
        [InlineData("scene.")]
        [InlineData("scene ")]
        [InlineData("CON")]
        [InlineData("con.txt")]
        [InlineData("PrN.snapshot")]
        [InlineData("AUX")]
        [InlineData("nul.anything")]
        [InlineData("COM1")]
        [InlineData("com9.grid")]
        [InlineData("COM¹")]
        [InlineData("com².txt")]
        [InlineData("CoM³.grid")]
        [InlineData("LPT1")]
        [InlineData("lPt9.txt")]
        [InlineData("LPT¹")]
        [InlineData("lpt².txt")]
        [InlineData("LpT³.grid")]
        public void Unsafe_scene_names_are_rejected(string scene)
        {
            using var temp = new TempDirectory();

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                GoldenImage.Check(temp.Path, scene, Frame(), Width, Height, 0, "metal-native"));

            Assert.Contains("file name", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("console")]
        [InlineData("com10")]
        [InlineData("lpt0")]
        public void Device_name_prefixes_that_are_not_reserved_remain_valid(string scene)
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);

            GoldenResult result = GoldenImage.Check(
                temp.Path, scene, Frame(), Width, Height, 0, "metal-native");

            Assert.NotNull(result.SkipReason);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(256)]
        public void Tolerance_outside_byte_range_is_rejected(int tolerance)
        {
            using var temp = new TempDirectory();

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                GoldenImage.Check(temp.Path, "scene", Frame(), Width, Height, tolerance, "metal-native"));

            Assert.Contains("0 through 255", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Golden_read_error_returns_actionable_failure()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);
            string path = Path.Combine(temp.Path, "blocked.metal-native.txt");
            Directory.CreateDirectory(path);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "blocked", Frame(), Width, Height, 0, "metal-native");

            Assert.False(result.Pass);
            Assert.Null(result.SkipReason);
            Assert.Contains("could not read golden", result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(path, result.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void Non_finite_golden_cell_returns_actionable_failure()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", null);
            string path = Path.Combine(temp.Path, "corrupt.metal-native.txt");
            string serialized = GoldenGrid.Serialize(GoldenGrid.Downsample(Frame(), Width, Height));
            int firstCell = serialized.IndexOf("0.0000", StringComparison.Ordinal);
            serialized = serialized.Remove(firstCell, "0.0000".Length).Insert(firstCell, "NaN");
            File.WriteAllText(path, serialized);

            GoldenResult result = GoldenImage.Check(
                temp.Path, "corrupt", Frame(), Width, Height, 0, "metal-native");

            Assert.False(result.Pass);
            Assert.Null(result.SkipReason);
            Assert.Contains("non-finite", result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cell 0 R", result.Detail, StringComparison.Ordinal);
            Assert.Contains(path, result.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void Golden_write_error_returns_actionable_failure()
        {
            using var temp = new TempDirectory();
            using var update = new EnvironmentVariableScope("KE_UPDATE_GOLDENS", "1");
            string blockedDirectory = Path.Combine(temp.Path, "file-instead-of-directory");
            File.WriteAllText(blockedDirectory, "occupied");

            GoldenResult result = GoldenImage.Check(
                blockedDirectory, "blocked", Frame(), Width, Height, 0, "metal-native");

            Assert.False(result.Pass);
            Assert.False(result.Rebaked);
            Assert.Null(result.SkipReason);
            Assert.Contains("could not write golden", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        static byte[] Frame(byte r = 0, byte g = 0, byte b = 0)
        {
            var rgba = new byte[Width * Height * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }

            return rgba;
        }

        static void SetChannel(byte[] rgba, int x, int y, int channel, byte value)
            => rgba[((y * Width) + x) * 4 + channel] = value;

        static void WriteGolden(string directory, string scene, string backend, byte[] rgba)
        {
            float[] grid = GoldenGrid.Downsample(rgba, Width, Height);
            File.WriteAllText(
                Path.Combine(directory, $"{scene}.{backend}.txt"),
                GoldenGrid.Serialize(grid));
        }

        sealed class EnvironmentVariableScope : IDisposable
        {
            readonly string _name;
            readonly string? _previous;

            public EnvironmentVariableScope(string name, string? value)
            {
                _name = name;
                _previous = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
        }

        sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "KhaozEngine.GoldenImageTests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
        }
    }

    [CollectionDefinition("GoldenImageEnvironmentSerial", DisableParallelization = true)]
    public sealed class GoldenImageEnvironmentCollection
    {
    }
}
