using System;
using System.Threading.Tasks;

namespace KhaozEngine.Simulation;

/// <summary>
/// An <see cref="IJobScheduler"/> that fans jobs across the BCL thread pool via <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>.
/// Opt-in: pass it where a scheduler is accepted (e.g. a <c>ShardHost</c>) to tick independent work across cores.
/// Use only for genuinely independent jobs - the engine's per-cell sim step and per-row-pure <c>ForEach</c> qualify.
/// </summary>
public sealed class ThreadPoolJobScheduler : IJobScheduler
{
    private readonly ParallelOptions options;

    /// <param name="maxDegreeOfParallelism">
    /// Cap on concurrent workers. <c>-1</c> (default) lets the thread pool decide (typically up to the core count).
    /// Must be <c>-1</c> or positive.
    /// </param>
    public ThreadPoolJobScheduler(int maxDegreeOfParallelism = -1)
    {
        if (maxDegreeOfParallelism is not (-1) and < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism,
                "Max degree of parallelism must be -1 (unbounded) or a positive integer.");
        options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
    }

    /// <summary>The cap this instance was constructed with (the constructor's <c>maxDegreeOfParallelism</c>
    /// parameter): <c>-1</c> for unbounded, else the positive worker-count cap. Read-only diagnostic - the cap
    /// itself is fixed at construction.</summary>
    public int MaxDegreeOfParallelism => options.MaxDegreeOfParallelism;

    /// <inheritdoc />
    public void For(int count, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return;
        if (count == 1) { body(0); return; }   // skip the Parallel.For machinery for a single job (e.g. one hot cell)
        Parallel.For(0, count, options, body);
    }
}
