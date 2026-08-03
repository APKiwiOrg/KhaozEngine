using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// What <see cref="D3D11NativeTraceEmitter"/> writes into: a per-call count and an ordered trace of the
    /// <c>ID3D11DeviceContext</c> calls a replay would make. A plain class, so the emitter above it stays a
    /// readonly struct that is copied freely while every copy still writes here.
    /// <para>
    /// A SEPARATE TYPE FROM <see cref="D3D11EmitterCallLog"/> ON PURPOSE, and the near-identical shape is worth
    /// the duplication. That log counts SEAM calls and this one counts NATIVE calls, and the entire argument of
    /// 5.3 and of decision T2 is that those are different numbers. One log over both vocabularies would make a
    /// seam total and a native total interchangeable at the call site, which is precisely the mistake the seam's
    /// own remarks were rewritten to stop.
    /// </para>
    /// <para>
    /// Resources appear as stable ids assigned in first-seen order (<c>r0</c>, <c>r1</c>) by reference identity,
    /// matching the emitter call log, so a native trace and a seam trace over one frame name the same resources
    /// the same way and can be read side by side.
    /// </para>
    /// </summary>
    internal sealed class D3D11NativeCallLog
    {
        readonly Dictionary<D3D11NativeCall, int> _counts = new();
        readonly List<string> _trace = new();
        readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every entry so far, in order, one line each. Longer than <see cref="TotalCalls"/> by the
        /// number of <see cref="D3D11NativeCall.ResourceSetPending"/> markers in it.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        /// <summary>
        /// Total NATIVE calls recorded, which is what decision T2's budget is made of.
        /// <para>
        /// <see cref="D3D11NativeCall.ResourceSetPending"/> is EXCLUDED, because it is not a call. A resource-set
        /// bind records only and issues nothing at the device, and the flush that follows it is where the calls
        /// appear. Counting the marker would add one to the total for every set a frame binds, which is a number
        /// that grows with the recording rather than with the device work, and a budget built on it would move
        /// whenever a renderer bound the same set a second time.
        /// </para>
        /// </summary>
        internal int TotalCalls { get; private set; }

        /// <summary>How many times one call was issued. Answers for the pending marker too, which is how a test
        /// asserts that a bind recorded and did not emit.</summary>
        internal int Count(D3D11NativeCall call) => _counts.TryGetValue(call, out int n) ? n : 0;

        /// <summary>Record one call, or the one marker that is not one.</summary>
        internal void Record(D3D11NativeCall call, string arguments = "")
        {
            _counts[call] = Count(call) + 1;
            if (call != D3D11NativeCall.ResourceSetPending) TotalCalls++;
            _trace.Add(call + "(" + arguments + ")");
        }

        /// <summary>A stable, readable id for a resource, by reference identity.</summary>
        internal string Id(object? resource)
        {
            if (resource is null) return "null";
            if (!_ids.TryGetValue(resource, out int id))
            {
                id = _ids.Count;
                _ids[resource] = id;
            }

            return "r" + id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Forget everything, including the resource ids, so one log can measure several frames one at a
        /// time.</summary>
        internal void Reset()
        {
            _counts.Clear();
            _trace.Clear();
            _ids.Clear();
            TotalCalls = 0;
        }

        /// <summary>The trace as one line per call, for a failure message.</summary>
        public override string ToString() => string.Join(Environment.NewLine, _trace);
    }
}
