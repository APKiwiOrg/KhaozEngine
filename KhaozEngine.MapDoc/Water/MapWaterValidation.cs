using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Document-level water rules shared by loading, editing and saving, including exact domain geometry:
/// simple rings, no overlap between the pieces of one body, and no overlap between bodies that cover the same
/// space. Containment in the covered spaces is checked elsewhere. All geometry work in one call shares one
/// <see cref="MapWaterWorkBudget"/>, so validation stays bounded.</summary>
internal static class MapWaterValidation
{
    const int MaxMetres = 1_000_000;
    static readonly MapExactValue MaxExtent = new(MaxMetres, 1);
    static readonly MapExactValue MinExtent = new(-MaxMetres, 1);

    internal static void Validate(MapDocument doc, List<string> errors) => Validate(doc, errors, new MapWaterWorkBudget());

    /// <summary>Validates against <paramref name="budget"/>. Once it is spent the call reports the body or space that
    /// passed it and skips the remaining geometry checks.</summary>
    internal static void Validate(MapDocument doc, List<string> errors, MapWaterWorkBudget budget, List<MapDocumentFinding>? findings = null)
    {
        if (doc.Water is not { } water) return;
        if (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } ||
            doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
        {
            var finding = MapDocumentFinding.ResolverContract(doc, "water: bounded water requires a resolver-2 native document.",
                doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } ? "resolverIdentity" : "supportRecipe", "water", 2, MapSupportRecipe.AuthoredBindingsV2);
            errors.Add(finding.Message);
            findings?.Add(finding);
        }

        var media = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapWaterMediumDoc medium in water.Media) ValidateMedium(medium, media, errors, findings);
        var bodies = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<(MapWaterBodyDoc Body, MapWaterDomain Domain)>();
        foreach (MapWaterBodyDoc body in water.Bodies)
            if (ValidateBody(body, media, bodies, errors, budget, findings) is { } domain) compiled.Add((body, domain));
        ValidateSharedSpaces(compiled, errors, budget, findings);
    }

    static void ValidateMedium(MapWaterMediumDoc medium, HashSet<string> keys, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (medium is null || string.IsNullOrWhiteSpace(medium.Key))
        {
            Add(new(MapDocumentRefusal.SchemaValueRequired, "every water medium needs a non-empty key.")
            { FieldPath = medium is null ? "Media" : "Key", EntityId = medium?.Key, SubjectKind = "water medium" }, errors, findings);
            return;
        }
        string label = $"water medium '{medium.Key}'";
        if (!keys.Add(medium.Key)) Add(new(MapDocumentRefusal.IdentityDuplicate, $"{label} is duplicated.")
        { EntityId = medium.Key, SubjectKind = "water medium", FieldPath = "Key" }, errors, findings);
        if (!Enum.IsDefined(medium.Kind)) Add(new(MapDocumentRefusal.SchemaEnumUnsupported, $"{label}: kind is unsupported.")
        { EntityId = medium.Key, FieldPath = "Kind", ActualInteger = (int)medium.Kind, EnumType = typeof(MapWaterMediumKind) }, errors, findings);
        if (!float.IsFinite(medium.WadeSpeedScale) || !(medium.WadeSpeedScale > 0f) || medium.WadeSpeedScale > 1f)
            Add(new(!float.IsFinite(medium.WadeSpeedScale) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                $"{label}: wadeSpeedScale must be in (0, 1].")
            { EntityId = medium.Key, FieldPath = "WadeSpeedScale", ActualNumber = medium.WadeSpeedScale,
                MinimumNumber = 0, MaximumNumber = 1 }, errors, findings);
    }

    /// <summary>Returns the compiled domain when the body's geometry is valid and its id is unique.</summary>
    static MapWaterDomain? ValidateBody(MapWaterBodyDoc body, HashSet<string> media, HashSet<string> ids, List<string> errors,
        MapWaterWorkBudget budget, List<MapDocumentFinding>? findings)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
        {
            Add(new(MapDocumentRefusal.SchemaValueRequired, "every water body needs a non-empty id.")
            { FieldPath = body is null ? "Bodies" : "Id", EntityId = body?.Id, SubjectKind = "water body" }, errors, findings);
            return null;
        }
        string label = $"water body '{body.Id}'";
        bool unique = ids.Add(body.Id);
        if (!unique) Add(new(MapDocumentRefusal.IdentityDuplicate, $"{label} is duplicated.")
        { EntityId = body.Id, SubjectKind = "water body", FieldPath = "Id" }, errors, findings);
        if (!float.IsFinite(body.SurfaceY) || MathF.Abs(body.SurfaceY) > MaxMetres)
            Add(new(!float.IsFinite(body.SurfaceY) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                $"{label}: surfaceY must be finite within {MaxMetres} m.")
            { EntityId = body.Id, FieldPath = "SurfaceY", ActualNumber = body.SurfaceY,
                MinimumNumber = -MaxMetres, MaximumNumber = MaxMetres }, errors, findings);
        else
        {
            try { _ = MapExactValue.FromSingle(body.SurfaceY); }
            catch (MapExactOverflowException)
            {
                Add(new(MapDocumentRefusal.GeometryNotRepresentable, $"{label}: surfaceY is not representable.")
                { EntityId = body.Id, FieldPath = "SurfaceY", ActualNumber = body.SurfaceY }, errors, findings);
            }
        }
        if (body.Medium is null || !media.Contains(body.Medium))
            Add(new(body.Medium is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ReferenceMissing,
                $"{label}: unknown medium '{body.Medium}'.")
            { EntityId = body.Id, FieldPath = "Medium", ActualText = body.Medium, SubjectKind = "water medium" }, errors, findings);
        if (string.IsNullOrWhiteSpace(body.RenderMaterial))
            Add(new(MapDocumentRefusal.SchemaValueRequired, $"{label}: renderMaterial must be non-empty.")
            { EntityId = body.Id, FieldPath = "RenderMaterial" }, errors, findings);
        MapWaterDomain? domain = ValidateDomain(body.Domain, body.Id, label, errors, budget, findings);
        bool covers = ValidateCovers(body.Covers, body.Id, label, errors, findings);
        return unique && covers ? domain : null;
    }

    static void Add(MapDocumentFinding finding, List<string> errors, List<MapDocumentFinding>? findings)
    {
        errors.Add(finding.Message);
        findings?.Add(finding);
    }

    static MapWaterDomain? ValidateDomain(MapWaterDomainDoc domain, string bodyId, string label, List<string> errors,
        MapWaterWorkBudget budget, List<MapDocumentFinding>? findings)
    {
        if (domain?.Rects is null || domain.Rings is null)
        {
            Add(new(MapDocumentRefusal.SchemaValueRequired, $"{label}: domain is required.")
            { EntityId = bodyId, FieldPath = domain is null ? "Domain" : domain.Rects is null ? "Domain.Rects" : "Domain.Rings" }, errors, findings);
            return null;
        }
        if (!ValidFrame(domain.Frame, out var frameFinding))
        {
            Add(frameFinding! with { Message = $"{label}: invalid lattice frame.", EntityId = bodyId }, errors, findings);
            return null;
        }
        // Piece and vertex limits come before any per-piece or geometry work.
        var limits = new List<string>();
        var limitFindings = new List<MapDocumentFinding>();
        if (!MapWaterDomain.WithinLimits(domain, limits, limitFindings))
        {
            Add(limitFindings[0] with { Message = $"{label}: {limits[0]}.", EntityId = bodyId }, errors, findings);
            return null;
        }
        int before = errors.Count;
        if (domain.Rects.Count == 0 && domain.Rings.Count == 0)
            Add(new(MapDocumentRefusal.WaterDomainEmpty, $"{label}: domain is empty.")
            { EntityId = bodyId, FieldPath = "Domain", ActualCount = 0 }, errors, findings);
        bool far = false;
        MapDocumentFinding? farFinding = null;
        for (int i = 0; i < domain.Rects.Count; i++)
        {
            MapWaterRect rect = domain.Rects[i];
            if (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ)
                Add(new(MapDocumentRefusal.BoundsInvalid, $"{label}: rect {i} is empty.")
                { EntityId = bodyId, FieldPath = $"Domain.Rects[{i}]", Index = i, SubjectKind = "rect",
                    Address = MapLatticeAddress.Corner(rect.MinX, rect.MinZ) }, errors, findings);
            far |= FarAt(MapLatticeAddress.Corner(rect.MinX, rect.MinZ), $"Domain.Rects[{i}]", i) ||
                FarAt(MapLatticeAddress.Corner(rect.MaxX, rect.MaxZ), $"Domain.Rects[{i}]", i);
        }
        for (int i = 0; i < domain.Rings.Count; i++)
        {
            IReadOnlyList<MapLatticeAddress>? vertices = domain.Rings[i]?.Vertices;
            int? vertexCount = vertices?.Count;
            if (vertices is null || vertexCount < 3)
            {
                Add(new(vertices is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ValueOutsideRange,
                    $"{label}: ring {i} needs at least 3 vertices.")
                { EntityId = bodyId, FieldPath = domain.Rings[i] is null ? $"Domain.Rings[{i}]" : $"Domain.Rings[{i}].Vertices",
                    Index = i, ActualCount = vertexCount, MinimumInteger = vertices is null ? null : 3 }, errors, findings);
                if (vertices is null) continue;
            }
            for (int v = 0; v < vertices.Count; v++)
            {
                MapLatticeAddress? vertex = null;
                try
                {
                    vertex = vertices[v];
                    far |= FarAt(vertex.Value, $"Domain.Rings[{i}].Vertices[{v}]", i);
                }
                catch (MapDocumentException error)
                {
                    var finding = MapDocumentFinding.FromException(error)!;
                    Add(finding with { Message = $"{label}: ring {i} vertex {v} is not a valid lattice address.",
                        EntityId = bodyId, FieldPath = $"Domain.Rings[{i}].Vertices[{v}].{finding.FieldPath}", Index = i,
                        OtherIndex = v, Address = vertex ?? finding.Address }, errors, findings);
                }
            }
        }
        if (far) Add(farFinding! with { Message = $"{label}: domain exceeds {MaxMetres} m.", EntityId = bodyId }, errors, findings);
        if (errors.Count != before || budget.Exhausted) return null;

        var geometry = new List<string>();
        var geometryFindings = new List<MapDocumentFinding>();
        MapWaterDomain? compiled = MapWaterDomain.TryCompile(domain, null, geometry, budget, geometryFindings);
        foreach (var finding in geometryFindings)
            Add(finding with { Message = $"{label}: {finding.Message}.", EntityId = bodyId }, errors, findings);
        if (compiled is null) return null;
        var overlaps = new List<(int A, int B)>();
        try
        {
            overlaps.AddRange(compiled.OverlappingPieces(budget));
        }
        catch (MapDocumentException e)
        {
            Add(MapDocumentFinding.FromException(e)! with { Message = $"{label}: {e.Message}.", EntityId = bodyId }, errors, findings);
            return null;
        }
        overlaps.Sort();
        foreach ((int a, int b) in overlaps)
            Add(new(MapDocumentRefusal.WaterDomainPiecesOverlap, $"{label}: {compiled.PieceName(a)} and {compiled.PieceName(b)} overlap.")
            { EntityId = bodyId, FieldPath = "Domain", Index = a, OtherIndex = b }, errors, findings);
        return overlaps.Count == 0 ? compiled : null;

        bool FarAt(MapLatticeAddress address, string field, int index)
        {
            bool result = Far(domain.Frame, address, out var finding);
            if (result) farFinding ??= finding! with { FieldPath = field, Index = index, Address = address };
            return result;
        }
    }

    /// <summary>Refuses positive-area overlap between bodies that cover the same space. Pieces of every body in one
    /// space meet through one bounds sweep. Pairs within one body, already checked, and pairs of bodies already
    /// reported skip the exact test.</summary>
    static void ValidateSharedSpaces(List<(MapWaterBodyDoc Body, MapWaterDomain Domain)> compiled, List<string> errors,
        MapWaterWorkBudget budget, List<MapDocumentFinding>? findings)
    {
        var bySpace = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < compiled.Count; i++)
            foreach (MapRecordRef cover in compiled[i].Body.Covers)
            {
                if (!bySpace.TryGetValue(cover.Id, out List<int>? bodies)) bySpace[cover.Id] = bodies = new();
                bodies.Add(i);
            }
        foreach ((string space, List<int> bodies) in bySpace)
        {
            if (bodies.Count < 2) continue;
            if (budget.Exhausted) return;
            var pieces = new List<(MapWaterDomain Domain, int Piece)>();
            var owners = new List<int>();
            foreach (int body in bodies)
                for (int p = 0; p < compiled[body].Domain.Pieces.Count; p++)
                {
                    pieces.Add((compiled[body].Domain, p));
                    owners.Add(body);
                }
            var reported = new HashSet<(int, int)>();
            (int, int) BodyPair(int a, int b) => (Math.Min(owners[a], owners[b]), Math.Max(owners[a], owners[b]));
            try
            {
                foreach ((int a, int b) in MapWaterDomain.OverlappingPairs(pieces,
                    (a, b) => owners[a] != owners[b] && !reported.Contains(BodyPair(a, b)), budget))
                    reported.Add(BodyPair(a, b));
            }
            catch (MapDocumentException e)
            {
                Add(MapDocumentFinding.FromException(e)! with { Message = $"water bodies covering space '{space}': {e.Message}.",
                    SpaceId = space }, errors, findings);
                return;
            }
            var messages = new List<MapDocumentFinding>();
            foreach ((int first, int second) in reported)
            {
                string[] ids = { compiled[first].Body.Id, compiled[second].Body.Id };
                Array.Sort(ids, StringComparer.Ordinal);
                messages.Add(new(MapDocumentRefusal.WaterBodiesOverlap,
                    $"water bodies '{ids[0]}' and '{ids[1]}' overlap in space '{space}'.") { SourceIds = ids, SpaceId = space });
            }
            messages.Sort((a, b) => StringComparer.Ordinal.Compare(a.Message, b.Message));
            foreach (var finding in messages) Add(finding, errors, findings);
        }
    }

    static bool ValidateCovers(IReadOnlyList<MapRecordRef> covers, string bodyId, string label, List<string> errors,
        List<MapDocumentFinding>? findings)
    {
        int? coverCount = covers?.Count;
        if (covers is null || coverCount == 0)
        {
            Add(new(covers is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.WaterNoCoveredSpace,
                $"{label}: covers no space.") { EntityId = bodyId, FieldPath = "Covers", ActualCount = coverCount }, errors, findings);
            return false;
        }
        int before = errors.Count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < covers.Count; i++)
        {
            MapRecordRef cover = covers[i];
            if (cover is null || string.IsNullOrWhiteSpace(cover.Id) || string.IsNullOrWhiteSpace(cover.Anchor.SurfaceId))
                Add(new(MapDocumentRefusal.SchemaValueRequired, $"{label}: cover {i} needs a space id and an anchor surface.")
                { EntityId = bodyId, Index = i, RecordId = cover?.Id, Patch = cover?.Anchor,
                    FieldPath = cover is null ? $"Covers[{i}]" : string.IsNullOrWhiteSpace(cover.Id) ? $"Covers[{i}].Id" : $"Covers[{i}].Anchor.SurfaceId" }, errors, findings);
            else if (!seen.Add(cover.Id)) Add(new(MapDocumentRefusal.IdentityDuplicate, $"{label}: cover '{cover.Id}' is repeated.")
            { EntityId = bodyId, RecordId = cover.Id, Patch = cover.Anchor, Index = i, FieldPath = $"Covers[{i}]" }, errors, findings);
        }
        return errors.Count == before;
    }

    static bool ValidFrame(MapLatticeFrame? frame, out MapDocumentFinding? finding)
    {
        finding = null;
        if (frame is null)
        {
            finding = new(MapDocumentRefusal.SchemaValueRequired, "invalid lattice frame") { FieldPath = "Domain.Frame" };
            return false;
        }
        try { _ = frame.WorldXz(MapLatticeAddress.Corner(0, 0)); }
        catch (MapDocumentException error)
        {
            var inner = MapDocumentFinding.FromException(error)!;
            finding = inner with { FieldPath = "Domain.Frame." + inner.FieldPath };
            return false;
        }
        return true;
    }

    /// <summary>True when either world axis of the corner lies more than <see cref="MaxMetres"/> from the origin,
    /// including a corner too large to convert exactly.</summary>
    static bool Far(MapLatticeFrame frame, MapLatticeAddress address, out MapDocumentFinding? finding)
    {
        finding = null;
        try
        {
            MapExactXz world = frame.WorldXz(address);
            bool far = world.X.CompareTo(MaxExtent) > 0 || world.X.CompareTo(MinExtent) < 0 ||
                world.Z.CompareTo(MaxExtent) > 0 || world.Z.CompareTo(MinExtent) < 0;
            if (far) finding = new(MapDocumentRefusal.ValueOutsideRange, "domain exceeds world extent")
            { PositionX = world.X.ToDouble(), PositionZ = world.Z.ToDouble(), MinimumNumber = -MaxMetres, MaximumNumber = MaxMetres };
            return far;
        }
        catch (MapExactOverflowException)
        {
            finding = new(MapDocumentRefusal.GeometryNotRepresentable, "domain corner is not representable") { Address = address };
            return true;
        }
    }
}
