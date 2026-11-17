using System;
using System.Threading.Tasks;
using KhaozEngine.Determinism;

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
    /// <remarks>
    /// Every worker body runs inside a <see cref="DeterministicFpScope"/>. <c>DeterministicFp</c> pins the
    /// floating-point control register (rounding mode, FTZ/DAZ, trap masks) on the CALLING THREAD only, and
    /// <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> hands the body to arbitrary BCL thread-pool
    /// workers whose register is whatever the pool last left it at - neither the calling thread nor a dedicated sim
    /// thread. Without the scope a sim fanned across cores could silently diverge in the low bits between two
    /// machines, or between two runs on one machine, which is the exact class of bug the scope exists to remove,
    /// reintroduced one layer up at the job-scheduling boundary. Applying it here rather than at each call site means
    /// a consumer does not have to remember to.
    /// <para>The fan-out itself also runs inside a scope on the calling thread, entered before <see
    /// cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> and left when it returns, so the caller's own
    /// controls are back when this call returns. A thread can start with the controls of the thread that creates it. In
    /// a Linux arm64 probe, a fan-out that was the process's first thread pool use, from a caller rounding toward zero,
    /// left every pool thread rounding toward zero after the caller was restored, because the scope around each body
    /// restored each worker to what it found. Those threads then serve other code and the JIT. A pool that had already
    /// run work leaked nothing in the same probe. The outer scope makes the calling thread canonical while the pool may
    /// create threads for this call. The scope around each body stays, because a worker that already existed can hold
    /// anything.</para>
    /// <para>Both scopes are allocation-free. The cost is one save plus one restore of that register per slice, and
    /// one more pair per call. They nest harmlessly: an outer scope (the shard host installs one around each cell
    /// tick) is restored to exactly the canonical state the inner one found.</para>
    /// </remarks>
    public void For(int count, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return;
        // A single job skips the Parallel.For machinery (e.g. one hot cell) and runs inline. It still gets the scope:
        // the caller's own thread is no more canonical than a pool worker's, so exempting it would make determinism
        // depend on the job count.
        if (count == 1) { using (DeterministicFpScope.Enter()) body(0); return; }
        // Canonical on the calling thread while the pool may create workers for this call, so none starts with the
        // caller's controls.
        using (DeterministicFpScope.Enter())
        {
            Parallel.For(0, count, options, i =>
            {
                using (DeterministicFpScope.Enter()) body(i);
            });
        }
    }
}
