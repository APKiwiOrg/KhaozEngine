using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// The actual SPIRV-Tools exhaustive pass is measured offline. Bind that measurement
// to its input module so a changed call graph cannot pass using an old count.
internal static class CoverageShaderSizeManifest
{
    internal static Dictionary<string, string[]> Read([CallerFilePath] string thisFile = "") =>
        File.ReadLines(Path.Combine(Path.GetDirectoryName(thisFile)!, "shader-corpus", "coverage-sizes.csv"))
            .Skip(1).Select(line => line.Split(',')).ToDictionary(row => row[0], StringComparer.Ordinal);

    internal static int InlineInstructions(byte[] spirv, string name, int hlslBytes, int mslBytes)
    {
        var rows = Read();
        Assert.True(rows.TryGetValue(name, out string[]? row), $"{name}: missing exhaustive-inline size measurement");
        Assert.NotNull(row);
        string hash = Convert.ToHexStringLower(SHA256.HashData(spirv));
        Assert.True(hash == row[1], $"{name}: changed SPIR-V needs a fresh exhaustive-inline measurement. "
            + "Dump CoverageShaderSizeTests with KE_COVERAGE_SIZE_DUMP and run shader-corpus/measure-coverage-sizes.py");
        Assert.Equal(hlslBytes, int.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(mslBytes, int.Parse(row[4], System.Globalization.CultureInfo.InvariantCulture));
        return int.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture);
    }
}
