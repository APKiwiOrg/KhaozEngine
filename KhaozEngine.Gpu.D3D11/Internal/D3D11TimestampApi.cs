using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Timestamp query policy, native result checking and actual-device support probing.</summary>
    internal sealed class D3D11TimestampApi : ID3D11TimestampApi
    {
        readonly ID3D11TimestampQueries _queries;
        readonly IDeviceLiveness _liveness;
        readonly D3D11DeviceLossLatch? _loss;

        internal D3D11TimestampApi(ID3D11TimestampQueries queries, IDeviceLiveness? liveness = null,
            D3D11DeviceLossLatch? loss = null)
        {
            _queries = queries ?? throw new ArgumentNullException(nameof(queries));
            _liveness = liveness ?? LiveDevice.Instance;
            _loss = loss;
        }

        internal static bool Probe(ID3D11TimestampApi api)
        {
            object? timestamp = null, disjoint = null;
            try
            {
                bool supported = true;
                try { timestamp = api.CreateTimestamp(); }
                catch (NotSupportedException) { supported = false; }
                try { disjoint = api.CreateDisjoint(); }
                catch (NotSupportedException) { supported = false; }
                return supported;
            }
            finally
            {
                if (disjoint is not null) api.Destroy(disjoint);
                if (timestamp is not null) api.Destroy(timestamp);
            }
        }

        public object CreateTimestamp() => Create(2);
        public object CreateDisjoint() => Create(3);

        object Create(int type)
        {
            RequireLive();
            try { return _queries.CreateQuery(type); }
            catch (Exception fault)
            {
                _loss?.Check(fault.HResult, "timestamp query creation");
                RequireLive();
                // Only explicit unsupported-query results degrade the capability. Other device faults propagate.
                if (fault.HResult is unchecked((int)0x887A0004) or unchecked((int)0x80070057)
                    or unchecked((int)0x80004001))
                    throw new NotSupportedException("This Direct3D 11 device does not support timestamp queries.", fault);
                throw;
            }
        }

        public void Begin(object disjoint) { RequireLive(); _queries.Begin(disjoint); }
        public void End(object query) { RequireLive(); _queries.End(query); }

        public bool TryReadTimestamp(object query, out ulong value)
        {
            Span<byte> data = stackalloc byte[8];
            bool ready = Read(query, data);
            value = ready ? MemoryMarshal.Read<ulong>(data) : 0;
            return ready;
        }

        public bool TryReadDisjoint(object query, out ulong frequency, out bool disjoint)
        {
            // D3D11_QUERY_DATA_TIMESTAMP_DISJOINT has an eight-byte frequency, BOOL and trailing padding.
            Span<byte> data = stackalloc byte[16];
            bool ready = Read(query, data);
            frequency = ready ? MemoryMarshal.Read<ulong>(data) : 0;
            disjoint = ready && MemoryMarshal.Read<int>(data[8..]) != 0;
            return ready;
        }

        public int ReadAvailability(object query, bool flush)
        {
            // A null destination of zero bytes asks only for status. Flags 0 lets GetData flush the immediate context.
            RequireLive();
            int result = _queries.GetData(query, Span<byte>.Empty, flush ? 0u : 1u);
            _loss?.Check(result, "timestamp GetData");
            RequireLive();
            if (result < 0) throw new InvalidOperationException($"Direct3D 11 timestamp GetData failed (0x{result:X8}).");
            return result;
        }

        bool Read(object query, Span<byte> data)
        {
            RequireLive();
            int result = _queries.GetData(query, data, 1u); // D3D11_ASYNC_GETDATA_DONOTFLUSH
            _loss?.Check(result, "timestamp GetData");
            RequireLive();
            if (result < 0) throw new InvalidOperationException($"Direct3D 11 timestamp GetData failed (0x{result:X8}).");
            return result == 0;
        }

        public void Destroy(object query)
        {
            if (!_liveness.IsDead) _queries.Destroy(query);
        }

        void RequireLive()
        {
            if (_liveness.IsDead) throw new ObjectDisposedException(nameof(IGpuDevice), "The Direct3D 11 timestamp device is disposed or lost.");
        }
    }
}
