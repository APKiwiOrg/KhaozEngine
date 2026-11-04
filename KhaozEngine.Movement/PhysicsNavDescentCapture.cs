using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

internal static class PhysicsNavDescentCapture
{
    [ThreadStatic] static bool t_reference;
    internal static ReferenceScope ReferenceScanForTests()
    { bool prior = t_reference; t_reference = true; return new(prior); }
    internal readonly struct ReferenceScope(bool prior) : IDisposable
    { public void Dispose() => t_reference = prior; }
    sealed record Observed(StaticHandle Static, PhysicsEvidenceFace Address, PhysicsEvidencePoint[] Points,
        MovementSupportKey Owner, bool Analytic);

    internal static PhysicsNavDescentEvidence Capture(PhysicsNavBake bake)
    {
        var context = bake.Context;
        if (context.Physics is not IPhysicsStaticEvidence provider || context.Physics is not IPhysicsQueryLeaseSource leases ||
            context.MovementRegistration is not { } registration || registration.AnalyticSupport is not { } source ||
            !source.Matches(context.GroundHeight, context.GroundNormal, OrdinaryGroundSpace.World))
            throw Refuse(PhysicsNavDescentEvidenceReason.CapabilityUnavailable);
        using var lease = leases.AcquireQueryReadLease();
        if (bake.Basis is { } basis && (basis.GeometryGeneration != lease.GeometryGeneration || basis.Origin != lease.Origin))
            throw Refuse(PhysicsNavDescentEvidenceReason.BackendRefused);
        if (lease.Origin != bake.Origin) throw Refuse(PhysicsNavDescentEvidenceReason.BackendRefused);
        var options = bake.Options;
        var observed = new List<Observed>();
        var status = provider.CaptureStaticEvidence(lease,
            new Vector3(options.MinX, options.ProbeHeight - options.ProbeRange, options.MinZ) - lease.Origin,
            new Vector3(options.MaxX, options.ProbeHeight, options.MaxZ) - lease.Origin,
            options.MaxLayerCells, Visit);
        if (status != PhysicsEvidenceStatus.Complete)
            throw new PhysicsNavDescentEvidenceException(status == PhysicsEvidenceStatus.CapacityExceeded ?
                PhysicsNavDescentEvidenceReason.CapacityExceeded : PhysicsNavDescentEvidenceReason.BackendRefused, status);
        var hooks = ContactTerrainCaptureHooks.Current;
        hooks?.Progress?.Invoke(ContactTerrainCaptureStage.Geometry, observed.Count, observed.Count);
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices)
        {
            if (registration.BindCapturedFeature(lease, face.Static, face.Feature, out var owner, out bool analytic) != MovementAvailability.Known)
                throw Refuse(PhysicsNavDescentEvidenceReason.IdentityUnavailable);
            var points = vertices.ToArray();
            for (int i = 0; i < points.Length; i++) points[i] = new(points[i].X + lease.Origin.X,
                points[i].Y + lease.Origin.Y, points[i].Z + lease.Origin.Z);
            if (face.Kind == PhysicsEvidenceKind.Triangle) Array.Reverse(points);
            observed.Add(new(face.Static, face with { Pose = face.Pose with { Position = face.Pose.Position + lease.Origin } }, points, owner, analytic));
            return true;
        }
        var faces = new DescentFace[observed.Count];
        var owners = observed.GroupBy(o => o.Static).ToDictionary(g => g.Key, g => Digest(g));
        for (int i = 0; i < faces.Length; i++)
        {
            var input = observed[i];
            MovementSurfaceFeatureKey key;
            MovementSupportKey canonicalOwner = input.Owner;
            if (input.Analytic)
            {
                if (input.Points.Length != 3) throw Refuse(PhysicsNavDescentEvidenceReason.IdentityUnavailable);
                var centre = new Vector2((float)input.Points.Average(p => p.X), (float)input.Points.Average(p => p.Z));
                if (source.Describe(centre, out var feature) != MovementAvailability.Known || !SameTriangle(input.Points, feature))
                    throw Refuse(PhysicsNavDescentEvidenceReason.IdentityUnavailable);
                key = feature.Key;
                canonicalOwner = feature.Owner;
            }
            else key = new("installed/" + registration.CaptureProvenance + "/" + input.Owner.LocalId + "/" + owners[input.Static],
                input.Address.Leaf, input.Address.Feature);
            faces[i] = new(key, input.Owner, input.Analytic, input.Address.Kind, input.Points,
                input.Address.Pose, input.Address.Radius, input.Address.HalfLength, canonicalOwner);
        }
        int width = bake.Columns.Width, height = bake.Columns.Height;
        var budget = new DescentCaptureIndexBudget();
        var index = t_reference ? null : new DescentFaceIndex(faces, budget);
        int[] reference = t_reference ? Enumerable.Range(0, faces.Length).ToArray() : [];
        var firstKeys = new Dictionary<MovementSurfaceFeatureKey, int>();
        var primitives = new Dictionary<(StaticHandle Static, int Leaf), int>();
        budget.Reserve(faces.Length, 160);
        for (int f = 0; f < faces.Length; f++) firstKeys.TryAdd(faces[f].Key, f);
        for (int f = 0; f < faces.Length; f++)
            if (faces[f].Kind is PhysicsEvidenceKind.Sphere or PhysicsEvidenceKind.Capsule or PhysicsEvidenceKind.Cylinder)
                if (!primitives.TryAdd((observed[f].Static, observed[f].Address.Leaf), f))
                    primitives[(observed[f].Static, observed[f].Address.Leaf)] = -1;
        hooks?.Progress?.Invoke(ContactTerrainCaptureStage.Index, faces.Length, faces.Length);
        var starts = new int[checked(width * height + 1)];
        var coverage = new DescentCoverage[width * height];
        var refs = new List<DescentColumnFace>();
        var raw = new List<DescentRawBinding>();
        var observer = new PhysicsColumnEvidenceProbe(context.Physics, lease);
        var probe = new PhysicsColumnProbe(observer)
        {
            ProbeHeight = options.ProbeHeight - lease.Origin.Y,
            ProbeRange = options.ProbeRange,
            MaxSlopeRadians = options.MaxSlopeRadians,
            GroundMobility = QueryMobility.Statics
        };
        var output = new ColumnSurface[options.MaxSurfacesPerColumn + 1];
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int cell = z * width + x;
                starts[cell] = refs.Count;
                double left = options.MinX + x * (double)options.CellSize, bottom = options.MinZ + z * (double)options.CellSize;
                if (left + options.CellSize > options.MaxX || bottom + options.CellSize > options.MaxZ)
                { coverage[cell] = DescentCoverage.OutOfBounds; continue; }
                coverage[cell] = DescentCoverage.Complete;
                ReadOnlySpan<int> candidates = index is null ? reference : index.Query(left, bottom, left + options.CellSize, bottom + options.CellSize);
                foreach (int f in candidates)
                {
                    var face = faces[f];
                    if (face.Points.Length < 3)
                    {
                        double extent = face.Radius + face.HalfLength;
                        if (face.Pose.Position.X + extent >= left && face.Pose.Position.X - extent <= left + options.CellSize &&
                            face.Pose.Position.Z + extent >= bottom && face.Pose.Position.Z - extent <= bottom + options.CellSize)
                            refs.Add(new(f, bake.Classifier(face.Pose.Position)));
                        continue;
                    }
                    var clipped = DescentGeometry.Cell(face.Points, left, bottom, options.CellSize);
                    if (DescentGeometry.Area2(clipped).Sign == 0)
                    {
                        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minZ = minX, maxZ = maxX;
                        foreach (var p in face.Points) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
                        if (!face.Analytic && minX <= left + options.CellSize && maxX >= left && minZ <= bottom + options.CellSize && maxZ >= bottom)
                            refs.Add(new(f, bake.Classifier(new((float)face.Points[0].X, (float)face.Points[0].Y, (float)face.Points[0].Z))));
                        continue;
                    }
                    var point = clipped[0];
                    if (face.Normal.Y != 0)
                    {
                        DescentNumber cx = default, cz = default;
                        foreach (var p in clipped) { cx += p.X; cz += p.Z; }
                        point = new(cx / new DescentNumber(clipped.Count), cz / new DescentNumber(clipped.Count));
                    }
                    double y = face.Normal.Y == 0 ? face.Points[0].Y : DescentGeometry.Height(face.Points, point).Value;
                    refs.Add(new(f, bake.Classifier(new((float)point.X.Value, (float)y, (float)point.Z.Value))));
                    if (refs.Count > options.MaxLayerCells) throw Refuse(PhysicsNavDescentEvidenceReason.CapacityExceeded);
                }
                if (refs.Count > options.MaxLayerCells) throw Refuse(PhysicsNavDescentEvidenceReason.CapacityExceeded);
                if (refs.Count == starts[cell]) coverage[cell] = DescentCoverage.KnownEmpty;
                observer.Hits.Clear();
                float ax = options.MinX + (x + 0.5f) * options.CellSize, az = options.MinZ + (z + 0.5f) * options.CellSize;
                // Raw probes use float anchors. At large coordinates rounding can place one outside the double cell bounds.
                candidates = index is null ? reference : index.Query(ax, az, ax, az);
                int count = probe.Sample(ax - lease.Origin.X, az - lease.Origin.Z, output);
                var originals = bake.Columns.GetColumn(x, z);
                if (count != originals.Length) throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                for (int ordinal = 0; ordinal < count; ordinal++)
                {
                    int bits = BitConverter.SingleToInt32Bits(originals[ordinal].Height);
                    if (bits != BitConverter.SingleToInt32Bits(output[ordinal].Height + lease.Origin.Y))
                        throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                    var hits = observer.Hits.Where(h => BitConverter.SingleToInt32Bits(h.Result.Point.Y + lease.Origin.Y) == bits && h.Result.Distance > 0).ToArray();
                    if (hits.Length != 1 || hits[0].Result.Body is not { } body) throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                    int selected = -1;
                    if (t_reference)
                        for (int f = 0; f < faces.Length; f++)
                        {
                            if (observed[f].Static != body || observed[f].Address.Leaf != hits[0].Leaf ||
                                faces[f].Kind is not (PhysicsEvidenceKind.Sphere or PhysicsEvidenceKind.Capsule or PhysicsEvidenceKind.Cylinder)) continue;
                            if (selected >= 0) throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                            selected = f;
                        }
                    else if (primitives.TryGetValue((body, hits[0].Leaf), out int primitive))
                    {
                        if (primitive < 0) throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                        selected = primitive;
                    }
                    if (selected >= 0)
                    {
                        raw.Add(new(x, z, ordinal, bits, selected));
                        continue;
                    }
                    DescentNumber highest = default;
                    foreach (int f in candidates)
                    {
                        var face = faces[f];
                        if (observed[f].Static != body || hits[0].Leaf >= 0 && observed[f].Address.Leaf != hits[0].Leaf ||
                            face.Normal.Y <= 0 || !DescentGeometry.Contains(face.Points, ax, az)) continue;
                        var y = DescentGeometry.Height(face.Points, new(ax, az));
                        if (y > new DescentNumber(hits[0].Origin.Y + lease.Origin.Y)) continue;
                        if (selected >= 0 && y.CompareTo(highest) == 0 && face.Key != faces[selected].Key &&
                            !(face.Analytic && faces[selected].Analytic && source.AreAdjacent(face.Key, faces[selected].Key)))
                            throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                        if (selected < 0 || y > highest) { selected = f; highest = y; }
                    }
                    if (selected < 0) throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                    foreach (int f in candidates)
                    {
                        if (f == selected || faces[f].Normal.Y <= 0 || !DescentGeometry.Contains(faces[f].Points, ax, az)) continue;
                        var sameHeight = DescentGeometry.Height(faces[f].Points, new(ax, az));
                        if (sameHeight.CompareTo(highest) != 0) continue;
                        var overlap = DescentGeometry.Overlap(DescentGeometry.Project(faces[f].Points), DescentGeometry.Project(faces[selected].Points));
                        if (DescentGeometry.Area2(overlap).Sign > 0)
                            throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                        if (!(faces[f].Analytic && faces[selected].Analytic && source.AreAdjacent(faces[f].Key, faces[selected].Key)) &&
                            observed[f].Static != observed[selected].Static)
                            throw Refuse(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous);
                    }
                    // Shared triangle edges use the analytic field's own deterministic selection.
                    if (faces[selected].Analytic && source.Describe(new(ax, az), out var named) == MovementAvailability.Known)
                    {
                        if (t_reference)
                            for (int f = 0; f < faces.Length; f++) if (faces[f].Key == named.Key) { selected = f; break; }
                        if (!t_reference && firstKeys.TryGetValue(named.Key, out int canonical)) selected = canonical;
                    }
                    raw.Add(new(x, z, ordinal, bits, selected));
                }
            }
            hooks?.Progress?.Invoke(ContactTerrainCaptureStage.Columns, z + 1, height);
        }
        starts[^1] = refs.Count;
        var joins = new DescentAdjacencyIndex(faces, source, budget).Build(t_reference);
        hooks?.IndexStorage?.Invoke(budget.UsedBytes);
        hooks?.Progress?.Invoke(ContactTerrainCaptureStage.Adjacency, faces.Length, faces.Length);
        lease.AssertCurrent();
        return new(options, width, height, faces, starts, refs.ToArray(), coverage, raw.ToArray(), joins);
    }

    static bool SameTriangle(PhysicsEvidencePoint[] points, MovementSurfaceFeature feature)
    {
        Vector3[] expected = [feature.A, feature.B, feature.C];
        return points.All(p => expected.Any(v => p.X == v.X && p.Y == v.Y && p.Z == v.Z));
    }
    static string Digest(IEnumerable<Observed> rows)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            foreach (var row in rows.OrderBy(r => r.Address.Leaf).ThenBy(r => r.Address.Feature))
            {
                writer.Write((byte)row.Address.Kind); writer.Write(row.Address.Leaf); writer.Write(row.Address.Feature);
                foreach (var p in row.Points) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
                writer.Write(row.Address.Pose.Position.X); writer.Write(row.Address.Pose.Position.Y); writer.Write(row.Address.Pose.Position.Z);
                writer.Write(row.Address.Pose.Orientation.X); writer.Write(row.Address.Pose.Orientation.Y);
                writer.Write(row.Address.Pose.Orientation.Z); writer.Write(row.Address.Pose.Orientation.W);
                writer.Write(row.Address.Radius); writer.Write(row.Address.HalfLength);
            }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    static PhysicsNavDescentEvidenceException Refuse(PhysicsNavDescentEvidenceReason reason) => new(reason, PhysicsEvidenceStatus.Unavailable);
}
