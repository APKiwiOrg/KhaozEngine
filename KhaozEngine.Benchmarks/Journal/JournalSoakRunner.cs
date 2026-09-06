using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Journal;

public static class JournalSoakRunner
{
    public static async Task<JournalBenchmarkResult> RunAsync(
        JournalBenchmarkConfig config,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(output);
        if (config.Mode != JournalBenchmarkMode.Soak)
            throw new ArgumentException("Soak runner requires --journal-soak mode.", nameof(config));
        config.Validate();

        TextWriter synchronized = TextWriter.Synchronized(output);
        using var duration = new CancellationTokenSource(config.Duration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, duration.Token);
        Action<JournalBenchmarkProgress> progress = value =>
        {
            string json = JsonSerializer.Serialize(new
            {
                type = "journal-soak-progress",
                operationsCompleted = value.OperationsCompleted,
                applied = value.Applied,
                replayed = value.Replayed,
                elapsedSeconds = value.ElapsedSeconds,
            });
            synchronized.WriteLine(json);
            synchronized.Flush();
        };

        return await JournalBenchmarkRunner.RunAsync(
            config with { Operations = JournalBenchmarkConfig.MaximumOperations },
            progress,
            linked.Token).ConfigureAwait(false);
    }
}
