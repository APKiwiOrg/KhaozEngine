using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE ONE PLACE A RECORDING HOLDS A MANAGED REFERENCE. A resource argument becomes an index into this list,
    /// and the strong reference stored here is what keeps that resource alive for the recording's lifetime, which
    /// is section 5.1's rule. Ops themselves stay pure value data, so truncating a stream costs one integer.
    /// <para>
    /// The lifetime is the RECORDING, and not a byte longer. <see cref="Reset"/> nulls the slots it used rather
    /// than only rewinding the count, so a resource disposed by its owner between two frames is not held alive by
    /// last frame's array. Rewinding alone would have been the cheaper write and the wrong one: it would turn the
    /// stream into a one-frame-deep retention pool that nothing in the design accounts for.
    /// </para>
    /// <para>
    /// Consecutive references to the SAME instance collapse to one entry. That is not a micro-optimisation but a
    /// bound: decision R5 names thousands of offsets-only rebinds of one set per frame as the hot path, and
    /// without the collapse each of them would append another slot to an array that only resets at
    /// <c>Begin</c>. The dedup is a single reference comparison, so it costs nothing when it misses.
    /// </para>
    /// </summary>
    internal sealed class D3D11ReferenceList
    {
        /// <summary>The index an op carries when its command takes no resource argument.</summary>
        internal const int NoReference = -1;

        object?[] _slots;
        int _count;
        object? _last;
        int _lastIndex = NoReference;

        internal D3D11ReferenceList(int capacity = 64) => _slots = new object?[capacity < 1 ? 1 : capacity];

        /// <summary>How many references the current recording holds.</summary>
        internal int Count => _count;

        /// <summary>Store <paramref name="resource"/> and return the index an op should carry. A null resource
        /// records <see cref="NoReference"/> rather than a slot, so an op that took no resource and an op whose
        /// resource was null are indistinguishable, which is correct: the seam has no command that legitimately
        /// binds null.</summary>
        internal int Add(object? resource)
        {
            if (resource is null) return NoReference;
            if (ReferenceEquals(resource, _last)) return _lastIndex;

            if (_count == _slots.Length) Array.Resize(ref _slots, _slots.Length * 2);
            _slots[_count] = resource;
            _last = resource;
            _lastIndex = _count;
            return _count++;
        }

        /// <summary>The resource at <paramref name="index"/>, typed. Throws rather than handing back null,
        /// because a bad index or a wrong type here means the encoder and the replay switch disagree about what a
        /// command carries, which is a defect in this package and never anything a caller did.</summary>
        internal T Get<T>(int index) where T : class
        {
            if ((uint)index >= (uint)_count)
                throw new InvalidOperationException(
                    $"Command-stream reference {index} is outside the {_count} references this recording holds. "
                    + "The op encoder and the replay switch disagree about which arguments this command carries.");

            return _slots[index] as T
                ?? throw new InvalidOperationException(
                    $"Command-stream reference {index} is a {_slots[index]?.GetType().Name ?? "null"} and the "
                    + $"replay asked for a {typeof(T).Name}. The op encoder and the replay switch disagree about "
                    + "which arguments this command carries.");
        }

        /// <summary>Drop every reference and rewind. Called by <c>Begin</c> through the stream, and again when
        /// the owning command list is disposed.</summary>
        internal void Reset()
        {
            Array.Clear(_slots, 0, _count);
            _count = 0;
            _last = null;
            _lastIndex = NoReference;
        }
    }
}
