using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    internal sealed class D3D11TimestampTestApi : ID3D11TimestampApi
    {
        internal readonly List<string> Calls = new();
        internal readonly List<object> Destroyed = new();
        internal bool Record = true;
        internal Action<object>? OnEnd;
        internal Action? OnEmitterEnd;
        internal ulong Frequency = 10_000_000;
        internal bool Disjoint = false;
        internal int Unavailable = -1;
        // Every GetData on a query, availability or data read, returns S_FALSE this many times before S_OK.
        internal int NotReadyReads;
        internal readonly List<(object Query, bool Flush)> Availability = new();
        readonly Dictionary<object, int> _reads = new();
        int _next;
        public object CreateTimestamp() => _next++;
        public object CreateDisjoint() => "disjoint";
        public void Begin(object disjoint) { if (Record) Calls.Add($"Begin({disjoint})"); }
        public void End(object query) { if (Record) Calls.Add($"End({query})"); OnEnd?.Invoke(query); }
        public bool TryReadTimestamp(object query, out ulong value)
        {
            if (Record) Calls.Add($"Read({query})");
            value = (int)query == 0 ? 100ul : 125ul;
            return Reached(query) && (int)query != Unavailable;
        }
        public bool TryReadDisjoint(object query, out ulong frequency, out bool disjoint)
        {
            if (Record) Calls.Add("Read(disjoint)");
            frequency = Frequency;
            disjoint = Disjoint;
            return Reached(query) && Unavailable != -2;
        }
        public int ReadAvailability(object query, bool flush)
        {
            Availability.Add((query, flush));
            return Reached(query) ? 0 : 1;
        }
        bool Reached(object query)
        {
            _reads.TryGetValue(query, out int reads);
            _reads[query] = reads + 1;
            return reads >= NotReadyReads;
        }
        public void Destroy(object query) => Destroyed.Add(query);
    }

    internal sealed class D3D11TimestampTestQueries : ID3D11TimestampQueries
    {
        internal readonly List<int> Created = new();
        internal readonly List<object> Destroyed = new();
        internal readonly List<(int Size, uint Flags)> Reads = new();
        internal int UnsupportedType = -1;
        internal int Failure;
        internal bool Record = true;
        internal ulong Frequency = 10_000_000;
        internal bool Disjoint = false;
        public object CreateQuery(int type)
        {
            Created.Add(type);
            if (type == UnsupportedType) throw new COMException("Unsupported query", unchecked((int)0x887A0004));
            if (Failure < 0) throw new COMException("Device fault", Failure);
            return type;
        }
        public void Begin(object query) { }
        public void End(object query) { }
        public int GetData(object query, Span<byte> data, uint flags)
        {
            Reads.Add((data.Length, flags));
            if (data.IsEmpty) return Failure;
            BinaryPrimitives.WriteUInt64LittleEndian(data, (int)query == 3 ? Frequency : 125ul);
            if ((int)query == 3) BinaryPrimitives.WriteInt32LittleEndian(data[8..], Disjoint ? 1 : 0);
            return Failure;
        }
        public void Destroy(object query) => Destroyed.Add(query);
    }

    internal sealed class D3D11TimestampTestTimeline : ID3D11FenceTimeline
    {
        internal ulong Completed;
        internal int Polls, Flushes, Waits;
        internal Action? OnPoll;
        public D3D11FenceMechanism Mechanism => D3D11FenceMechanism.EventQuery;
        public bool PollIsFreeThreaded => false;
        public ulong Signal() => 1;
        public ulong CompletedValue { get { Polls++; OnPoll?.Invoke(); return Completed; } }
        public void Flush() => Flushes++;
        public bool TryWaitForValue(ulong value, int timeoutMilliseconds) { Waits++; return false; }
        public void Dispose() { }
    }
}
