using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>Writes a result to an absolute JSON path, temp then move, so a baseline is never half written.</summary>
public static class CatalogBenchmarkOutput
{
    public static async Task WriteAsync(
        CatalogBenchmarkResult result,
        string absolutePath,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Output path must be absolute.", nameof(absolutePath));
        if (!string.Equals(Path.GetExtension(absolutePath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must use the .json extension.", nameof(absolutePath));

        string directory = Path.GetDirectoryName(absolutePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(absolutePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                result.ToJson() + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellation).ConfigureAwait(false);
            File.Move(temporary, absolutePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
