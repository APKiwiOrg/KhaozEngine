using System;
using System.Threading;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App;

/// <summary>
/// Headless coverage for <see cref="SingleInstanceGuard"/>'s orchestration (via <see cref="FakeSingleInstanceLock"/>)
/// plus a couple of real-primitive integration tests for <see cref="SystemSingleInstanceLock"/> itself
/// (contention across two OS mutex handles on different threads, and the foreground signal channel).
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_NoConflict_ReturnsAcquiredAndKeepsTheLockAlive()
    {
        var fake = new FakeSingleInstanceLock { AcquireSucceeds = true };

        SingleInstanceAcquireResult result = SingleInstanceGuard.TryAcquire("my-key", fake);

        Assert.Equal(SingleInstanceOutcome.Acquired, result.Outcome);
        Assert.Same(fake, result.Lock);
        Assert.False(fake.Disposed); // the winning lock is handed back, not torn down
        Assert.Equal("my-key", fake.AcquiredKey);
    }

    [Fact]
    public void TryAcquire_Conflict_RequestsForegroundOnTheOwnerAndReturnsAlreadyRunning()
    {
        var fake = new FakeSingleInstanceLock { AcquireSucceeds = false };

        SingleInstanceAcquireResult result = SingleInstanceGuard.TryAcquire("my-key", fake);

        Assert.Equal(SingleInstanceOutcome.AlreadyRunning, result.Outcome);
        Assert.Null(result.Lock);
        // The losing side must have asked the existing owner to come forward - this is the "focus call"
        // seam a losing second launch drives, testable without ever touching a real OS window.
        Assert.True(fake.ForegroundRequested);
        Assert.Equal("my-key", fake.ForegroundRequestedKey);
        Assert.True(fake.Disposed); // the losing lock is torn down, never returned
    }

    [Fact]
    public void TryAcquire_EmptyKey_Throws()
    {
        var fake = new FakeSingleInstanceLock();
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(string.Empty, fake));
        Assert.Equal(0, fake.TryAcquireCalls); // never touches the lock with a bad key
    }

    [Fact]
    public void TryAcquire_NoExplicitWait_UsesAppRelaunchDefaultPredecessorTimeout()
    {
        var fake = new FakeSingleInstanceLock { AcquireSucceeds = true };

        SingleInstanceGuard.TryAcquire("my-key", fake);

        // Composition with AppRelaunch: the forced-restart handshake and this guard must ride out the same
        // predecessor-exit window, or a legitimate relaunch successor could lose to its own dying predecessor.
        Assert.Equal(AppRelaunch.DefaultPredecessorTimeout, fake.AcquiredPredecessorWait);
    }

    [Fact]
    public void TryAcquire_ExplicitWait_IsForwardedToTheLock()
    {
        var fake = new FakeSingleInstanceLock { AcquireSucceeds = true };

        SingleInstanceGuard.TryAcquire("my-key", fake, TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), fake.AcquiredPredecessorWait);
    }

    // ---- SystemSingleInstanceLock: the real named-mutex + named-event seam ----

    [Fact]
    public void SystemLock_SecondThreadCannotAcquireWhileFirstHolds()
    {
        string key = "ke-test-" + Guid.NewGuid().ToString("N");
        using var owner = new SystemSingleInstanceLock();
        Assert.True(owner.TryAcquire(key, TimeSpan.Zero));

        bool secondAcquired = true;
        var contender = new Thread(() =>
        {
            using var second = new SystemSingleInstanceLock();
            secondAcquired = second.TryAcquire(key, TimeSpan.FromMilliseconds(100));
        });
        contender.Start();
        contender.Join();

        Assert.False(secondAcquired);
    }

    [Fact]
    public void SystemLock_RequestForeground_SignalsTheExistingOwner()
    {
        string key = "ke-test-" + Guid.NewGuid().ToString("N");
        using var owner = new SystemSingleInstanceLock();
        Assert.True(owner.TryAcquire(key, TimeSpan.Zero));

        // A losing second launch never acquired anything; it just signals via the same key.
        using var loser = new SystemSingleInstanceLock();
        loser.RequestForeground(key);

        Assert.True(owner.WaitForForegroundRequest(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SystemLock_WaitForForegroundRequest_TimesOutWithNoSignal()
    {
        string key = "ke-test-" + Guid.NewGuid().ToString("N");
        using var owner = new SystemSingleInstanceLock();
        Assert.True(owner.TryAcquire(key, TimeSpan.Zero));

        Assert.False(owner.WaitForForegroundRequest(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void SystemLock_ReleasedAfterDispose_AllowsAnotherAcquire()
    {
        string key = "ke-test-" + Guid.NewGuid().ToString("N");
        var first = new SystemSingleInstanceLock();
        Assert.True(first.TryAcquire(key, TimeSpan.Zero));
        first.Dispose();

        using var second = new SystemSingleInstanceLock();
        Assert.True(second.TryAcquire(key, TimeSpan.FromMilliseconds(100)));
    }
}
