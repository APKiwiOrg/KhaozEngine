using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>One-way commit, next complete freeze, old commit cleanup and next report transitions.</summary>
internal sealed class UpgradePublishSequence
{
    int _phase;
    CatalogPhaseWatch? _olderWatch;
    CatalogPhaseWatch? _nextWatch;
    IDisposable? _freezeDependency;
    volatile bool _aborted;
    Exception? _failure;
    public CancellationToken Lifetime { get; set; }
    public TaskCompletionSource FirstCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OlderWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFrozen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ContentDraft? DraftAfterOlderPublish { get; private set; }
    public Func<Task>? BeforeSnapshot { get; set; }
    public Func<Task>? BeforeOlderReturn { get; set; }
    public bool Aborted => _aborted;
    public int Phase => Volatile.Read(ref _phase);

    public void Finished() => Advance(3, SecondFinished);

    public void NextStarted()
    {
        _nextWatch = CatalogPhaseWatch.Current;
        _freezeDependency = _olderWatch?.Follow(_nextWatch);
    }

    public async Task WaitForNextFreezeAsync()
    {
        _olderWatch = CatalogPhaseWatch.Current;
        Advance(0, FirstCommitted);
        CatalogPhaseWatch.Enter(CatalogPhase.OlderSweepWaitingForNextFreeze);
        OlderWaiting.TrySetResult();
        try
        {
            await SecondFrozen.Task.WaitAsync(Lifetime);
        }
        finally
        {
            _freezeDependency?.Dispose();
            CatalogPhaseWatch.Enter(CatalogPhase.OlderSweepAfterNextFreeze);
        }
    }

    public async Task WaitForOldCleanupAsync()
    {
        CatalogPhaseWatch.Enter(CatalogPhase.NextFreezeWaitingForOldCleanup);
        using IDisposable? dependency = CatalogPhaseWatch.Current?.Follow(_olderWatch);
        Advance(1, SecondFrozen);
        try
        {
            await FirstReleased.Task.WaitAsync(Lifetime);
        }
        finally
        {
            CatalogPhaseWatch.Enter(CatalogPhase.NextPublishAfterOldCleanup);
        }
    }

    public void ThrowIfFailed()
    {
        if (_failure is Exception failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void Fail(Exception failure)
    {
        Interlocked.CompareExchange(ref _failure, failure, null);
        _aborted = true;
        foreach (TaskCompletionSource gate in new[] { FirstCommitted, OlderWaiting, SecondFrozen, FirstReleased, SecondFinished })
        {
            gate.TrySetException(failure);
            // Read faults even when the primary error wins a readiness race.
            _ = gate.Task.Exception;
        }
    }

    public async Task CaptureAndReleaseAsync(IContentAuthoringStore inner, Exception? publishFailure)
    {
        if (Phase < 2 && publishFailure is not null)
        {
            Fail(publishFailure);
        }

        // Install the dependency before releasing the next publisher. Its first IO can run as soon
        // as FirstReleased completes, including on a different continuation thread.
        using IDisposable? dependency = CatalogPhaseWatch.Current?.Follow(_nextWatch);
        Exception? snapshotFailure = null;
        try
        {
            CatalogPhaseWatch.Enter(CatalogPhase.OldCommitCleanupSnapshot);
            if (BeforeSnapshot is not null)
            {
                await BeforeSnapshot();
            }

            // Exercise cancellation must not suppress bookkeeping while the store lifetime is still owned.
            DraftAfterOlderPublish = await inner.GetOpenDraftAsync(CancellationToken.None);
        }
        catch (Exception failure)
        {
            snapshotFailure = failure;
            Fail(failure);
            throw;
        }
        finally
        {
            if (snapshotFailure is null)
            {
                Advance(2, FirstReleased);
            }
            else
            {
                FirstReleased.TrySetException(snapshotFailure);
            }
        }

        try
        {
            CatalogPhaseWatch.Enter(CatalogPhase.OlderRunnerWaitingForNextReport);
            await SecondFinished.Task.WaitAsync(Lifetime);
        }
        finally
        {
            if (BeforeOlderReturn is not null)
            {
                CatalogPhaseWatch.Enter(CatalogPhase.OlderRunnerTeardownHold);
                await BeforeOlderReturn();
            }
        }
    }

    void Advance(int expected, TaskCompletionSource gate)
    {
        if (Aborted)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _phase, expected + 1, expected) != expected)
        {
            throw new InvalidOperationException($"Catalog publish phase {expected + 1} repeated or arrived out of order.");
        }

        gate.TrySetResult();
    }
}
