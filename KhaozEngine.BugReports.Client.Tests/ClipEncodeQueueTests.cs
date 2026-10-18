using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using KhaozEngine.BugReports.Client.Internal;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class ClipEncodeQueueTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    /// <summary>An encoder that blocks on the first frame until released and records every frame it sees.</summary>
    sealed class GatedEncoder : IDisposable
    {
        readonly object _gate = new();
        readonly List<TimeSpan> _encoded = new();
        int _calls;

        internal ManualResetEventSlim Release { get; } = new(false);
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal int ThreadId { get; private set; }

        /// <summary>When set, the first frame throws once released instead of encoding.</summary>
        internal Exception? FirstFault { get; init; }

        internal TimeSpan[] Encoded { get { lock (_gate) return _encoded.ToArray(); } }

        internal byte[] Encode(RawClipFrame frame)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            lock (_gate) _encoded.Add(frame.CapturedAt);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Release.Wait(Long);
                if (FirstFault is not null) throw FirstFault;
            }
            return new byte[] { 0xFF, 0xD8, (byte)frame.CapturedAt.TotalMilliseconds };
        }

        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
            Entered.Dispose();
        }
    }

    sealed class Delivered
    {
        readonly List<TimeSpan> _at = new();
        internal void Add(ClipFrameRecord record) { lock (_at) _at.Add(record.CapturedAt); }
        internal TimeSpan[] All { get { lock (_at) return _at.ToArray(); } }
    }

    /// <summary>Wraps the shared pool and counts how often each buffer it rented comes back.</summary>
    sealed class CountingPool : ArrayPool<byte>
    {
        readonly ConditionalWeakTable<byte[], StrongBox<int>> _returns = new();
        readonly List<byte[]> _rented = new();

        internal IReadOnlyList<byte[]> Rented { get { lock (_rented) return _rented.ToArray(); } }

        internal int Returns(byte[] buffer) { lock (_rented) return _returns.TryGetValue(buffer, out var box) ? box.Value : 0; }

        internal int TotalReturns
        {
            get
            {
                int total = 0;
                foreach (byte[] buffer in Rented) total += Returns(buffer);
                return total;
            }
        }

        public override byte[] Rent(int minimumLength)
        {
            byte[] buffer = Shared.Rent(minimumLength);
            lock (_rented)
            {
                _rented.Add(buffer);
                _returns.AddOrUpdate(buffer, new StrongBox<int>(0));
            }
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_rented)
                if (_returns.TryGetValue(array, out var box)) box.Value++;
            Shared.Return(array, clearArray);
        }
    }

    static RawClipFrame Frame(ArrayPool<byte> pool, int ms, int width = 8, int height = 4)
        => new(pool.Rent(width * height * 4), width, height, TimeSpan.FromMilliseconds(ms), 16.7, 0);

    static long TimedEnqueue(ClipEncodeQueue queue, RawClipFrame frame)
    {
        var watch = Stopwatch.StartNew();
        queue.Enqueue(frame);
        return watch.ElapsedMilliseconds;
    }

    [Fact]
    public void Frames_are_encoded_off_the_calling_thread_and_delivered_in_order()
    {
        using var encoder = new GatedEncoder();
        var delivered = new Delivered();
        using var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add);

        queue.Enqueue(Frame(ArrayPool<byte>.Shared, 1));
        Assert.True(encoder.Entered.Wait(Long));
        queue.Enqueue(Frame(ArrayPool<byte>.Shared, 2));
        queue.Enqueue(Frame(ArrayPool<byte>.Shared, 3));
        encoder.Release.Set();

        Assert.True(queue.WaitIdle(Long));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, Array.ConvertAll(delivered.All, t => t.TotalMilliseconds));
        Assert.NotEqual(Environment.CurrentManagedThreadId, encoder.ThreadId);
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void A_full_queue_drops_the_oldest_waiting_frame_and_never_blocks()
    {
        using var encoder = new GatedEncoder();
        var delivered = new Delivered();
        using var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add);

        queue.Enqueue(Frame(ArrayPool<byte>.Shared, 1));
        Assert.True(encoder.Entered.Wait(Long));
        Assert.InRange(TimedEnqueue(queue, Frame(ArrayPool<byte>.Shared, 2)), 0, 49);
        Assert.InRange(TimedEnqueue(queue, Frame(ArrayPool<byte>.Shared, 3)), 0, 49);
        Assert.InRange(TimedEnqueue(queue, Frame(ArrayPool<byte>.Shared, 4)), 0, 49);
        Assert.Equal(1, queue.Dropped);
        encoder.Release.Set();

        Assert.True(queue.WaitIdle(Long));
        Assert.Equal(new[] { 1.0, 3.0, 4.0 }, Array.ConvertAll(delivered.All, t => t.TotalMilliseconds));
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public void Every_buffer_goes_back_to_the_pool_after_encode_or_drop()
    {
        using var encoder = new GatedEncoder();
        var pool = new CountingPool();
        var delivered = new Delivered();
        using var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add, pool: pool);

        queue.Enqueue(Frame(pool, 1));
        Assert.True(encoder.Entered.Wait(Long));
        queue.Enqueue(Frame(pool, 2));
        queue.Enqueue(Frame(pool, 3));
        queue.Enqueue(Frame(pool, 4));
        queue.Enqueue(Frame(pool, 5));
        encoder.Release.Set();

        Assert.True(queue.WaitIdle(Long));
        Assert.Equal(2, queue.Dropped);
        Assert.Equal(5, pool.Rented.Count);
        foreach (byte[] buffer in pool.Rented) Assert.Equal(1, pool.Returns(buffer));
    }

    [Fact]
    public void WaitIdle_returns_true_once_the_queue_drains()
    {
        var delivered = new Delivered();
        using var queue = new ClipEncodeQueue(_ => new byte[] { 1 }, delivered.Add);
        Assert.True(queue.WaitIdle(TimeSpan.Zero));

        queue.Enqueue(Frame(ArrayPool<byte>.Shared, 1));
        Assert.True(queue.WaitIdle(Long));
        Assert.Single(delivered.All);
    }

    [Fact]
    public void WaitIdle_returns_false_when_the_encoder_outlives_the_timeout()
    {
        using var encoder = new GatedEncoder();
        var delivered = new Delivered();
        using var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add);

        queue.Enqueue(Frame(ArrayPool<byte>.Shared, 1));
        Assert.True(encoder.Entered.Wait(Long));
        Assert.False(queue.WaitIdle(TimeSpan.FromMilliseconds(50)));
        Assert.Empty(delivered.All);

        encoder.Release.Set();
        Assert.True(queue.WaitIdle(Long));
        Assert.Single(delivered.All);
    }

    [Fact]
    public void Dispose_stops_the_worker_with_frames_still_waiting()
    {
        using var encoder = new GatedEncoder();
        var pool = new CountingPool();
        var delivered = new Delivered();
        var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add, pool: pool);

        queue.Enqueue(Frame(pool, 1));
        Assert.True(encoder.Entered.Wait(Long));
        queue.Enqueue(Frame(pool, 2));
        queue.Enqueue(Frame(pool, 3));

        var disposer = new Thread(queue.Dispose) { IsBackground = true };
        disposer.Start();
        var watch = Stopwatch.StartNew();
        while (pool.TotalReturns < 2 && watch.Elapsed < Long) Thread.Sleep(1);
        Assert.Equal(2, pool.TotalReturns);
        encoder.Release.Set();

        Assert.True(disposer.Join(Long));
        Assert.Equal(new[] { 1.0 }, Array.ConvertAll(encoder.Encoded, t => t.TotalMilliseconds));
        Assert.Empty(delivered.All);
        foreach (byte[] buffer in pool.Rented) Assert.Equal(1, pool.Returns(buffer));

        byte[] late = pool.Rent(8 * 4 * 4);
        queue.Enqueue(new RawClipFrame(late, 8, 4, TimeSpan.FromMilliseconds(9), 16.7, 0));
        Assert.Equal(1, pool.Returns(late));
        Assert.Equal(0, queue.Dropped);
    }

    /// <summary>Runs frame 1 through a gated encoder, queues frames 2 and 3 behind it, then releases and drains.</summary>
    static void RunThree(ClipEncodeQueue queue, GatedEncoder encoder, ArrayPool<byte> pool)
    {
        queue.Enqueue(Frame(pool, 1));
        Assert.True(encoder.Entered.Wait(Long));
        queue.Enqueue(Frame(pool, 2));
        queue.Enqueue(Frame(pool, 3));
        encoder.Release.Set();
        Assert.True(queue.WaitIdle(Long));
    }

    [Fact]
    public void A_throwing_encoder_drops_the_frame_reports_the_fault_and_keeps_the_worker_alive()
    {
        var fault = new InvalidOperationException("encode failed");
        using var encoder = new GatedEncoder { FirstFault = fault };
        var pool = new CountingPool();
        var delivered = new Delivered();
        var faults = new List<Exception>();
        using var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add, e => { lock (faults) faults.Add(e); }, pool);

        RunThree(queue, encoder, pool);

        Assert.Same(fault, Assert.Single(faults));
        Assert.Equal(1, queue.Faults);
        Assert.Equal(0, queue.Dropped);
        Assert.Equal(new[] { 2.0, 3.0 }, Array.ConvertAll(delivered.All, t => t.TotalMilliseconds));
        Assert.Equal(3, pool.Rented.Count);
        foreach (byte[] buffer in pool.Rented) Assert.Equal(1, pool.Returns(buffer));
    }

    [Fact]
    public void A_throwing_deliver_is_contained()
    {
        using var encoder = new GatedEncoder();
        var pool = new CountingPool();
        var delivered = new Delivered();
        var fault = new InvalidOperationException("deliver failed");
        var faults = new List<Exception>();
        using var queue = new ClipEncodeQueue(encoder.Encode, record =>
        {
            if (record.CapturedAt == TimeSpan.FromMilliseconds(1)) throw fault;
            delivered.Add(record);
        }, e => { lock (faults) faults.Add(e); }, pool);

        RunThree(queue, encoder, pool);

        Assert.Same(fault, Assert.Single(faults));
        Assert.Equal(1, queue.Faults);
        Assert.Equal(new[] { 2.0, 3.0 }, Array.ConvertAll(delivered.All, t => t.TotalMilliseconds));
        foreach (byte[] buffer in pool.Rented) Assert.Equal(1, pool.Returns(buffer));
    }

    [Fact]
    public void A_throwing_fault_callback_does_not_stop_the_worker()
    {
        using var encoder = new GatedEncoder { FirstFault = new InvalidOperationException("encode failed") };
        var pool = new CountingPool();
        var delivered = new Delivered();
        int calls = 0;
        using var queue = new ClipEncodeQueue(encoder.Encode, delivered.Add, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("fault callback failed");
        }, pool);

        RunThree(queue, encoder, pool);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, queue.Faults);
        Assert.Equal(new[] { 2.0, 3.0 }, Array.ConvertAll(delivered.All, t => t.TotalMilliseconds));
        foreach (byte[] buffer in pool.Rented) Assert.Equal(1, pool.Returns(buffer));
    }
}
