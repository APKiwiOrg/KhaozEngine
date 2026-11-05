using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Opt-in calling-thread dependency receipt. Geometry and proof state are never changed.</summary>
internal sealed class ContactTerrainEvidenceReads
{
    [ThreadStatic] static ContactTerrainEvidenceReads? t_current;
    readonly PhysicsNavDescentEvidence _owner;
    internal HashSet<(int X, int Z)> Coverage { get; } = [];
    internal HashSet<(int X, int Z)> Columns { get; } = [];
    internal HashSet<int> FaceRecords { get; } = [];
    internal HashSet<int> Adjacency { get; } = [];
    internal HashSet<MovementSurfaceFeatureKey> Normals { get; } = [];
    internal HashSet<MovementSurfaceFeatureKey> Points { get; } = [];
    internal Dictionary<(int Layer, int X, int Z), DescentRawBinding?> Bindings { get; } = [];
    internal ContactTerrainEvidenceReads(PhysicsNavDescentEvidence owner)
    {
        _owner = owner;
    }
    internal bool Matches(PhysicsNavDescentEvidence owner) => ReferenceEquals(_owner, owner);
    internal void UnionWith(ContactTerrainEvidenceReads other)
    {
        if (!other.Matches(_owner)) throw new ArgumentException("The dependency receipt belongs to another capture.", nameof(other));
        Coverage.UnionWith(other.Coverage); Columns.UnionWith(other.Columns);
        FaceRecords.UnionWith(other.FaceRecords); Adjacency.UnionWith(other.Adjacency);
        Normals.UnionWith(other.Normals); Points.UnionWith(other.Points);
        foreach (var (key, value) in other.Bindings)
        {
            if (Bindings.TryGetValue(key, out var prior) && prior != value)
                throw new PhysicsNavDescentEvidenceException(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous,
                    KhaozEngine.Physics.PhysicsEvidenceStatus.Unavailable);
            Bindings[key] = value;
        }
    }
    internal Scope Observe()
    {
        var prior = t_current;
        t_current = this;
        return new(prior);
    }
    internal readonly struct Scope(ContactTerrainEvidenceReads? prior) : IDisposable
    { public void Dispose() => t_current = prior; }
    internal static void ReadCoverage(PhysicsNavDescentEvidence owner, int x, int z)
    { if (t_current is { } r && ReferenceEquals(r._owner, owner)) r.Coverage.Add((x, z)); }
    internal static void ReadColumn(PhysicsNavDescentEvidence owner, int x, int z)
    { if (t_current is { } r && ReferenceEquals(r._owner, owner)) r.Columns.Add((x, z)); }
    internal static void ReadFace(PhysicsNavDescentEvidence owner, int index)
    { if (t_current is { } r && ReferenceEquals(r._owner, owner)) r.FaceRecords.Add(index); }
    internal static void ReadAdjacency(PhysicsNavDescentEvidence owner, int index)
    { if (t_current is { } r && ReferenceEquals(r._owner, owner)) r.Adjacency.Add(index); }
    internal static void ReadNormal(DescentFace face)
    { if (t_current is { } r && r._owner.OwnsFace(face)) r.Normals.Add(face.Key); }
    internal static void ReadPoints(DescentFace face)
    { if (t_current is { } r && r._owner.OwnsFace(face)) r.Points.Add(face.Key); }
    internal static void ReadBinding(PhysicsNavDescentEvidence owner, int layer, int x, int z, DescentRawBinding? raw)
    { if (t_current is { } r && ReferenceEquals(r._owner, owner)) r.Bindings[(layer, x, z)] = raw; }
}
