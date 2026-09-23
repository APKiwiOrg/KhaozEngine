using System;
using System.Globalization;
using System.IO;
using KhaozEngine.Imaging;

namespace KhaozEngine.Gpu.TestKit
{
    /// <summary>The outcome of one golden-image check or re-bake.</summary>
    /// <param name="Pass">True when the capture matched or was re-baked successfully.</param>
    /// <param name="Rebaked">True when this call wrote the canonical golden.</param>
    /// <param name="SkipReason">Why the check has no golden to compare, or null when it ran.</param>
    /// <param name="Detail">A diagnostic describing the check, re-bake or failure.</param>
    public readonly record struct GoldenResult(bool Pass, bool Rebaked, string? SkipReason, string Detail);

    /// <summary>Backend-aware golden-grid comparison and re-bake helper for GPU tests.</summary>
    public static class GoldenImage
    {
        const string UpdateEnvironmentVariable = "KE_UPDATE_GOLDENS";
        const float GoldenTextHalfStep = 0.00005f;
        const float ComparisonEpsilon = 0.0000001f;

        /// <summary>
        /// Downsamples an RGBA8 capture and compares it with
        /// <c>&lt;goldenDirectory&gt;/&lt;scene&gt;.&lt;actual-backend&gt;.txt</c>. The tolerance is in 8-bit channel
        /// units from 0 through 255 and is converted to the normalized <see cref="GoldenGrid"/> unit by dividing
        /// by 255. Comparison adds half of one four-decimal golden-text step and a small floating-point epsilon to
        /// account for stored-reference rounding. Set <c>KE_UPDATE_GOLDENS=1</c> to write the canonical golden
        /// instead of comparing.
        /// </summary>
        public static GoldenResult Check(string goldenDirectory, string scene, ReadOnlySpan<byte> rgba,
            int width, int height, int tolerance)
            => Check(goldenDirectory, scene, rgba, width, height, tolerance, static () => GpuTestGate.BackendName);

        /// <summary>Headless seam that resolves the backend only after validating the capture arguments.</summary>
        internal static GoldenResult Check(string goldenDirectory, string scene, ReadOnlySpan<byte> rgba,
            int width, int height, int tolerance, Func<string> backendName)
        {
            ArgumentNullException.ThrowIfNull(backendName);
            ValidateCaptureInput(goldenDirectory, scene, rgba, width, height, tolerance);
            return Check(goldenDirectory, scene, rgba, width, height, tolerance, backendName());
        }

        /// <summary>Headless check seam that substitutes only the already-resolved backend family name.</summary>
        internal static GoldenResult Check(string goldenDirectory, string scene, ReadOnlySpan<byte> rgba,
            int width, int height, int tolerance, string backendName)
        {
            ValidateInput(goldenDirectory, scene, rgba, width, height, tolerance, backendName);

            float normalizedTolerance = (tolerance / 255f) + GoldenTextHalfStep + ComparisonEpsilon;
            float[] actual = GoldenGrid.Downsample(rgba.ToArray(), width, height);
            string path = Path.Combine(goldenDirectory, $"{scene}.{backendName}.txt");

            if (Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) == "1")
                return Rebake(path, actual);

            string serialized;
            try
            {
                serialized = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                return Missing(scene, backendName, path);
            }
            catch (DirectoryNotFoundException)
            {
                return Missing(scene, backendName, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failure(
                    $"Could not read golden '{path}' ({ex.GetType().Name}: {ex.Message}). "
                    + "Check the golden path and file permissions.");
            }

            float[] expected;
            try
            {
                expected = GoldenGrid.Deserialize(serialized);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                return Failure(
                    $"Could not parse golden '{path}' ({ex.GetType().Name}: {ex.Message}). "
                    + $"Re-bake it with {UpdateEnvironmentVariable}=1.");
            }

            if (expected.Length != actual.Length)
            {
                return Failure(
                    $"Golden '{path}' has {expected.Length / 3} cells, expected {actual.Length / 3}. "
                    + $"Re-bake it with {UpdateEnvironmentVariable}=1.");
            }

            for (int index = 0; index < expected.Length; index++)
            {
                if (float.IsFinite(expected[index])) continue;

                int cell = index / 3;
                string nonFiniteChannel = ChannelName(index % 3);
                return Failure(
                    $"Golden '{path}' contains a non-finite value at cell {cell} {nonFiniteChannel}. "
                    + $"Re-bake it with {UpdateEnvironmentVariable}=1.");
            }

            GoldenGridComparison comparison = GoldenGrid.Compare(actual, expected, normalizedTolerance);
            if (comparison.Passed)
            {
                return new GoldenResult(
                    true,
                    false,
                    null,
                    $"Golden '{scene}' passed on backend '{backendName}'. Worst channel difference "
                    + $"{ByteUnits(comparison.WorstDiff)}/255 within tolerance {tolerance}/255 plus the "
                    + "0.00005 golden-text precision allowance.");
            }

            GoldenGridOffender worst = comparison.Offenders[0];
            int cellX = worst.Cell % GoldenGrid.DefaultGridW;
            int cellY = worst.Cell / GoldenGrid.DefaultGridW;
            string channel = ChannelName(worst.Channel);

            return Failure(
                $"Golden '{scene}' regressed on backend '{backendName}': {comparison.Offenders.Count} "
                + $"channel(s) exceeded tolerance {tolerance}/255 plus the 0.00005 golden-text precision "
                + $"allowance. Worst cell ({cellX},{cellY}) {channel}: "
                + $"got {worst.Got.ToString("0.####", CultureInfo.InvariantCulture)}, "
                + $"wanted {worst.Want.ToString("0.####", CultureInfo.InvariantCulture)}, "
                + $"difference {ByteUnits(worst.Diff)}/255 normalized "
                + $"{worst.Diff.ToString("0.####", CultureInfo.InvariantCulture)}.");
        }

        static GoldenResult Rebake(string path, float[] actual)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (directory != null) Directory.CreateDirectory(directory);
                File.WriteAllText(path, GoldenGrid.Serialize(actual));
                return new GoldenResult(true, true, null, $"Re-baked golden '{path}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failure(
                    $"Could not write golden '{path}' ({ex.GetType().Name}: {ex.Message}). "
                    + "Check the golden directory and file permissions.");
            }
        }

        static GoldenResult Missing(string scene, string backendName, string path)
        {
            string reason =
                $"No committed golden for scene '{scene}' on backend '{backendName}' at '{path}'. "
                + $"Run with KE_GPU_TESTS=1 {UpdateEnvironmentVariable}=1 to create it.";
            return new GoldenResult(false, false, reason, reason);
        }

        static GoldenResult Failure(string detail) => new(false, false, null, detail);

        static string ByteUnits(float normalized)
            => (normalized * 255f).ToString("0.###", CultureInfo.InvariantCulture);

        static string ChannelName(int channel) => channel switch
        {
            0 => "R",
            1 => "G",
            _ => "B",
        };

        static void ValidateInput(string goldenDirectory, string scene, ReadOnlySpan<byte> rgba,
            int width, int height, int tolerance, string backendName)
        {
            ValidateCaptureInput(goldenDirectory, scene, rgba, width, height, tolerance);
            ValidateFileName(backendName, nameof(backendName));
        }

        static void ValidateCaptureInput(string goldenDirectory, string scene, ReadOnlySpan<byte> rgba,
            int width, int height, int tolerance)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(goldenDirectory);
            ValidateFileName(scene, nameof(scene));

            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), width, "Image dimensions must be positive.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), height, "Image dimensions must be positive.");
            if (tolerance is < 0 or > 255)
                throw new ArgumentOutOfRangeException(
                    nameof(tolerance), tolerance, "Tolerance must be from 0 through 255 in 8-bit channel units.");

            long pixelCount = (long)width * height;
            if (pixelCount > int.MaxValue / 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width), "Image dimensions exceed the supported RGBA8 buffer size.");
            }

            int expectedBytes = (int)pixelCount * 4;
            if (rgba.Length != expectedBytes)
            {
                throw new ArgumentException(
                    $"RGBA byte count {rgba.Length} does not match width * height * 4 ({expectedBytes}).",
                    nameof(rgba));
            }
        }

        static void ValidateFileName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value is "." or ".."
                || value.EndsWith(' ')
                || value.EndsWith('.')
                || HasPortableInvalidFileNameCharacter(value)
                || HasWindowsReservedDeviceStem(value))
            {
                throw new ArgumentException(
                    $"{parameterName} must be one safe portable file name without directory separators.",
                    parameterName);
            }
        }

        static bool HasPortableInvalidFileNameCharacter(string value)
        {
            const string invalid = "<>:\"/\\|?*";
            foreach (char character in value)
            {
                if (character < ' ' || invalid.IndexOf(character, StringComparison.Ordinal) >= 0) return true;
            }

            return false;
        }

        static bool HasWindowsReservedDeviceStem(string value)
        {
            int dot = value.IndexOf('.', StringComparison.Ordinal);
            ReadOnlySpan<char> stem = value.AsSpan(0, dot < 0 ? value.Length : dot);
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (stem.Length != 4 || !IsWindowsReservedDeviceDigit(stem[3])) return false;
            ReadOnlySpan<char> prefix = stem[..3];
            return prefix.Equals("COM", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsWindowsReservedDeviceDigit(char value)
            => value is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
