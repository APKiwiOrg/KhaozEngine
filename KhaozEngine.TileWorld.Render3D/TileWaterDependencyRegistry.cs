using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KhaozEngine.TileWorld.Render3D;

/// <summary>Null Plane tracks availability, including negative lookups. Other entries track height planes.</summary>
internal readonly record struct TileWaterOpticsDependency(RegionCoord Region, int? Plane);

/// <summary>Owned optical metadata. Epochs exist only while a resident generation, pending request or refusal
/// stamp references them. Every created or changed epoch takes a fresh serial, so an entry released and later
/// recreated can never restore a value an older witness recorded. Reverse dependencies map an accepted surface
/// key to the water keys whose field consumed it or whose refusal awaits it.</summary>
internal sealed class TileWaterDependencyRegistry
{
    readonly Dictionary<TileWaterOpticsDependency, long> _epochs = new();
    readonly Dictionary<(RegionCoord Region, int Plane), HashSet<(RegionCoord Region, int Plane)>> _dependents = new();
    readonly Dictionary<(RegionCoord Region, int Plane), (RegionCoord Region, int Plane)[]> _sources = new();
    readonly List<TileWaterOpticsDependency> _unowned = new();
    static readonly Comparer<(RegionCoord Region, int Plane)> Order = Comparer<(RegionCoord Region, int Plane)>.Create(Compare);
    long _serial;

    /// <summary>Increments on every announced change, owned or not. Refusal latches compare it.</summary>
    internal long Revision { get; private set; }

    internal int EpochCount => _epochs.Count;

    /// <summary>Source keys with at least one registered dependent.</summary>
    internal int SourceKeyCount => _dependents.Count;

    /// <summary>Registered water keys, resident or refused.</summary>
    internal int DependentCount => _sources.Count;

    internal long Acquire(TileWaterOpticsDependency dependency)
    {
        ref long epoch = ref CollectionsMarshal.GetValueRefOrAddDefault(_epochs, dependency, out bool exists);
        if (!exists) epoch = ++_serial;
        return epoch;
    }

    /// <summary>An absent entry is never current, so a stamp that lost its ownership fails safe.</summary>
    internal bool IsCurrent(TileWaterOpticsDependency dependency, long epoch) =>
        _epochs.TryGetValue(dependency, out long current) && current == epoch;

    internal void Changed(TileWaterOpticsDependency dependency)
    {
        Revision++;
        ref long epoch = ref CollectionsMarshal.GetValueRefOrNullRef(_epochs, dependency);
        if (!Unsafe.IsNullRef(ref epoch)) epoch = ++_serial;
    }

    internal void SettingsChanged() => Revision++;

    /// <summary>Replace the source keys one water key depends on.</summary>
    internal void Register((RegionCoord Region, int Plane) water, IReadOnlyList<(RegionCoord Region, int Plane)> sources)
    {
        Unregister(water);
        if (sources.Count == 0) return;
        var owned = new (RegionCoord Region, int Plane)[sources.Count];
        for (int i = 0; i < owned.Length; i++)
        {
            owned[i] = sources[i];
            ref HashSet<(RegionCoord Region, int Plane)>? set =
                ref CollectionsMarshal.GetValueRefOrAddDefault(_dependents, owned[i], out _);
            (set ??= new()).Add(water);
        }
        _sources[water] = owned;
    }

    internal void Unregister((RegionCoord Region, int Plane) water)
    {
        if (!_sources.Remove(water, out (RegionCoord Region, int Plane)[]? old)) return;
        foreach ((RegionCoord Region, int Plane) source in old)
            if (_dependents.TryGetValue(source, out HashSet<(RegionCoord Region, int Plane)>? set) &&
                set.Remove(water) && set.Count == 0)
                _dependents.Remove(source);
    }

    /// <summary>Appends registered dependents of one source key in a deterministic order.</summary>
    internal void CollectDependents((RegionCoord Region, int Plane) source, List<(RegionCoord Region, int Plane)> into)
    {
        if (!_dependents.TryGetValue(source, out HashSet<(RegionCoord Region, int Plane)>? set)) return;
        int first = into.Count;
        into.AddRange(set);
        into.Sort(first, into.Count - first, Order);
    }

    static int Compare((RegionCoord Region, int Plane) a, (RegionCoord Region, int Plane) b)
    {
        int order = a.Region.Rz.CompareTo(b.Region.Rz);
        if (order == 0) order = a.Region.Rx.CompareTo(b.Region.Rx);
        return order != 0 ? order : a.Plane.CompareTo(b.Plane);
    }

    /// <summary>Drop every epoch no live stamp references.</summary>
    internal void Sweep(IEnumerable<TileWaterSourceStamp> live)
    {
        var owned = new HashSet<TileWaterOpticsDependency>();
        foreach (TileWaterSourceStamp stamp in live)
            foreach (TileWaterSourceStamp.Entry entry in stamp.Entries) owned.Add(entry.Dependency);
        _unowned.Clear();
        foreach (TileWaterOpticsDependency dependency in _epochs.Keys)
            if (!owned.Contains(dependency)) _unowned.Add(dependency);
        foreach (TileWaterOpticsDependency dependency in _unowned) _epochs.Remove(dependency);
        _unowned.Clear();
    }

    internal void Clear()
    {
        _epochs.Clear();
        _dependents.Clear();
        _sources.Clear();
    }
}
