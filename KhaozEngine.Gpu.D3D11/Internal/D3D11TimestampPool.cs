using System;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>A fixed set of timestamp queries and one disjoint query, with one outstanding batch.</summary>
    internal sealed class D3D11TimestampPool : IGpuTimestampPool, IGpuTimestampAvailabilityWait
    {
        readonly ID3D11TimestampApi _api;
        readonly D3D11FenceSubsystem _fences;
        readonly object _submitLock;
        readonly GpuTimestampPoolState _state;
        readonly object[] _queries;
        readonly ulong[] _values;
        readonly object _disjoint;
        readonly D3D11TimestampAvailability _availability;
        object? _recording;
        ulong _recordingId;
        bool _ended, _disposed;
        GpuTimestampStatus? _resolved;
        double _period;

        internal D3D11TimestampPool(ID3D11TimestampApi api, D3D11FenceSubsystem fences, object submitLock, uint capacity)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _fences = fences ?? throw new ArgumentNullException(nameof(fences));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _state = new GpuTimestampPoolState(capacity);
            RequireLive();
            Capacity = capacity;
            _queries = new object[capacity];
            _values = new ulong[capacity];
            _disjoint = api.CreateDisjoint();
            int created = 0;
            try
            {
                for (; created < _queries.Length; created++) _queries[created] = api.CreateTimestamp();
            }
            catch
            {
                for (int i = 0; i < created; i++) api.Destroy(_queries[i]);
                api.Destroy(_disjoint);
                throw;
            }
            _availability = new D3D11TimestampAvailability(api, fences, submitLock, _disjoint, _queries);
        }

        public uint Capacity { get; }
        internal ulong Frequency { get; private set; }
        internal object DisjointQuery => _disjoint;
        internal object TimestampQuery(uint index) => _queries[index];

        internal void RequireOwner(D3D11FenceSubsystem fences)
        {
            RequireLive();
            if (!ReferenceEquals(_fences, fences)) throw new ArgumentException("Use a timestamp pool from this Direct3D 11 device.", "pool");
        }

        internal void BeginRecording(object recording, uint count, ulong recordingId = 0)
        {
            RequireLive();
            _state.Begin(count);
            _recording = recording;
            _recordingId = recordingId;
            _ended = false;
            _resolved = null;
            Frequency = 0;
            _period = 0;
        }

        internal void ValidateTimestampEnd(object recording, ulong recordingId, uint finalIndex)
        {
            RequireLive();
            if (!ReferenceEquals(_recording, recording) || _recordingId != recordingId)
                throw new InvalidOperationException("The timestamp pool must be begun on this list in its current recording.");
            RequireRecording(recording);
            _state.ValidateTimestampEnd(finalIndex);
        }

        internal void WriteRecording(object recording, uint index)
        {
            RequireRecording(recording);
            _state.Write(index);
        }

        internal void EndRecording(object recording)
        {
            RequireRecording(recording);
            _state.End();
            _ended = true;
            _recording = null;
        }

        internal void EmitBegin() { RequireLive(); _api.Begin(_disjoint); }
        internal void EmitWrite(uint index) { RequireLive(); _api.End(_queries[index]); }
        internal void EmitEnd() { RequireLive(); _api.End(_disjoint); }

        public GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double tickPeriodNanoseconds)
        {
            tickPeriodNanoseconds = 0d;
            RequireLive();
            ArgumentNullException.ThrowIfNull(completion);
            if (!_ended) throw new InvalidOperationException("End a timestamp batch before resolving it.");
            int count = (int)_state.Count;
            if (ticks.Length < count) throw new ArgumentException("The output cannot hold the timestamp batch.", nameof(ticks));
            if (completion is not D3D11GpuFence fence) throw new ArgumentException("Use this Direct3D 11 device's completion fence.", nameof(completion));
            fence.RequireOwner(_fences);
            if (_resolved is null)
            {
                if (!Monitor.TryEnter(_submitLock)) return GpuTimestampStatus.Pending;
                try
                {
                    if (!fence.Signaled) return GpuTimestampStatus.Pending;
                    RequireLive();
                    bool ready = _api.TryReadDisjoint(_disjoint, out ulong frequency, out bool disjoint);
                    for (int i = 0; i < count; i++)
                        ready &= _api.TryReadTimestamp(_queries[i], out _values[i]);
                    if (!ready) return GpuTimestampStatus.Pending;
                    Frequency = frequency;
                    _resolved = !disjoint && frequency != 0 && GpuTimestampResults.TryNormalize(_values.AsSpan(0, count), 64, _values)
                        ? GpuTimestampStatus.Ready : GpuTimestampStatus.Invalid;
                    _period = _resolved == GpuTimestampStatus.Ready ? 1_000_000_000d / frequency : 0d;
                    _state.Resolved();
                }
                finally { Monitor.Exit(_submitLock); }
            }
            if (_resolved == GpuTimestampStatus.Ready)
            {
                _values.AsSpan(0, count).CopyTo(ticks);
                tickPeriodNanoseconds = _period;
            }
            return _resolved.Value;
        }

        /// <summary>The retained-completion wait for this batch's queries. See <see cref="D3D11TimestampAvailability"/>.</summary>
        public GpuTimestampAvailability AwaitAvailability(IGpuFence completion, Func<long> now, long deadline)
        {
            RequireLive();
            ArgumentNullException.ThrowIfNull(completion);
            if (!_ended) throw new InvalidOperationException("End a timestamp batch before awaiting its queries.");
            if (completion is not D3D11GpuFence fence) throw new ArgumentException("Use this Direct3D 11 device's completion fence.", nameof(completion));
            fence.RequireOwner(_fences);
            return _availability.Await(fence, (int)_state.Count, now, deadline);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recording = null;
            // As with other D3D11 children, COM retains submitted use. Releasing adds no drain or query wait.
            foreach (object query in _queries) _api.Destroy(query);
            _api.Destroy(_disjoint);
        }

        void RequireLive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fences.IsDeviceDead) throw new ObjectDisposedException(nameof(IGpuDevice), "The Direct3D 11 timestamp device is disposed or lost.");
        }

        void RequireRecording(object recording)
        {
            RequireLive();
            if (_ended || !ReferenceEquals(recording, _recording))
                throw new InvalidOperationException("Write and end timestamps in the command list that began them.");
        }
    }
}
