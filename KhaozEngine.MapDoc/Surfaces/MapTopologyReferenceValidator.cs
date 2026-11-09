using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Complete-view reference integrity. Geometry certification is a separate operation.</summary>
public static class MapTopologyReferenceValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<MapSurfaceRef> surfaces, IReadOnlyCollection<MapSurfacePatch> patches) =>
        Validate(surfaces, patches, null);

    /// <summary>Returns reference-integrity refusals and their known subjects in diagnostic order.
    /// The string validator is the prose projection of these findings.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateFindings(IReadOnlyList<MapSurfaceRef> surfaces,
        IReadOnlyCollection<MapSurfacePatch> patches)
    {
        var findings = new List<MapDocumentFinding>();
        Validate(surfaces, patches, findings);
        return findings.AsReadOnly();
    }

    internal static IReadOnlyList<string> Validate(IReadOnlyList<MapSurfaceRef> surfaces, IReadOnlyCollection<MapSurfacePatch> patches,
        List<MapDocumentFinding>? findings)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(patches);
        var context = new Context(surfaces, patches, findings, null);
        context.Validate();
        return context.Errors;
    }

    internal static IReadOnlyList<MapSpaceDiagnostic> ValidateDiagnostics(IReadOnlyList<MapSurfaceRef> surfaces,
        IReadOnlyCollection<MapSurfacePatch> patches)
    {
        var diagnostics = new List<MapSpaceDiagnostic>();
        var context = new Context(surfaces, patches, null, diagnostics);
        context.Validate();
        return diagnostics.AsReadOnly();
    }

    sealed class Context
    {
        readonly Dictionary<string, MapSurfaceRef> _surfaces = new(StringComparer.Ordinal);
        readonly Dictionary<MapPatchKey, MapSurfacePatch> _patches = new();
        readonly Dictionary<string, (MapPatchKey Anchor, MapTopologyRecord Record)> _records = new(StringComparer.Ordinal);
        readonly HashSet<(string Record, MapSide Side)> _sides = new();
        readonly Dictionary<(string Space, MapRecordRef Reference, MapSide Side), int> _portalMembership = new();
        readonly Dictionary<(string Space, MapRecordRef Reference), int> _linkMembership = new();
        readonly List<MapDocumentFinding>? _findings;
        readonly List<MapSpaceDiagnostic>? _diagnostics;
        readonly HashSet<string>? _sortOwners;
        internal List<string> Errors { get; } = new();

        void Add(MapDocumentFinding finding, params string?[] subjects)
        {
            Errors.Add(finding.Message);
            _findings?.Add(finding);
            if (_diagnostics is not null)
                _diagnostics.Add(new(SortOwner(subjects), finding.Message, finding));
        }

        string SortOwner(params string?[] subjects) => subjects.OfType<string>().Where(_sortOwners!.Contains)
            .Order(StringComparer.Ordinal).FirstOrDefault() ?? "";

        internal Context(IReadOnlyList<MapSurfaceRef> surfaces, IReadOnlyCollection<MapSurfacePatch> patches, List<MapDocumentFinding>? findings,
            List<MapSpaceDiagnostic>? diagnostics)
        {
            _findings = findings;
            _diagnostics = diagnostics;
            _sortOwners = diagnostics is null ? null : patches.SelectMany(p => p.Records).Where(r => r is not null)
                .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (MapSurfaceRef surface in surfaces)
            {
                if (surface is null || string.IsNullOrWhiteSpace(surface.Id))
                {
                    Add(new(MapDocumentRefusal.SchemaValueRequired, "invalid surface id")
                    { FieldPath = surface is null ? "surface" : nameof(surface.Id), SurfaceId = surface?.Id }, []);
                    continue;
                }
                if (!_surfaces.TryAdd(surface.Id, surface))
                    Add(new(MapDocumentRefusal.IdentityDuplicate, $"duplicate id '{surface.Id}' in surfaces")
                    { SurfaceId = surface.Id, EntityId = surface.Id, SubjectKind = "surface", FieldPath = "surfaces" }, surface.Id);
                if (!Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy) || surface.Frame is null)
                    Add(new(!Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy)
                        ? MapDocumentRefusal.SchemaEnumUnsupported : MapDocumentRefusal.SchemaValueRequired,
                        $"invalid surface role/frame '{surface.Id}'")
                    {
                        SurfaceId = surface.Id,
                        FieldPath = !Enum.IsDefined(surface.Role) ? nameof(surface.Role)
                            : !Enum.IsDefined(surface.PresencePolicy) ? nameof(surface.PresencePolicy) : nameof(surface.Frame),
                        EnumType = !Enum.IsDefined(surface.Role) ? typeof(MapSurfaceRole)
                            : !Enum.IsDefined(surface.PresencePolicy) ? typeof(MapPresencePolicy) : null,
                        ActualInteger = !Enum.IsDefined(surface.Role) ? (long)surface.Role
                            : !Enum.IsDefined(surface.PresencePolicy) ? (long)surface.PresencePolicy : null,
                    }, surface.Id);
                else
                    try { _ = surface.Frame.WorldXz(MapLatticeAddress.Corner(0, 0)); }
                    catch (MapDocumentException ex)
                    {
                        string message = $"invalid surface frame '{surface.Id}'";
                        if (MapDocumentFinding.FromException(ex) is { } finding) Add(finding with { Message = message, SurfaceId = surface.Id }, surface.Id);
                        else { Errors.Add(message); _diagnostics?.Add(new(SortOwner(surface.Id), message, null)); }
                    }
            }
            foreach (MapSurfacePatch patch in patches.OrderBy(p => p.Key))
            {
                var localFindings = _findings is null && _diagnostics is null ? null : new List<MapDocumentFinding>();
                IReadOnlyList<string> local = patch.ValidateLocal(localFindings);
                if (local.Count != 0)
                {
                    Errors.AddRange(local);
                    if (localFindings is not null)
                    {
                        _findings?.AddRange(localFindings);
                        // Patch-local diagnostics quote no record owners.
                        _diagnostics?.AddRange(localFindings.Select(f => new MapSpaceDiagnostic("", f.Message, f)));
                    }
                    continue;
                }
                if (!_surfaces.ContainsKey(patch.Key.SurfaceId))
                    Add(new(MapDocumentRefusal.ReferenceMissing, $"missing surface '{patch.Key.SurfaceId}'")
                    { SurfaceId = patch.Key.SurfaceId, Patch = patch.Key, FieldPath = "Key.SurfaceId" }, patch.Key.SurfaceId);
                if (!_patches.TryAdd(patch.Key, patch))
                    Add(new(MapDocumentRefusal.IdentityDuplicate, "duplicate patch key")
                    { Patch = patch.Key, SubjectKind = "patch", FieldPath = "patches" }, []);
                foreach (MapTopologyRecord record in patch.Records)
                {
                    if (record is null || string.IsNullOrWhiteSpace(record.Id))
                    {
                        Add(new(MapDocumentRefusal.SchemaValueRequired, "invalid record id")
                        { Patch = patch.Key, RecordId = record?.Id, FieldPath = record is null ? "record" : nameof(record.Id) }, []);
                        continue;
                    }
                    if (!_records.TryAdd(record.Id, (patch.Key, record)))
                        Add(new(MapDocumentRefusal.IdentityDuplicate, $"duplicate id '{record.Id}'")
                        { Patch = patch.Key, RecordId = record.Id, SubjectKind = "record", FieldPath = "Records" }, record.Id);
                }
            }
            foreach (MapSpaceDoc space in _records.Values.Select(r => r.Record).OfType<MapSpaceDoc>())
            {
                foreach (MapBoundaryRef boundary in space.Portals)
                {
                    var key = (space.Id, boundary.Record, boundary.Side);
                    _portalMembership[key] = _portalMembership.GetValueOrDefault(key) + 1;
                }
                foreach (MapRecordRef link in space.Links)
                {
                    var key = (space.Id, link);
                    _linkMembership[key] = _linkMembership.GetValueOrDefault(key) + 1;
                }
            }
        }

        internal void Validate()
        {
            foreach (MapSurfaceRef surface in _surfaces.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
                if (surface.IndoorSpan is { } span)
                    _ = Find<MapSpaceDoc>(span.ParentSpace, $"indoor span '{span.Id}' on surface '{surface.Id}'", surface.Id, span.Id);
            foreach (var row in _records.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value))
            {
                MapTopologyRecord record = row.Record;
                switch (record)
                {
                    case MapSurfaceSeam seam:
                        Edge(seam.First, seam.Id); Edge(seam.Second, seam.Id);
                        foreach (var pair in seam.Pairs) { Vertex(pair.First, seam.Id); Vertex(pair.Second, seam.Id); }
                        break;
                    case MapBoundaryChain chain: Chain(chain); break;
                    case MapWallStrip strip:
                        _ = Find<MapBoundaryChain>(strip.LowerChain, strip.Id); _ = Find<MapBoundaryChain>(strip.UpperChain, strip.Id);
                        if (!Enum.IsDefined(strip.Facing))
                            Add(new(MapDocumentRefusal.SchemaEnumUnsupported, $"invalid strip facing '{strip.Id}'")
                            { RecordId = strip.Id, FieldPath = nameof(strip.Facing), EnumType = typeof(MapStripFacing), ActualInteger = (long)strip.Facing }, strip.Id);
                        break;
                    case MapCavePortal portal: Portal(portal, row.Anchor); break;
                    case MapHorizontalOpening opening: Slots(opening.Patch, opening.SlotCells, opening.Id, requireAbsent: true); break;
                    case MapVerticalLink link: Link(link, row.Anchor); break;
                    case MapSpaceDoc space: Space(space); break;
                    case MapPrismDomain prism:
                        var room = Find<MapSpaceDoc>(prism.Space, prism.Id);
                        _ = Find<MapSpaceDoc>(prism.Parent, prism.Id);
                        if (prism.PayloadVersion != 1 || room?.Kind != MapSpaceKind.Room || room.Parent != prism.Parent)
                            Add(new(prism.PayloadVersion != 1 ? MapDocumentRefusal.SchemaPayloadVersionUnsupported
                                : room is null ? MapDocumentRefusal.ReferenceMissing
                                : room.Kind != MapSpaceKind.Room ? MapDocumentRefusal.ReferenceKindMismatch : MapDocumentRefusal.SpacePrismParentMismatch,
                                $"invalid prism parent/room '{prism.Id}'")
                            {
                                RecordId = prism.Id,
                                SpaceId = prism.Space?.Id,
                                RelatedRecordId = prism.Parent?.Id,
                                FieldPath = prism.PayloadVersion != 1 ? nameof(prism.PayloadVersion)
                                    : room is null ? nameof(prism.Space) : room.Kind != MapSpaceKind.Room ? "Space.Kind" : "Space.Parent",
                                ActualVersion = prism.PayloadVersion != 1 ? prism.PayloadVersion : null,
                                SupportedVersions = prism.PayloadVersion != 1 ? new[] { 1 } : null,
                                EnumType = prism.PayloadVersion == 1 && room is not null && room.Kind != MapSpaceKind.Room ? typeof(MapSpaceKind) : null,
                                ActualInteger = prism.PayloadVersion == 1 && room is not null && room.Kind != MapSpaceKind.Room ? (long)room.Kind : null,
                            }, prism.Id);
                        break;
                    case MapSpaceFootprint footprint: Footprint(footprint); break;
                    default:
                        Add(new(MapDocumentRefusal.OperationKindUnsupported, $"unknown record type '{record.Id}'")
                        { RecordId = record.Id, RecordType = record.GetType(), Patch = row.Anchor }, record.Id);
                        break;
                }
            }
            foreach (var room in _records.Values.Select(r => r.Record).OfType<MapSpaceDoc>().Where(s => s.Kind == MapSpaceKind.Room && s.AliasOf is null))
            {
                int count = _records.Values.Count(r => r.Record is MapPrismDomain p && p.Space.Id == room.Id);
                if (count != 1)
                    Add(new(MapDocumentRefusal.SpaceRoomOccupancyInvalid, $"room requires one prism '{room.Id}'")
                    { SpaceId = room.Id, ActualCount = count, ExpectedCount = 1 }, room.Id);
            }
            ValidateParentCycles();
        }

        void ValidateParentCycles()
        {
            // One call-owned traversal state prevents a long parent chain being walked once per space.
            var state = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (MapSpaceDoc space in _records.Values.Select(r => r.Record).OfType<MapSpaceDoc>())
            {
                var path = new List<string>();
                MapSpaceDoc current = space;
                while (true)
                {
                    byte seen = state.GetValueOrDefault(current.Id);
                    if (seen != 0)
                    {
                        if (seen == 1)
                            Add(new(MapDocumentRefusal.SpaceParentCycle, $"parent cycle at '{current.Id}'") { SpaceId = current.Id }, current.Id);
                        break;
                    }
                    state[current.Id] = 1;
                    path.Add(current.Id);
                    if (current.Parent is not { } parent || !_records.TryGetValue(parent.Id, out var row) ||
                        row.Anchor != parent.Anchor || row.Record is not MapSpaceDoc next) break;
                    current = next;
                }
                foreach (string id in path) state[id] = 2;
            }
        }

        T? Find<T>(MapRecordRef? reference, string source, string? surfaceId = null, string? spanId = null) where T : MapTopologyRecord
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.Id))
            {
                Add(new(MapDocumentRefusal.SchemaValueRequired, $"missing reference in '{source}'")
                {
                    RecordId = surfaceId is null ? source : null,
                    ActualText = reference?.Id,
                    Patch = reference?.Anchor,
                    SurfaceId = surfaceId,
                    FieldPath = reference is null ? "reference" : "reference.Id",
                }, surfaceId ?? source, spanId);
                return null;
            }
            if (!_records.TryGetValue(reference.Id, out var row))
            {
                Add(new(MapDocumentRefusal.ReferenceMissing, $"missing record '{reference.Id}' in '{source}'")
                { RecordId = surfaceId is null ? source : null, SurfaceId = surfaceId, RelatedRecordId = reference.Id, Patch = reference.Anchor }, reference.Id, surfaceId ?? source, spanId);
                return null;
            }
            if (row.Anchor != reference.Anchor)
            {
                Add(new(MapDocumentRefusal.AuthoredOwnershipMismatch, $"wrong anchor for '{reference.Id}' in '{source}'")
                { RecordId = surfaceId is null ? source : null, SurfaceId = surfaceId, RelatedRecordId = reference.Id, Patch = reference.Anchor, ActualAnchor = row.Anchor }, reference.Id, surfaceId ?? source, spanId);
                return null;
            }
            if (row.Record is not T typed)
            {
                Add(new(MapDocumentRefusal.ReferenceKindMismatch, $"wrong type for '{reference.Id}' in '{source}'")
                { RecordId = surfaceId is null ? source : null, SurfaceId = surfaceId, RelatedRecordId = reference.Id, Patch = reference.Anchor, RecordType = row.Record.GetType() }, reference.Id, surfaceId ?? source, spanId);
                return null;
            }
            return typed;
        }
        void Vertex(MapLatticeVertex vertex, string source)
        {
            if (string.IsNullOrWhiteSpace(vertex.SurfaceId) || !_surfaces.ContainsKey(vertex.SurfaceId))
                Add(new(string.IsNullOrWhiteSpace(vertex.SurfaceId) ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ReferenceMissing,
                    $"missing vertex surface in '{source}'")
                { RecordId = source, SurfaceId = vertex.SurfaceId, FieldPath = "Vertex.SurfaceId" }, source);
            try { vertex.Address.RequireValid(); }
            catch (MapDocumentException ex)
            {
                string message = $"invalid vertex denominator in '{source}'";
                if (MapDocumentFinding.FromException(ex) is { } finding)
                    Add(finding with { Message = message, RecordId = source, SurfaceId = vertex.SurfaceId }, source);
                else { Errors.Add(message); _diagnostics?.Add(new(SortOwner(source), message, null)); }
            }
        }
        void Edge(MapSurfaceEdgeRef edge, string source)
        {
            if (!_patches.ContainsKey(edge.Patch))
                Add(new(MapDocumentRefusal.ReferenceMissing, $"missing edge patch in '{source}'") { RecordId = source, Patch = edge.Patch }, source);
            Vertex(edge.From, source); Vertex(edge.To, source);
            if (edge.From.SurfaceId != edge.Patch.SurfaceId || edge.To.SurfaceId != edge.Patch.SurfaceId)
                Add(new(MapDocumentRefusal.AuthoredOwnershipMismatch, $"edge surface mismatch in '{source}'")
                { RecordId = source, Patch = edge.Patch, SurfaceId = edge.From.SurfaceId != edge.Patch.SurfaceId ? edge.From.SurfaceId : edge.To.SurfaceId }, source);
        }
        void Chain(MapBoundaryChain chain)
        {
            bool supportedKind = Enum.IsDefined(chain.Kind);
            int? vertexCount = supportedKind ? chain.Vertices.Count : null;
            if (!supportedKind || vertexCount is < 2 or > 4097)
                Add(new(!supportedKind ? MapDocumentRefusal.SchemaEnumUnsupported : MapDocumentRefusal.ValueOutsideRange,
                    $"invalid chain kind/vertices '{chain.Id}'")
                {
                    RecordId = chain.Id,
                    FieldPath = !supportedKind ? nameof(chain.Kind) : nameof(chain.Vertices),
                    EnumType = !supportedKind ? typeof(MapChainKind) : null,
                    ActualInteger = !supportedKind ? (long)chain.Kind : null,
                    ActualCount = vertexCount,
                    MinimumInteger = supportedKind ? 2 : null,
                    MaximumInteger = supportedKind ? 4097 : null,
                }, chain.Id);
            if (chain.Kind == MapChainKind.SurfaceEdge && (chain.SourcePatch is null || !_patches.ContainsKey(chain.SourcePatch.Value)))
                Add(new(chain.SourcePatch is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ReferenceMissing,
                    $"missing chain source patch '{chain.Id}'")
                { RecordId = chain.Id, Patch = chain.SourcePatch, FieldPath = nameof(chain.SourcePatch) }, chain.Id);
            if (chain.Kind == MapChainKind.Authored && chain.SourcePatch is not null)
                Add(new(MapDocumentRefusal.AuthoredOwnershipMismatch, $"authored chain has source patch '{chain.Id}'")
                { RecordId = chain.Id, Patch = chain.SourcePatch, FieldPath = nameof(chain.SourcePatch) }, chain.Id);
            int index = 0;
            foreach (MapChainVertex v in chain.Vertices)
            {
                Vertex(v.Vertex, chain.Id);
                if ((chain.Kind == MapChainKind.Authored) != v.HeightUnits.HasValue)
                    Add(new(MapDocumentRefusal.SpaceChainHeightKindMismatch, $"chain height kind mismatch '{chain.Id}'")
                    { RecordId = chain.Id, Index = index, FieldPath = nameof(v.HeightUnits), ActualInteger = v.HeightUnits }, chain.Id);
                if (chain.Kind == MapChainKind.SurfaceEdge && chain.SourcePatch is { } patch && v.Vertex.SurfaceId != patch.SurfaceId)
                    Add(new(MapDocumentRefusal.AuthoredValueMismatch, $"chain source surface mismatch '{chain.Id}'")
                    { RecordId = chain.Id, Patch = patch, SurfaceId = v.Vertex.SurfaceId, Index = index, FieldPath = "Vertex.SurfaceId" }, chain.Id);
                index++;
            }
        }
        void Portal(MapCavePortal portal, MapPatchKey anchor)
        {
            MapSpaceDoc? from = Find<MapSpaceDoc>(portal.FromSpace, portal.Id), to = Find<MapSpaceDoc>(portal.ToSpace, portal.Id);
            _ = Find<MapBoundaryChain>(portal.BandBottom, portal.Id);
            if (portal.BandTop is not null) _ = Find<MapBoundaryChain>(portal.BandTop, portal.Id);
            else if (from is not null && to is not null && (from.Kind != MapSpaceKind.Exterior || to.Kind != MapSpaceKind.Exterior))
                Add(new(MapDocumentRefusal.SpacePortalOpenTopInvalid, $"open top portal requires Exterior spaces '{portal.Id}'")
                {
                    RecordId = portal.Id,
                    SpaceId = from.Kind != MapSpaceKind.Exterior ? from.Id : to.Id,
                    EnumType = typeof(MapSpaceKind),
                    ActualInteger = (long)(from.Kind != MapSpaceKind.Exterior ? from.Kind : to.Kind),
                    FieldPath = nameof(portal.BandTop),
                }, portal.Id);
            int intervalCount = portal.Interval.Count;
            if (intervalCount < 2)
                Add(new(MapDocumentRefusal.GeometryVertexSequenceInvalid, $"invalid portal interval '{portal.Id}'")
                { RecordId = portal.Id, FieldPath = nameof(portal.Interval), ActualCount = intervalCount, MinimumInteger = 2 }, portal.Id);
            foreach (MapLatticeVertex v in portal.Interval) Vertex(v, portal.Id);
            var reference = new MapRecordRef(portal.Id, anchor);
            bool semanticRoom = from?.Kind == MapSpaceKind.Room || to?.Kind == MapSpaceKind.Room;
            if (!semanticRoom && from is not null && _portalMembership.GetValueOrDefault((from.Id, reference, MapSide.Front)) != 1)
                Add(new(MapDocumentRefusal.SpacePortalMembershipMismatch, $"portal list mismatch '{portal.Id}'")
                { RecordId = portal.Id, SpaceId = from.Id, FieldPath = nameof(portal.FromSpace), ActualCount = _portalMembership.GetValueOrDefault((from.Id, reference, MapSide.Front)), ExpectedCount = 1 }, portal.Id);
            if (!semanticRoom && to is not null && _portalMembership.GetValueOrDefault((to.Id, reference, MapSide.Back)) != 1)
                Add(new(MapDocumentRefusal.SpacePortalMembershipMismatch, $"portal list mismatch '{portal.Id}'")
                { RecordId = portal.Id, SpaceId = to.Id, FieldPath = nameof(portal.ToSpace), ActualCount = _portalMembership.GetValueOrDefault((to.Id, reference, MapSide.Back)), ExpectedCount = 1 }, portal.Id);
        }
        void Link(MapVerticalLink link, MapPatchKey anchor)
        {
            MapSpaceDoc? upper = Find<MapSpaceDoc>(link.UpperSpace, link.Id), lower = Find<MapSpaceDoc>(link.LowerSpace, link.Id);
            RefSet<MapHorizontalOpening>(link.Openings, link.Id); RefSet<MapCavePortal>(link.Portals, link.Id);
            RefSet<MapTopologyRecord>(link.GeometryOwners, link.Id);
            foreach (MapRecordRef owner in link.GeometryOwners)
                if (_records.TryGetValue(owner.Id, out var row) && row.Record is MapSpaceDoc or MapSpaceFootprint or MapVerticalLink)
                    Add(new(MapDocumentRefusal.AuthoredReferenceKindInvalid, $"wrong geometry owner type '{owner.Id}' in '{link.Id}'")
                    { RecordId = link.Id, RelatedRecordId = owner.Id, Patch = owner.Anchor, RecordType = row.Record.GetType(), FieldPath = nameof(link.GeometryOwners) }, owner.Id, link.Id);
            var reference = new MapRecordRef(link.Id, anchor);
            if (upper is not null && _linkMembership.GetValueOrDefault((upper.Id, reference)) != 1)
                Add(new(MapDocumentRefusal.SpaceLinkMembershipMismatch, $"link list mismatch '{link.Id}'")
                { RecordId = link.Id, SpaceId = upper.Id, FieldPath = nameof(link.UpperSpace), ActualCount = _linkMembership.GetValueOrDefault((upper.Id, reference)), ExpectedCount = 1 }, link.Id);
            if (lower is not null && _linkMembership.GetValueOrDefault((lower.Id, reference)) != 1)
                Add(new(MapDocumentRefusal.SpaceLinkMembershipMismatch, $"link list mismatch '{link.Id}'")
                { RecordId = link.Id, SpaceId = lower.Id, FieldPath = nameof(link.LowerSpace), ActualCount = _linkMembership.GetValueOrDefault((lower.Id, reference)), ExpectedCount = 1 }, link.Id);
        }
        void RefSet<T>(IReadOnlyList<MapRecordRef> refs, string source) where T : MapTopologyRecord
        {
            var seen = new HashSet<MapRecordRef>();
            int index = 0;
            foreach (MapRecordRef reference in refs)
            {
                if (!seen.Add(reference))
                    Add(new(MapDocumentRefusal.IdentityDuplicate, $"duplicate reference '{reference.Id}' in '{source}'")
                    { RecordId = source, RelatedRecordId = reference.Id, Patch = reference.Anchor, Index = index, SubjectKind = "record reference" }, reference.Id, source);
                _ = Find<T>(reference, source);
                index++;
            }
        }
        void Space(MapSpaceDoc space)
        {
            if (!Enum.IsDefined(space.Kind))
                Add(new(MapDocumentRefusal.SchemaEnumUnsupported, $"invalid space kind '{space.Id}'")
                { RecordId = space.Id, SpaceId = space.Id, FieldPath = nameof(space.Kind), EnumType = typeof(MapSpaceKind), ActualInteger = (long)space.Kind }, space.Id);
            if (space.Parent is not null) _ = Find<MapSpaceDoc>(space.Parent, space.Id);
            if (space.AliasOf is not null)
            {
                MapSpaceDoc? target = Find<MapSpaceDoc>(space.AliasOf, space.Id);
                if (target is not null && (target.AliasOf is not null || target.Parent != space.Parent || target.Kind != space.Kind))
                    Add(new(MapDocumentRefusal.SpaceAliasPeerInvalid, $"alias must target an alias-free peer '{space.Id}'")
                    {
                        RecordId = space.Id,
                        SpaceId = space.Id,
                        RelatedRecordId = target.Id,
                        FieldPath = target.AliasOf is not null ? "AliasOf.AliasOf" : target.Parent != space.Parent ? "AliasOf.Parent" : "AliasOf.Kind",
                    }, space.Id);
            }
            foreach (MapBoundaryRef wall in space.Walls)
            {
                _ = Find<MapWallStrip>(wall.Record, space.Id); Side(wall, space.Id);
            }
            foreach (MapBoundaryRef boundary in space.Portals)
            {
                MapCavePortal? portal = Find<MapCavePortal>(boundary.Record, space.Id); Side(boundary, space.Id);
                if (portal is not null)
                {
                    MapRecordRef endpoint = boundary.Side == MapSide.Front ? portal.FromSpace : portal.ToSpace;
                    var endpointSpace = Find<MapSpaceDoc>(endpoint, portal.Id);
                    if (endpointSpace?.Id != space.Id)
                        Add(new(MapDocumentRefusal.SpacePortalMembershipMismatch, $"portal list mismatch '{space.Id}'")
                        { RecordId = space.Id, SpaceId = space.Id, RelatedRecordId = portal.Id, EntityId = endpointSpace?.Id, FieldPath = nameof(space.Portals) }, space.Id);
                }
            }
            RefSet<MapVerticalLink>(space.Links, space.Id);
            foreach (MapRecordRef reference in space.Links)
                if (_records.TryGetValue(reference.Id, out var row) && row.Record is MapVerticalLink link &&
                    link.UpperSpace.Id != space.Id && link.LowerSpace.Id != space.Id)
                    Add(new(MapDocumentRefusal.SpaceLinkMembershipMismatch, $"link list mismatch '{space.Id}'")
                    { RecordId = space.Id, SpaceId = space.Id, RelatedRecordId = link.Id, FieldPath = nameof(space.Links) }, space.Id);
        }
        void Side(MapBoundaryRef boundary, string source)
        {
            if (!Enum.IsDefined(boundary.Side))
                Add(new(MapDocumentRefusal.SchemaEnumUnsupported, $"invalid boundary side in '{source}'")
                { RecordId = source, RelatedRecordId = boundary.Record.Id, FieldPath = nameof(boundary.Side), EnumType = typeof(MapSide), ActualInteger = (long)boundary.Side }, source);
            if (!_sides.Add((boundary.Record.Id, boundary.Side)))
                Add(new(MapDocumentRefusal.IdentityDuplicate, $"duplicate boundary side '{boundary.Record.Id}'")
                { RecordId = source, RelatedRecordId = boundary.Record.Id, FieldPath = nameof(boundary.Side), EnumType = typeof(MapSide), ActualInteger = (long)boundary.Side, SubjectKind = "boundary side" }, boundary.Record.Id);
        }
        void Footprint(MapSpaceFootprint footprint)
        {
            MapSpaceDoc? space = Find<MapSpaceDoc>(footprint.Space, footprint.Id);
            if (space?.Kind == MapSpaceKind.Room)
                Add(new(MapDocumentRefusal.SpaceRoomOccupancyInvalid, $"room occupancy requires its prism, not a lattice footprint '{footprint.Id}'")
                { RecordId = footprint.Id, SpaceId = space.Id, FieldPath = nameof(footprint.Space) }, footprint.Id);
            Slots(footprint.Lattice, footprint.SlotCells, footprint.Id, requireAbsent: false);
            Bound(footprint.Lower, lower: true, space, footprint.Id);
            Bound(footprint.Upper, lower: false, space, footprint.Id);
            _surfaces.TryGetValue(footprint.Lower.SurfaceId ?? "", out MapSurfaceRef? lowerSurface);
            if (space is not null && MapLegacyExteriorRecipe.CheckFinding(footprint, space, lowerSurface) is { } failure)
                Add(failure with { Message = $"{failure.Message}: footprint '{footprint.Id}'" }, footprint.Id);
        }
        void Bound(MapBoundRef bound, bool lower, MapSpaceDoc? space, string source)
        {
            string role = lower ? "Lower" : "Upper";
            if (bound.Kind == MapBoundKind.OpenTop)
            {
                if (lower || space?.Kind != MapSpaceKind.Exterior || bound.SurfaceId is not null || bound.Opening is not null)
                    Add(new(MapDocumentRefusal.SpaceOpenTopInvalid, $"invalid open top bound '{source}'")
                    {
                        RecordId = source,
                        SpaceId = space?.Id,
                        SurfaceId = bound.SurfaceId,
                        RelatedRecordId = bound.Opening?.Id,
                        FieldPath = lower ? "Lower.Kind" : space?.Kind != MapSpaceKind.Exterior ? "Space.Kind"
                            : bound.SurfaceId is not null ? "Upper.SurfaceId" : "Upper.Opening",
                    }, source);
                return;
            }
            if (bound.Kind == MapBoundKind.HorizontalOpening)
            {
                _ = Find<MapHorizontalOpening>(bound.Opening, source);
                if (bound.SurfaceId is not null)
                    Add(new(MapDocumentRefusal.AuthoredValueMismatch, $"invalid opening bound surface '{source}'")
                    { RecordId = source, SpaceId = space?.Id, SurfaceId = bound.SurfaceId, RelatedRecordId = bound.Opening?.Id, FieldPath = $"{role}.SurfaceId" }, source);
                return;
            }
            MapSurfaceRole expected = lower ? MapSurfaceRole.SupportFloor : MapSurfaceRole.Ceiling;
            bool kind = lower ? bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1 : bound.Kind == MapBoundKind.Ceiling;
            MapSurfaceRef? surface = null;
            if (!kind || bound.Opening is not null || bound.SurfaceId is null ||
                !_surfaces.TryGetValue(bound.SurfaceId, out surface) || surface.Role != expected)
                Add(new(!kind ? !Enum.IsDefined(bound.Kind) ? MapDocumentRefusal.SchemaEnumUnsupported : MapDocumentRefusal.SpaceBoundRoleMismatch
                    : bound.Opening is not null ? MapDocumentRefusal.AuthoredValueMismatch
                    : bound.SurfaceId is null ? MapDocumentRefusal.SchemaValueRequired
                    : surface is null ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.SpaceBoundRoleMismatch,
                    $"bound surface role mismatch '{source}'")
                {
                    RecordId = source,
                    SpaceId = space?.Id,
                    SurfaceId = bound.SurfaceId,
                    RelatedRecordId = bound.Opening?.Id,
                    FieldPath = !kind ? $"{role}.Kind" : bound.Opening is not null ? $"{role}.Opening"
                        : surface is null ? $"{role}.SurfaceId" : $"{role}.Surface.Role",
                    EnumType = !kind ? typeof(MapBoundKind) : surface is not null ? typeof(MapSurfaceRole) : null,
                    ActualInteger = !kind ? (long)bound.Kind : surface is not null ? (long)surface.Role : null,
                }, source);
        }
        void Slots(MapPatchKey key, IReadOnlyList<int> cells, string source, bool requireAbsent)
        {
            if (!_patches.TryGetValue(key, out MapSurfacePatch? patch))
            {
                Add(new(MapDocumentRefusal.ReferenceMissing, $"missing lattice patch '{source}'") { RecordId = source, Patch = key }, source);
                return;
            }
            int cellCount = cells.Count;
            if (cellCount is < 1 or > 4096)
                Add(new(MapDocumentRefusal.ValueOutsideRange, $"invalid slot cell count '{source}'")
                { RecordId = source, Patch = key, FieldPath = "SlotCells.Count", ActualCount = cellCount, MinimumInteger = 1, MaximumInteger = 4096 }, source);
            int previous = -1;
            int index = 0;
            foreach (int cell in cells)
            {
                if (cell <= previous)
                    Add(new(index == 0 ? MapDocumentRefusal.ValueOutsideRange
                        : cell == previous ? MapDocumentRefusal.IdentityDuplicate : MapDocumentRefusal.OrderInvalid,
                        $"duplicate or unordered slot cell '{source}'")
                    {
                        RecordId = source,
                        Patch = key,
                        FieldPath = "SlotCells",
                        Index = index,
                        ActualInteger = cell,
                        PreviousInteger = index == 0 ? null : previous,
                        MinimumInteger = index == 0 ? 0 : null
                    }, source);
                previous = cell;
                int x = cell % 64 - patch.CellMinX, z = cell / 64 - patch.CellMinZ;
                if (cell is < 0 or >= 4096 || x < 0 || x >= patch.Width || z < 0 || z >= patch.Depth)
                    Add(new(cell is < 0 or >= 4096 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.SurfaceAddressOutOfPatch,
                        $"slot cell outside patch '{source}'")
                    {
                        RecordId = source,
                        Patch = key,
                        FieldPath = "SlotCells",
                        Index = index,
                        ActualInteger = cell,
                        MinimumInteger = cell is < 0 or >= 4096 ? 0 : null,
                        MaximumInteger = cell is < 0 or >= 4096 ? 4095 : null,
                        CellX = x,
                        CellZ = z,
                        Width = patch.Width,
                        Depth = patch.Depth,
                    }, source);
                else if (requireAbsent && patch.IsPresent(x, z))
                    Add(new(MapDocumentRefusal.SpaceOpeningCellsInvalid, $"opening over present cell '{source}'")
                    { RecordId = source, Patch = key, FieldPath = "SlotCells", Index = index, ActualInteger = cell, CellX = x, CellZ = z }, source);
                index++;
            }
        }
    }
}
