using System;
using KhaozEngine.Locomotion;
using Key = KhaozEngine.TileWorld.Physics.TileWaterTraceMemo.Key;

namespace KhaozEngine.TileWorld.Physics;

// Conservative per-entry charges count each distinct reference once, even sampler-owned strings.
// Cross-entry sharing is charged again. No unbounded collection or caller reference survives accounting.
internal sealed class TileWaterTraceIdentifiers(int budget, int capacity)
{
    internal const int MinimumStringBytes = 24;
    readonly string?[] _seen = new string?[capacity];
    int _count, _charge;

    internal bool TryCharge(in Key key, ReadOnlySpan<MovementDomainContact> contacts, int retained, out int charge)
    {
        _charge = retained;
        try
        {
            if (!Add(key.Space.WorldId) || !Add(key.Space.LocalId)) { charge = 0; return false; }
            foreach (var contact in contacts)
                if (!Add(contact.Domain.WorldId) || !Add(contact.Domain.LocalId) ||
                    !Add(contact.Space.WorldId) || !Add(contact.Space.LocalId) ||
                    !Add(contact.Interval.LowerBoundaryId) || !Add(contact.Interval.UpperBoundaryId) || !Add(contact.BoundaryId))
                { charge = 0; return false; }
            charge = _charge - retained;
            return true;
        }
        finally { Array.Clear(_seen, 0, _count); _count = _charge = 0; }
    }

    bool Add(string? value)
    {
        if (value is null) return true;
        for (int i = 0; i < _count; i++) if (ReferenceEquals(_seen[i], value)) return true;
        long bytes = (2L * IntPtr.Size + sizeof(int) + 2L * (value.Length + 1) + 7) & ~7L;
        if (bytes > budget - _charge || _count == _seen.Length) return false;
        _seen[_count++] = value;
        _charge += (int)bytes;
        return true;
    }
}
