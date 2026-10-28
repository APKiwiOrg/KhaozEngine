using System;
using System.Collections.Immutable;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The completion wait for one pool's queries. A completed later fence does not make Direct3D 11 query data
    /// available: WARP rehearsal run 40628799283 read S_FALSE from a drained batch's final frame
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1635). Query availability is therefore the completion
    /// signal for these queries, and the caller's drain waits for it here.
    /// <para>
    /// One non-flushing read of every query comes first, so an already available batch costs zero polls. Each
    /// later round is one flushing <c>GetData</c> of every query still unavailable, under the submit lock, then
    /// a spin that never sleeps a millisecond. The caller's deadline is the only bound. Nothing here reads or
    /// invents a value, and <see cref="D3D11TimestampPool.TryResolve"/> still decides the result.
    /// </para>
    /// </summary>
    internal sealed class D3D11TimestampAvailability
    {
        readonly ID3D11TimestampApi _api;
        readonly D3D11FenceSubsystem _fences;
        readonly object _submitLock;
        readonly object[] _queries;
        readonly string[] _names;
        readonly int[] _notReady, _final;

        // Index zero is the disjoint query and index i + 1 is timestamp i, the order TryResolve reads them in.
        internal D3D11TimestampAvailability(ID3D11TimestampApi api, D3D11FenceSubsystem fences, object submitLock,
            object disjoint, object[] timestamps)
        {
            _api = api;
            _fences = fences;
            _submitLock = submitLock;
            _queries = new object[timestamps.Length + 1];
            _names = new string[_queries.Length];
            _queries[0] = disjoint;
            _names[0] = "disjoint";
            for (int i = 0; i < timestamps.Length; i++)
            {
                _queries[i + 1] = timestamps[i];
                _names[i + 1] = "timestamp." + i;
            }
            _notReady = new int[_queries.Length];
            _final = new int[_queries.Length];
        }

        internal GpuTimestampAvailability Await(D3D11GpuFence fence, int count, Func<long> now, long deadline)
        {
            ArgumentNullException.ThrowIfNull(now);
            long start = now();
            if (!fence.Signaled)
                return new(false, 0, start, now(), fence.Target, _fences.CompletedValue, _fences.LastDrainTarget, []);
            int total = count + 1;
            for (int i = 0; i < total; i++) { _notReady[i] = 0; _final[i] = -1; }
            bool ready;
            lock (_submitLock) ready = ReadUnavailable(total, flush: false);
            int polls = 0;
            var spin = new SpinWait();
            while (!ready && now() < deadline)
            {
                polls++;
                lock (_submitLock) ready = ReadUnavailable(total, flush: true);
                if (!ready) D3D11DrainSpin.SpinOnce(ref spin);
            }
            long end = now();
            var queries = ImmutableArray.CreateBuilder<GpuTimestampQueryAvailability>(total);
            for (int i = 0; i < total; i++) queries.Add(new(_names[i], _notReady[i], _final[i]));
            return new(ready, polls, start, end, fence.Target, _fences.CompletedValue, _fences.LastDrainTarget,
                queries.MoveToImmutable());
        }

        bool ReadUnavailable(int total, bool flush)
        {
            bool ready = true;
            for (int i = 0; i < total; i++)
            {
                if (_final[i] == 0) continue;
                // The earlier read is no longer the last one, so it joins the not-ready run.
                if (_final[i] > 0) _notReady[i]++;
                _final[i] = _api.ReadAvailability(_queries[i], flush);
                ready &= _final[i] == 0;
            }
            return ready;
        }
    }
}
