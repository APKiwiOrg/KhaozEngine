using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The engine-owned CPU command stream of decision R1: a growable array of 32-byte <see cref="D3D11Op"/>
    /// values, the <see cref="D3D11ReferenceList"/> that keeps their resource arguments alive, and the
    /// <see cref="D3D11PayloadArena"/> that holds their bulk bytes. Storage only. The encoding lives in
    /// <see cref="D3D11StreamEmitter"/> and the decoding in <see cref="D3D11StreamReplay"/>, which is what keeps
    /// three jobs out of one file (the incumbent's equivalent is 1751 lines against an 800-line cap).
    /// <para>
    /// <see cref="Reset"/> is what <c>Begin</c> reaches, and it is one integer write plus two child resets. No
    /// native call, no lock, no device contact, which is the property section 5.1 states and the one that makes a
    /// nested or concurrent <c>Begin</c> structurally harmless: two recorders are two arrays, so there is no
    /// shared device state for one to wipe out from under the other.
    /// </para>
    /// <para>
    /// The array is REUSED across recordings and only ever grows to a frame's high-water mark. Ops hold no
    /// managed references, so truncation neither allocates nor writes barriers.
    /// </para>
    /// </summary>
    internal sealed class D3D11CommandStream
    {
        D3D11Op[] _ops;
        int _count;
        readonly D3D11ReferenceList _references;
        readonly D3D11PayloadArena _payloads;

        internal D3D11CommandStream(int capacity = 256, int referenceCapacity = 64, int payloadCapacity = 4096)
        {
            _ops = new D3D11Op[capacity < 1 ? 1 : capacity];
            _references = new D3D11ReferenceList(referenceCapacity);
            _payloads = new D3D11PayloadArena(payloadCapacity);
        }

        /// <summary>Ops recorded so far.</summary>
        internal int Count => _count;

        /// <summary>The recorded ops, in record order. The replay walks this.</summary>
        internal ReadOnlySpan<D3D11Op> Ops => _ops.AsSpan(0, _count);

        /// <summary>The op array's current allocation, which is the high-water mark it has grown to.</summary>
        internal int Capacity => _ops.Length;

        /// <summary>How many resource references this recording holds.</summary>
        internal int ReferenceCount => _references.Count;

        /// <summary>Bytes of bulk payload this recording holds.</summary>
        internal int PayloadLength => _payloads.Length;

        /// <summary>True once <c>End</c> has sealed the recording. This mirrors the recorder's own seal flag
        /// for test visibility only. Replaying a list that was never ended would replay a half-recorded frame
        /// rather than failing. In production, Submit checks the recorder's <c>IsSealed</c> instead.</summary>
        internal bool Sealed { get; private set; }

        /// <summary>Truncate to zero and drop every reference and payload. What <c>Begin</c> does.</summary>
        internal void Reset()
        {
            _count = 0;
            _references.Reset();
            _payloads.Reset();
            Sealed = false;
        }

        /// <summary>Seal the recording. What <c>End</c> does.</summary>
        internal void Seal() => Sealed = true;

        /// <summary>Append one op.</summary>
        internal void Append(in D3D11Op op)
        {
            if (_count == _ops.Length) Array.Resize(ref _ops, _ops.Length * 2);
            _ops[_count++] = op;
        }

        /// <summary>Store a resource argument and return the index an op should carry.</summary>
        internal int AddReference(object? resource) => _references.Add(resource);

        /// <summary>Copy a bulk payload in and return the offset an op should carry.</summary>
        internal int AddPayload(ReadOnlySpan<byte> data) => _payloads.Append(data);

        /// <summary>The typed resource an op's index names.</summary>
        internal T Reference<T>(int index) where T : class => _references.Get<T>(index);

        /// <summary>The bytes an op's offset and length name.</summary>
        internal ReadOnlySpan<byte> Payload(int offset, int length) => _payloads.Slice(offset, length);

        /// <summary>
        /// Replay every recorded op into <paramref name="emitter"/>, bracketed by one <c>Begin</c> and one
        /// <c>End</c>. Generic over a STRUCT emitter so the JIT monomorphizes the whole loop and the production
        /// path carries no interface dispatch, which is section 5.1's shape verbatim.
        /// <para>
        /// By reference to avoid copying the struct on entry and, more to the point, to avoid the defensive copy
        /// per call that an <c>in</c> parameter of a constrained type parameter would force. It is a cost
        /// property and not a correctness one: an emitter's mutable state lives behind a class reference (see
        /// <see cref="ID3D11Emitter"/>), so a copy would drive the same emission.
        /// </para>
        /// </summary>
        internal void Replay<TEmitter>(ref TEmitter emitter) where TEmitter : struct, ID3D11Emitter
            => D3D11StreamReplay.Run(this, ref emitter);
    }
}
