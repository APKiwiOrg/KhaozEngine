using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// What <see cref="D3D11CountingEmitter"/> writes into: a per-command call count and an ordered trace of the
    /// calls themselves. A plain class, so the emitter above it can stay a struct that is copied freely while
    /// every copy still writes to the same log.
    /// <para>
    /// The counts are what the native-call budget of decision T2 is measured against. The trace is what proves
    /// ORDER, and it is a list of strings on purpose: comparing two drivers means comparing two sequences, and a
    /// string sequence puts the actual divergence in the failure message rather than an index into something the
    /// reader then has to go and decode.
    /// </para>
    /// <para>
    /// Resources appear in the trace as stable ids assigned in first-seen order (<c>r0</c>, <c>r1</c>), by
    /// reference identity. That keeps a trace readable and keeps two traces over the SAME instances comparable,
    /// without a resource needing to know how to name itself.
    /// </para>
    /// </summary>
    internal sealed class D3D11EmitterCallLog
    {
        readonly Dictionary<D3D11OpCode, int> _counts = new();
        readonly List<string> _trace = new();
        readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every emitter call so far, in order, one line each.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        /// <summary>Total emitter calls, scope markers included.</summary>
        internal int TotalCalls { get; private set; }

        /// <summary>How many times one command was issued.</summary>
        internal int Count(D3D11OpCode code) => _counts.TryGetValue(code, out int n) ? n : 0;

        /// <summary>Record one emitter call.</summary>
        internal void Record(D3D11OpCode code, string arguments = "")
        {
            _counts[code] = Count(code) + 1;
            TotalCalls++;
            _trace.Add(code + "(" + arguments + ")");
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

            return "r" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Forget everything, including the resource ids. Lets one log measure several frames one at a
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
