using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The shaped link the client cold start budgets run over. One bucket is shared by every concurrent
/// reader, because the link is the thing being modelled and four streams do not each get 20 Mbit.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _bytesPerSecond;
    private readonly double _capacity;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private double _available;
    private double _lastSeconds;

    public TokenBucket(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bitsPerSecond));
        _bytesPerSecond = bitsPerSecond / 8.0;
        _capacity = _bytesPerSecond / 10.0;   // a 100 ms burst, which is what a real link buffers
        _available = _capacity;
    }

    public long BytesConsumed { get; private set; }

    public async Task ConsumeAsync(int bytes, CancellationToken cancellation)
    {
        while (true)
        {
            double wait;
            lock (_gate)
            {
                double now = _clock.Elapsed.TotalSeconds;
                _available = Math.Min(_capacity, _available + ((now - _lastSeconds) * _bytesPerSecond));
                _lastSeconds = now;
                if (_available >= bytes)
                {
                    _available -= bytes;
                    BytesConsumed += bytes;
                    return;
                }
                wait = (bytes - _available) / _bytesPerSecond;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, 0.0005)), cancellation).ConfigureAwait(false);
        }
    }
}
