using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal readonly record struct ContactNavigationRawSource(int ColumnX, int ColumnZ, int SurfaceOrdinal, int RawHeightBits);

internal sealed record ContactNavigationSource(ContactNavigationRawSource Raw, int Layer);

/// <summary>Original admitted sample addresses, bound to derived cells before resting heights are substituted.
/// Missing or ambiguous associations disable reuse without changing admission or layer extraction.</summary>
internal sealed class ContactNavigationSources
{
    readonly Dictionary<(int X, int Y, int Z), ContactNavigationRawSource?> _raw = [];
    readonly Dictionary<(int Layer, int X, int Z), ContactNavigationSource> _cells = [];

    internal void Record(Vector3 rawFeet, in ContactNavigationRawSource raw)
    {
        var key = Bits(rawFeet);
        if (raw.RawHeightBits != key.Y || !_raw.TryAdd(key, raw)) _raw[key] = null;
    }

    internal void BindLayers(NavSpace rawSpace)
    {
        _cells.Clear();
        for (int layer = 0; layer < rawSpace.Layers.Count; layer++)
        {
            NavGrid grid = rawSpace.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (grid.ClearanceAt(x, z) == 0 || grid.SurfaceHeightAt(x, z) is not float height) continue;
                    Vector2 centre = grid.CellCenter(x, z);
                    if (_raw.TryGetValue(Bits(new(centre.X, height, centre.Y)), out var raw) &&
                        raw is { } address && address.ColumnX == x && address.ColumnZ == z)
                        _cells.Add((layer, x, z), new(address, layer));
                }
        }
    }

    internal bool TryGet(int layer, int x, int z, out ContactNavigationSource source) =>
        _cells.TryGetValue((layer, x, z), out source!);

    static (int X, int Y, int Z) Bits(Vector3 feet) => (BitConverter.SingleToInt32Bits(feet.X),
        BitConverter.SingleToInt32Bits(feet.Y), BitConverter.SingleToInt32Bits(feet.Z));
}
