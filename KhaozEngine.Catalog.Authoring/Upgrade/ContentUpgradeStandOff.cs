using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One definition's patience with a rival publisher: how long it waits and, more importantly, WHAT it waits
/// for.
/// <para>
/// <b>The budget is spent on a catalog that is not moving, not on a clock.</b> A fixed count of attempts
/// makes a boot's correctness depend on how loaded the machine is, and that is exactly how a two-runner
/// regression failed once under a full-solution run and passed every time it was looked at. So the attempt
/// count RESETS whenever the catalog visibly moved: a ledger row appeared, the active version advanced, or
/// the open draft changed. A run gives up only when nothing at all has happened for a whole budget.
/// </para>
/// <para>
/// <b>The budget is still finite.</b> Progress is bounded in practice, because a rival can only publish the
/// upgrades the shipped set holds, but a boot that could wait forever is a hang rather than an outage that
/// reports itself, so a total ceiling stands behind the resetting one.
/// </para>
/// </summary>
sealed class ContentUpgradeStandOff
{
    /// <summary>
    /// How many attempts one definition gets while the catalog does not move. With the backoff below this
    /// is a little over thirty seconds of waiting, which is sized for a loaded CI machine writing a pack to
    /// remote storage rather than for the local case, and none of it is paid by a run with no rival.
    /// </summary>
    internal const int MaxAttempts = 40;

    /// <summary>The backoff step, multiplied by the attempt up to <see cref="MaxBackoffMilliseconds"/>.</summary>
    internal const int BackoffMilliseconds = 50;

    /// <summary>The longest single wait, so the budget grows with attempts rather than with each wait.</summary>
    internal const int MaxBackoffMilliseconds = 1000;

    /// <summary>
    /// The ceiling on attempts INCLUDING the ones progress bought back, which is what keeps a livelock
    /// between two runners bounded.
    /// </summary>
    internal const int MaxTotalAttempts = MaxAttempts * 4;

    int _attempt;
    int _total;
    string _progress = string.Empty;

    /// <summary>The wait before the attempt this stand-off is now on, in milliseconds.</summary>
    internal int Delay => Math.Min(_attempt * BackoffMilliseconds, MaxBackoffMilliseconds);

    /// <summary>
    /// Records what the catalog looks like now and answers whether another attempt is owed. A signature that
    /// DIFFERS from the last one means a rival made progress, and the attempt count starts again.
    /// </summary>
    /// <param name="progress">The catalog's ledger, active version and open draft as one comparable value.</param>
    internal bool Observe(string progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        _total++;
        if (string.Equals(progress, _progress, StringComparison.Ordinal))
        {
            _attempt++;
        }
        else
        {
            _progress = progress;
            _attempt = 1;
        }

        return _attempt <= MaxAttempts && _total <= MaxTotalAttempts;
    }
}
