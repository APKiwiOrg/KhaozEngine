using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

internal enum DescentCoverage : byte { Unavailable, Complete, KnownEmpty, OutOfBounds, Excluded }
internal readonly record struct DescentColumnFace(int Face, uint Areas);
internal readonly record struct DescentRawBinding(int X, int Z, int Ordinal, int HeightBits, int Face);

internal sealed class DescentFace(MovementSurfaceFeatureKey key, MovementSupportKey owner, bool analytic,
    PhysicsEvidenceKind kind, PhysicsEvidencePoint[] points, Pose pose, float radius, float halfLength,
    MovementSupportKey? canonicalOwner = null)
{
    readonly PhysicsEvidencePoint[] _points = (PhysicsEvidencePoint[])points.Clone();
    internal MovementSurfaceFeatureKey Key { get; } = key;
    internal MovementSupportKey Owner { get; } = owner;
    internal MovementSupportKey CanonicalOwner { get; } = canonicalOwner ?? owner;
    internal bool Analytic { get; } = analytic;
    internal PhysicsEvidenceKind Kind { get; } = kind;
    internal ReadOnlySpan<PhysicsEvidencePoint> Points
    { get { ContactTerrainEvidenceReads.ReadPoints(this); return _points; } }
    internal Pose Pose { get; } = pose;
    internal float Radius { get; } = radius;
    internal float HalfLength { get; } = halfLength;
    internal Vector3 Normal
    {
        get
        {
            ContactTerrainEvidenceReads.ReadNormal(this);
            if (_points.Length < 3) return default;
            var a = _points[0]; var b = _points[1]; var c = _points[2];
            double x = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y);
            double y = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
            double z = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            double length = Math.Sqrt(x * x + y * y + z * z);
            return length > 0 ? new((float)(x / length), (float)(y / length), (float)(z / length)) : default;
        }
    }
}

/// <summary>Sparse immutable physical coverage, separate from standable columns and all admission.</summary>
internal sealed class PhysicsNavDescentEvidence
{
    readonly DescentFace[] _faces;
    readonly int[] _starts;
    readonly DescentColumnFace[] _columns;
    readonly DescentCoverage[] _coverage;
    readonly DescentRawBinding[] _raw;
    readonly int[][] _joins;
    readonly DescentEvidenceLookup _lookup;
    internal bool StrictDependencies { get; private init; }
    internal PhysicsNavBakeOptions Options { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int FaceCount => _faces.Length;
    internal int ReferenceCount => _columns.Length;
    internal int RawCount => _raw.Length;
    internal long GeometryBytes => _faces.Length * 64L + PointCount * 24L;
    internal int PointCount { get { int total = 0; foreach (var face in _faces) total += face.Points.Length; return total; } }
    internal long RetainedPayloadBytes { get { long n = GeometryBytes + _starts.Length * 4L + _columns.Length * 8L + _coverage.Length + _raw.Length * 20L; foreach (var j in _joins) n += j.Length * 4L; return n; } }
    internal PhysicsNavDescentEvidence(PhysicsNavBakeOptions options, int width, int height, DescentFace[] faces,
        int[] starts, DescentColumnFace[] columns, DescentCoverage[] coverage, DescentRawBinding[] raw, int[][] joins,
        DescentCaptureIndexBudget? indexBudget = null)
    {
        Options = options; Width = width; Height = height; _faces = faces; _starts = starts;
        _columns = columns; _coverage = coverage; _raw = raw; _joins = joins;
        _lookup = new(faces, raw, indexBudget ?? new());
    }
    internal ReadOnlySpan<DescentRawBinding> RawBindings => _raw;
    internal DescentFace Face(int index) { ContactTerrainEvidenceReads.ReadFace(this, index); return _faces[index]; }
    internal DescentCoverage Coverage(int x, int z)
    {
        ContactTerrainEvidenceReads.ReadCoverage(this, x, z);
        var coverage = StoredCoverage(x, z);
        if (StrictDependencies && coverage == DescentCoverage.Excluded) MissingDependency();
        return coverage;
    }
    internal DescentCoverage StoredCoverage(int x, int z) => (uint)x < (uint)Width && (uint)z < (uint)Height
        ? _coverage[z * Width + x] : DescentCoverage.OutOfBounds;
    internal ReadOnlySpan<DescentColumnFace> Column(int x, int z)
    {
        ContactTerrainEvidenceReads.ReadColumn(this, x, z);
        if (StrictDependencies && StoredCoverage(x, z) == DescentCoverage.Excluded) MissingDependency();
        return _columns.AsSpan(_starts[z * Width + x], _starts[z * Width + x + 1] - _starts[z * Width + x]);
    }
    internal ReadOnlySpan<int> Joins(int face) { ContactTerrainEvidenceReads.ReadAdjacency(this, face); return _joins[face]; }
    internal bool TryRaw(int x, int z, int bits, out DescentRawBinding result)
    {
        if (StrictDependencies && StoredCoverage(x, z) == DescentCoverage.Excluded) MissingDependency();
        return _lookup.TryRaw(x, z, bits, out result);
    }
    internal bool OwnsFace(DescentFace face) => _lookup.Owns(face);
    internal DescentSolidInterval SolidInterval(int face)
    {
        var interval = _lookup.Solid(_faces[face]);
        foreach (int member in interval.Members)
        {
            ContactTerrainEvidenceReads.ReadFace(this, member);
            ContactTerrainEvidenceReads.ReadPoints(_faces[member]);
        }
        return interval;
    }
    internal PhysicsNavDescentEvidence MissingColumnForTests(int x, int z)
    { var coverage = (DescentCoverage[])_coverage.Clone(); coverage[z * Width + x] = DescentCoverage.Unavailable; return new(Options, Width, Height, _faces, _starts, _columns, coverage, _raw, _joins); }
    PhysicsNavDescentEvidence(PhysicsNavDescentEvidence source)
    {
        Options = source.Options; Width = source.Width; Height = source.Height;
        _faces = source._faces; _starts = source._starts; _columns = source._columns;
        _coverage = source._coverage; _raw = source._raw; _joins = source._joins; _lookup = source._lookup;
        StrictDependencies = true;
    }
    internal PhysicsNavDescentEvidence RequireDependencies() => StrictDependencies ? this : new(this);
    internal static void MissingDependency() => throw new PhysicsNavDescentEvidenceException(
        PhysicsNavDescentEvidenceReason.DependencyMissing, PhysicsEvidenceStatus.Unavailable);
}
