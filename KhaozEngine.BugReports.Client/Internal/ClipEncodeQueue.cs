using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>
/// Keeps clip encoding off the frame thread. The frame thread hands mapped frames to <see cref="Enqueue"/>, which
/// never blocks. One dedicated background thread encodes them in order and delivers each record. At most
/// <see cref="Capacity"/> frames wait, and a full queue drops its oldest waiting frame. Every frame's buffer goes
/// back to the pool exactly once, after its encode or its drop. A throw from the encoder or the deliver callback drops
/// that frame, reports the fault and leaves the worker running, so a capture fault never takes the game down.
/// </summary>
internal sealed class ClipEncodeQueue : IDisposable
{
    internal const int Capacity = 2;

    readonly object _gate = new();
    readonly Queue<RawClipFrame> _waiting = new(Capacity);
    readonly SemaphoreSlim _signal = new(0);
    readonly Func<RawClipFrame, byte[]> _encode;
    readonly Action<ClipFrameRecord> _deliver;
    readonly Action<Exception> _onFault;
    readonly ArrayPool<byte> _pool;
    readonly Thread _worker;
    int _dropped;
    int _faults;
    int _warned;
    bool _encoding;
    bool _disposed;

    /// <param name="encode">Encodes a frame from <c>Rgba.AsSpan(0, Width * Height * 4)</c>. Runs on the worker.</param>
    /// <param name="deliver">Receives each encoded frame, in enqueue order. Runs on the worker.</param>
    /// <param name="onFault">
    /// Receives each exception thrown by <paramref name="encode"/> or <paramref name="deliver"/>, on the worker. A
    /// throw from it is swallowed. The recorder wires it to the clip guard's trip. Defaults to one
    /// <see cref="Log.Warn"/> naming the queue, silent for later faults.
    /// </param>
    /// <param name="pool">The pool frame buffers were rented from. Defaults to <see cref="ArrayPool{T}.Shared"/>.</param>
    internal ClipEncodeQueue(Func<RawClipFrame, byte[]> encode, Action<ClipFrameRecord> deliver,
        Action<Exception>? onFault = null, ArrayPool<byte>? pool = null)
    {
        _encode = encode ?? throw new ArgumentNullException(nameof(encode));
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        _onFault = onFault ?? WarnOnce;
        _pool = pool ?? ArrayPool<byte>.Shared;
        _worker = new Thread(Work) { IsBackground = true, Name = "KhaozEngine clip encode" };
        _worker.Start();
    }

    /// <summary>Frames dropped because the queue was full when a newer frame arrived.</summary>
    internal int Dropped { get { lock (_gate) return _dropped; } }

    /// <summary>Frames lost to a throw from the encoder or the deliver callback.</summary>
    internal int Faults => Volatile.Read(ref _faults);

    /// <summary>
    /// Queues <paramref name="frame"/> for encoding without blocking. When <see cref="Capacity"/> frames already
    /// wait, the oldest is dropped and its buffer returned. After <see cref="Dispose"/> the frame's buffer is
    /// returned at once and nothing is queued.
    /// </summary>
    internal void Enqueue(RawClipFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RawClipFrame? drop = null;
        lock (_gate)
        {
            if (_disposed)
            {
                drop = frame;
            }
            else
            {
                if (_waiting.Count == Capacity)
                {
                    drop = _waiting.Dequeue();
                    _dropped++;
                }
                _waiting.Enqueue(frame);
                // Inside the lock, so a racing Dispose cannot dispose the semaphore first.
                _signal.Release();
            }
        }

        if (drop is not null) _pool.Return(drop.Rgba);
    }

    /// <summary>True once no frame waits or encodes, false when <paramref name="timeout"/> passes first.</summary>
    internal bool WaitIdle(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_waiting.Count > 0 || _encoding)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(remaining));
            }
            return true;
        }
    }

    /// <summary>
    /// Returns every waiting frame's buffer and joins the worker. The frame being encoded is not delivered when its
    /// encode finishes after this call marks the queue disposed. A frame that passed that disposed check first is
    /// still delivered, on the worker, while this call waits in the join.
    /// </summary>
    public void Dispose()
    {
        RawClipFrame[] waiting;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            waiting = _waiting.ToArray();
            _waiting.Clear();
            Monitor.PulseAll(_gate);
        }

        foreach (RawClipFrame frame in waiting) _pool.Return(frame.Rgba);
        _signal.Release();
        // A Dispose from inside deliver leaves the worker to exit on its own once deliver returns.
        if (Thread.CurrentThread == _worker) return;
        _worker.Join();
        _signal.Dispose();
    }

    void Work()
    {
        while (true)
        {
            _signal.Wait();
            RawClipFrame frame;
            lock (_gate)
            {
                if (_disposed) return;
                // A drop leaves one more signal than frames, so an empty queue just waits again.
                if (!_waiting.TryDequeue(out RawClipFrame? next)) continue;
                frame = next;
                _encoding = true;
            }

            try
            {
                byte[] jpeg = _encode(frame);
                bool deliver;
                lock (_gate) deliver = !_disposed;
                if (deliver)
                    _deliver(new ClipFrameRecord(frame.CapturedAt, frame.RenderMilliseconds, frame.Width, frame.Height,
                        jpeg, frame.Generation));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _faults);
                try
                {
                    _onFault(ex);
                }
                catch (Exception)
                {
                    // The fault callback must not end the worker either. The frame is already lost.
                }
            }
            finally
            {
                _pool.Return(frame.Rgba);
                lock (_gate)
                {
                    _encoding = false;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    void WarnOnce(Exception fault)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            Log.Warn("ClipEncodeQueue dropped a clip frame after a fault. Later faults are not reported.", fault);
    }
}
