using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Journal;

public static class JournalBenchmarkOutput
{
    public static async Task WriteAsync(
        JournalBenchmarkResult result,
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Output path must be absolute.", nameof(absolutePath));
        if (!string.Equals(Path.GetExtension(absolutePath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must use the .json extension.", nameof(absolutePath));

        string directory = Path.GetDirectoryName(absolutePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(absolutePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                result.ToJson() + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, absolutePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
