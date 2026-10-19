using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageHistoryEvidence
{
    internal static int BufferId(IGpuBuffer buffer) => RuntimeHelpers.GetHashCode(buffer);

    internal static void Write(IGpuDevice device, string name, object actual)
    {
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_EVIDENCE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "grimhollow-equipment-coverage-task12");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"coverage-history-{name}-{device.Backend}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Backend = device.Backend.ToString(),
            Device = device.Capabilities.DeviceName,
            Evidence = "Native scene output and production GPU history selection",
            Actual = actual,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native coverage history evidence: {path}");
    }
}
