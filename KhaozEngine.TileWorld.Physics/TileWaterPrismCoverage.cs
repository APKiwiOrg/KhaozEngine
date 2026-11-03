using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using R = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Range;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Full capsule coverage over the captured triangle-water union, with bounded private output.</summary>
internal static class TileWaterPrismCoverage
{
    const int SpanLimit = MovementQueryLease.MaxCoverageSpans, ContactLimit = MovementQueryLease.MaxDomainContacts;
    readonly record struct Hit(int Index, R Range);
    [ThreadStatic] static Scratch? t_scratch;
    sealed class Scratch
    {
        internal readonly float[] Cuts = new float[SpanLimit + 2];
        internal readonly Hit[] Pieces = new Hit[ContactLimit];
        internal readonly Hit[] Faces = new Hit[ContactLimit * 8];
        internal readonly MovementCoverageSpan[] Spans = new MovementCoverageSpan[SpanLimit];
        internal readonly MovementDomainContact[] Contacts = new MovementDomainContact[ContactLimit];
    }

    internal static MovementCoverageResult Trace(TileWaterPrism[] pieces, TileWaterPrismFace[] faces, CapturedTileGround ground, TileWaterColumnIndex index, int[] prismStarts, int[] faceStarts,
        in MovementMediumSweepQuery query, Vector3 origin, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        Scratch scratch = t_scratch ??= new();
        int cuts = 2, pieceCount = 0, faceCount = 0, contactCount = 0;
        scratch.Cuts[0] = 0;
        scratch.Cuts[1] = 1;
        double error = 0;
        I core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        V drive = V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z);
        Span<int> candidates = stackalloc int[ContactLimit];
        int candidateCount = index.Collect(new(query, origin), candidates);
        if (candidateCount < 0) return Refuse(MovementAvailability.CapacityExceeded, identity, 0, ContactLimit + 1);
        for (int c = 0; c < candidateCount; c++)
        for (int i = prismStarts[candidates[c]]; i < prismStarts[candidates[c] + 1]; i++)
        {
            TileWaterPrism piece = pieces[i];
            if (!piece.Candidate(query, origin)) continue;
            R range = piece.Shape.Intersect(piece.Start(query, origin), drive, core, query.Body.Radius, heightError: piece.HeightError, horizontalError: piece.HorizontalError);
            if (!range.Resolved) return Refuse(MovementAvailability.Unresolved, identity);
            if (!range.Hit) continue;
            // A certified rejection has no reported contact location. Only intersected pieces charge
            // their projection and representation uncertainty to the published trace certificate.
            error = Math.Max(error, Math.Max(range.Error, piece.RepresentationError));
            if (pieceCount == scratch.Pieces.Length || !AddRange(range, scratch.Cuts, ref cuts, query.Delta, ref error))
                return Refuse(MovementAvailability.CapacityExceeded, identity, SpanLimit + 1, ContactLimit + 1);
            scratch.Pieces[pieceCount++] = new(i, range);
        }
        for (int c = 0; c < candidateCount; c++)
        for (int i = faceStarts[candidates[c]]; i < faceStarts[candidates[c] + 1]; i++)
        {
            TileWaterPrismFace face = faces[i];
            TileWaterPrism piece = pieces[face.Prism];
            if (!piece.Candidate(query, origin)) continue;
            R range = face.Shape.Intersect(piece.Start(query, origin), drive, core, query.Body.Radius, heightError: piece.HeightError, horizontalError: piece.HorizontalError);
            if (!range.Resolved) return Refuse(MovementAvailability.Unresolved, identity);
            if (!range.Hit) continue;
            // A certified rejection has no reported contact location. Only intersected pieces charge
            // their projection and representation uncertainty to the published trace certificate.
            error = Math.Max(error, Math.Max(range.Error, piece.RepresentationError));
            if (faceCount == scratch.Faces.Length || !AddRange(range, scratch.Cuts, ref cuts, query.Delta, ref error))
                return Refuse(MovementAvailability.CapacityExceeded, identity, SpanLimit + 1, ContactLimit + 1);
            scratch.Faces[faceCount++] = new(i, range);
        }
        int spanCount = cuts - 1;
        if (spanCount > SpanLimit) return Refuse(MovementAvailability.CapacityExceeded, identity, spanCount);
        try
        {
            for (int s = 0; s < spanCount; s++)
            {
                float enter = scratch.Cuts[s], exit = scratch.Cuts[s + 1];
                int first = contactCount;
                for (int p = 0; p < pieceCount; p++)
                {
                    Hit hit = scratch.Pieces[p];
                    if (!Overlaps(hit.Range, enter, exit)) continue;
                    TileWaterPrism piece = pieces[hit.Index];
                    R covered = piece.Shape.Intersect(piece.Start(query, origin), drive, core, query.Body.Radius,
                        enter, exit, piece.HeightError, piece.HorizontalError);
                    if (!covered.Resolved) return Refuse(MovementAvailability.Unresolved, identity);
                    if (!covered.Hit) continue;
                    error = Math.Max(error, covered.Error);
                    bool boundary = false;
                    for (int f = 0; f < faceCount; f++)
                    {
                        Hit faceHit = scratch.Faces[f];
                        TileWaterPrismFace face = faces[faceHit.Index];
                        if (face.Prism != hit.Index || !face.Wet || !Overlaps(faceHit.Range, enter, exit)) continue;
                        R located = face.Shape.Intersect(piece.Start(query, origin), drive, core, query.Body.Radius,
                            enter, exit, piece.HeightError, piece.HorizontalError);
                        if (!located.Resolved) return Refuse(MovementAvailability.Unresolved, identity);
                        if (!located.Hit) continue;
                        error = Math.Max(error, located.Error);
                        boundary = true;
                        if (!Append(piece, face.Plane, located, covered, enter, exit, ground, query, origin,
                                scratch.Contacts, ref contactCount, ref error))
                            return Refuse(contactCount == ContactLimit ? MovementAvailability.CapacityExceeded : MovementAvailability.Unresolved,
                                identity, spanCount, ContactLimit + 1);
                    }
                    if (!boundary && !Append(piece, 4, covered, covered, enter, exit, ground, query, origin,
                            scratch.Contacts, ref contactCount, ref error))
                        return Refuse(contactCount == ContactLimit ? MovementAvailability.CapacityExceeded : MovementAvailability.Unresolved,
                            identity, spanCount, ContactLimit + 1);
                }
                bool dry = Dry(pieces, faces, candidates[..candidateCount], prismStarts, scratch.Faces.AsSpan(0, faceCount), query, origin, enter, exit);
                scratch.Spans[s] = new(enter, exit, first, contactCount - first, dry || first == contactCount);
            }
            float certificate = TileWaterColumnCoverage.Upper(error);
            if (!float.IsFinite(certificate) || certificate > MovementQueryLease.CoverageSkinMetres)
                return Refuse(MovementAvailability.Unresolved, identity);
            if (spanCount > spans.Length || contactCount > contacts.Length)
                return Refuse(MovementAvailability.CapacityExceeded, identity, spanCount, contactCount);
            scratch.Spans.AsSpan(0, spanCount).CopyTo(spans);
            scratch.Contacts.AsSpan(0, contactCount).CopyTo(contacts);
            return new(MovementAvailability.Known, spanCount, spanCount, contactCount, contactCount, certificate, identity);
        }
        finally { scratch.Contacts.AsSpan(0, contactCount).Clear(); }
    }

    static bool Append(TileWaterPrism piece, int face, R contact, R covered, float enter, float exit,
        CapturedTileGround ground, in MovementMediumSweepQuery query, Vector3 origin,
        Span<MovementDomainContact> output, ref int count, ref double error)
    {
        if (count == output.Length) return false;
        float fraction = Math.Clamp((float)Math.Max(enter, contact.Enter), enter, exit);
        V located = contact.Witness(fraction) + piece.Reference;
        double x = TileWaterConvexShape.Mid(located.X), z = TileWaterConvexShape.Mid(located.Z);
        Vector2 column = new(Math.Clamp((float)x, piece.Column.Minimum.X, piece.Column.Maximum.X),
            Math.Clamp((float)z, piece.Column.Minimum.Z, piece.Column.Maximum.Z));
        if (!ground.TryDrawnHeight(column.X, column.Y, out float bed) || !(bed < piece.Column.Maximum.Y))
        {
            if (!piece.Witness.TryLocate(x, z, out column) ||
                !ground.TryDrawnHeight(column.X, column.Y, out bed) || !(bed < piece.Column.Maximum.Y)) return false;
        }
        double dx = column.X - x, dz = column.Y - z;
        error = Math.Max(error, located.Width + Math.Sqrt(dx * dx + dz * dz));
        I core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        R point = piece.Shape.Intersect(piece.Start(query, origin), V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z),
            core, query.Body.Radius, fraction, fraction, piece.HeightError, piece.HorizontalError);
        if (!point.Resolved) return false;
        error = Math.Max(error, point.Error);
        double squared = (double)query.Body.Radius * query.Body.Radius;
        var whole = covered.MinimumSquared >= squared ? MovementContactOverlap.Tangent : MovementContactOverlap.Overlapping;
        var overlap = whole == MovementContactOverlap.Tangent || point.MinimumSquared >= squared
            ? MovementContactOverlap.Tangent : MovementContactOverlap.Overlapping;
        output[count++] = new(piece.Column.Domain, query.Body.CurrentSpace,
            new(bed, piece.Column.Maximum.Y, piece.Column.Maximum.Y, true, piece.Column.BedBoundaryId, piece.Column.SurfaceBoundaryId),
            column, piece.Normal(face), fraction, piece.BoundaryId(face), 0, overlap, whole);
        return true;
    }

    static bool Dry(TileWaterPrism[] pieces, TileWaterPrismFace[] faces, ReadOnlySpan<int> candidates, int[] prismStarts,
        ReadOnlySpan<Hit> hits,
        in MovementMediumSweepQuery query, Vector3 origin, float enter, float exit)
    {
        double middle = ((double)enter + exit) * 0.5;
        V drive = V.Point(query.Delta.X, query.Delta.Y, query.Delta.Z);
        bool inside = false;
        for (int c = 0; c < candidates.Length && !inside; c++)
        for (int p = prismStarts[candidates[c]]; p < prismStarts[candidates[c] + 1]; p++)
        {
            TileWaterPrism piece = pieces[p];
            if (piece.Candidate(query, origin) && piece.Shape.Contains(piece.Start(query, origin) + drive * I.Point(middle)))
            { inside = true; break; }
        }
        if (!inside) return true;
        double squared = (double)query.Body.Radius * query.Body.Radius;
        I core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        foreach (Hit hit in hits)
        {
            TileWaterPrismFace face = faces[hit.Index];
            if (!face.Dry || !Overlaps(hit.Range, enter, exit)) continue;
            TileWaterPrism piece = pieces[face.Prism];
            R range = face.Shape.Intersect(piece.Start(query, origin), drive, core, query.Body.Radius, enter, exit, piece.HeightError, piece.HorizontalError);
            if (!range.Resolved || range.MinimumSquared < squared) return true;
        }
        return false;
    }

    static bool Overlaps(R range, double enter, double exit) => range.Hit && range.Exit >= enter && range.Enter <= exit;

    static bool AddRange(R range, Span<float> cuts, ref int count, Vector3 delta, ref double error)
    {
        for (int endpoint = 0; endpoint < 2; endpoint++)
        {
            double value = endpoint == 0 ? range.Enter : range.Exit;
            if (value <= 0 || value >= 1) continue;
            float low = TileWaterColumnCoverage.Lower(value), high = TileWaterColumnCoverage.Upper(value);
            error = Math.Max(error, Math.Max(value - low, high - value) * delta.Length());
            if (!Insert(low, cuts, ref count) || !Insert(high, cuts, ref count)) return false;
        }
        return true;
    }

    static bool Insert(float at, Span<float> cuts, ref int count)
    {
        int i = 0;
        while (i < count && cuts[i] < at) i++;
        if (i < count && cuts[i] == at) return true;
        if (count == cuts.Length) return false;
        cuts[i..count].CopyTo(cuts[(i + 1)..]);
        cuts[i] = at;
        count++;
        return true;
    }

    static MovementCoverageResult Refuse(MovementAvailability status, MovementQueryIdentity identity,
        int spans = 0, int contacts = 0) => new(status, 0, spans, 0, contacts, 0, identity);
}
