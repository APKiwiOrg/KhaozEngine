using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class D3D11TimestampTests
    {
        [Fact]
        public void ContendedImmediateContextReturnsPendingWithoutPolling()
        {
            var api = new D3D11TimestampTestApi();
            var timeline = new D3D11TimestampTestTimeline { Completed = 1 };
            object gate = new();
            using var fences = new D3D11FenceSubsystem(timeline, gate);
            using var pool = new D3D11TimestampPool(api, fences, gate, 2);
            using var fence = (D3D11GpuFence)fences.CreateFence();
            fence.Arm(1);
            using var commands = Recorder(fences);
            Record(commands, pool);
            ulong[] ticks = [90, 91];
            GpuTimestampStatus status = default;
            double period = -1;
            Exception? workerError = null;
            string phase = "Created";
            int resolverThread = 0;
            using var ready = new ManualResetEventSlim();
            using var resolve = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try
                {
                    resolverThread = Environment.CurrentManagedThreadId;
                    Volatile.Write(ref phase, "Ready");
                    ready.Set();
                    resolve.Wait();
                    Volatile.Write(ref phase, "Entering TryResolve");
                    status = pool.TryResolve(fence, ticks, out period);
                    Volatile.Write(ref phase, "Returned TryResolve");
                }
                catch (Exception error)
                {
                    workerError = error;
                    Volatile.Write(ref phase, "Faulted");
                }
                finally { completed.Set(); }
            })
            { IsBackground = true, Name = "d3d11-timestamp-resolver" };
            bool started = false;
            Exception? failure = null;
            var watchdog = TimeSpan.FromSeconds(10);
            try
            {
                worker.Start();
                started = true;
                Assert.True(ready.Wait(watchdog), $"Resolver startup watchdog expired. Phase: {Volatile.Read(ref phase)}.");
                Assert.NotEqual(Environment.CurrentManagedThreadId, resolverThread);
                lock (gate)
                {
                    resolve.Set();
                    // This is a deadlock watchdog, after startup, while contention is still real.
                    Assert.True(completed.Wait(watchdog), $"Resolver did not complete before unlock. Phase: {Volatile.Read(ref phase)}.");
                    if (workerError is null)
                    {
                        Assert.Equal(GpuTimestampStatus.Pending, status);
                        Assert.Equal(0d, period);
                        Assert.Equal(new ulong[] { 90, 91 }, ticks);
                        Assert.Empty(api.Calls);
                        Assert.Equal(0, timeline.Polls);
                        Assert.Equal(0, timeline.Flushes);
                        Assert.Equal(0, timeline.Waits);
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                // Unwind the owner lock before joining even if TryResolve regresses to a blocking call.
                resolve.Set();
                if (started) worker.Join();
            }
            // A late worker error must not replace the original watchdog or assertion failure.
            if (failure is not null && workerError is not null)
                throw new AggregateException(failure, workerError);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (workerError is not null) ExceptionDispatchInfo.Capture(workerError).Throw();

            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 0, 25 }, ticks);
            Assert.Equal(100d, period);
            Assert.Equal(new[] { "Read(disjoint)", "Read(0)", "Read(1)" }, api.Calls);
        }

        [Fact]
        public void FencePollExceptionReleasesTheGateForAnotherThread()
        {
            var sentinel = new InvalidOperationException("Fence poll sentinel.");
            var api = new D3D11TimestampTestApi();
            var timeline = new D3D11TimestampTestTimeline { Completed = 1, OnPoll = () => throw sentinel };
            object gate = new();
            using var fences = new D3D11FenceSubsystem(timeline, gate);
            using var pool = new D3D11TimestampPool(api, fences, gate, 2);
            using var fence = (D3D11GpuFence)fences.CreateFence();
            fence.Arm(1);
            using var commands = Recorder(fences);
            Record(commands, pool);
            ulong[] ticks = [90, 91];
            GpuTimestampStatus status = default;
            double period = -1;
            Exception? workerError = null;
            string phase = "Created";
            using var completed = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try
                {
                    Volatile.Write(ref phase, "Entering TryResolve");
                    status = pool.TryResolve(fence, ticks, out period);
                    Volatile.Write(ref phase, "Returned TryResolve");
                }
                catch (Exception error)
                {
                    workerError = error;
                    Volatile.Write(ref phase, "Faulted");
                }
                finally { completed.Set(); }
            })
            { IsBackground = true, Name = "d3d11-timestamp-after-poll-fault" };
            bool started = false;
            Exception? failure = null;
            try
            {
                Assert.Same(sentinel, Assert.Throws<InvalidOperationException>(() => pool.TryResolve(fence, ticks, out period)));
                Assert.Equal(0d, period);
                Assert.Equal(new ulong[] { 90, 91 }, ticks);
                Assert.Empty(api.Calls);
                timeline.OnPoll = null;

                // A same-thread retry could reenter a leaked monitor and hide the missing release.
                worker.Start();
                started = true;
                Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), $"Resolver after poll fault did not complete. Phase: {Volatile.Read(ref phase)}.");
                if (workerError is null)
                {
                    Assert.Equal(GpuTimestampStatus.Ready, status);
                    Assert.Equal(new ulong[] { 0, 25 }, ticks);
                    Assert.Equal(100d, period);
                    Assert.Equal(new[] { "Read(disjoint)", "Read(0)", "Read(1)" }, api.Calls);
                    Assert.Equal(0, timeline.Flushes);
                    Assert.Equal(0, timeline.Waits);
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                timeline.OnPoll = null;
                // Also unwind cleanly if the sentinel exposes a leaked, reentrant owner lock.
                while (Monitor.IsEntered(gate)) Monitor.Exit(gate);
                if (started) worker.Join();
            }
            if (failure is not null && workerError is not null)
                throw new AggregateException(failure, workerError);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (workerError is not null) ExceptionDispatchInfo.Capture(workerError).Throw();
        }
    }
}
