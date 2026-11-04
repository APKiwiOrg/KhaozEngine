using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Opt-in calling-thread dependency receipt. Geometry and proof state are never changed.</summary>
internal sealed class ContactTerrainEvidenceReads
{
    [ThreadStatic] static ContactTerrainEvidenceReads? t_current;
    readonly PhysicsNavDescentEvidence _owner;
    readonly HashSet<DescentFace> _faces = [];
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
        for (int i = 0; i < owner.FaceCount; i++) _faces.Add(owner.Face(i));
    }
    internal bool Matches(PhysicsNavDescentEvidence owner) => ReferenceEquals(_owner, owner);
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
    { if (t_current is { } r && r._faces.Contains(face)) r.Normals.Add(face.Key); }
    internal static void ReadPoints(DescentFace face)
    { if (t_current is { } r && r._faces.Contains(face)) r.Points.Add(face.Key); }
    internal static void ReadBinding(PhysicsNavDescentEvidence owner, int layer, int x, int z, DescentRawBinding? raw)
    { if (t_current is { } r && ReferenceEquals(r._owner, owner)) r.Bindings[(layer, x, z)] = raw; }
}
