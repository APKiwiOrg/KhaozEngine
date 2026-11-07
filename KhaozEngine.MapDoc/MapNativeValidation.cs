using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc;

/// <summary>Native metadata checks shared by loading, editing and saving.</summary>
internal static class MapNativeValidation
{
    internal static void Validate(MapDocument doc, List<string> errors, List<MapDocumentFinding>? findings = null)
    {
        ValidateNumericIds(doc, errors, findings: findings);
        ValidateSupport(doc, errors, findings);
        if (doc.ResolverIdentity is { } identity && (identity.PayloadVersion <= 0 || identity.ResolverVersion <= 0))
            Add(errors, findings, identity.PayloadVersion <= 0
                ? Version("resolverIdentity payloadVersion and resolverVersion must be positive.", Field(Field("$", "resolverIdentity"), "payloadVersion"), identity.PayloadVersion, positiveOnly: true)
                : MapDocumentFinding.ResolverContract(doc, "resolverIdentity payloadVersion and resolverVersion must be positive.", Field(Field("$", "resolverIdentity"), "resolverVersion"), "document"));
        if (doc.NativeAssets is null)
            Add(errors, findings, new(MapDocumentRefusal.SchemaValueRequired, "nativeAssets must not be null.") { FieldPath = Field("$", "nativeAssets") });
        else
        {
            for (int i = 0; i < doc.NativeAssets.Count; i++)
            {
                MapAssetRef asset = doc.NativeAssets[i];
                string path = $"{Field("$", "nativeAssets")}[{i}]";
                if (asset is null)
                {
                    Add(errors, findings, new(MapDocumentRefusal.SchemaValueRequired, "nativeAssets must not contain null entries.") { FieldPath = path, Index = i });
                    continue;
                }
                if (string.IsNullOrEmpty(asset.Id) || string.IsNullOrEmpty(asset.Path) || string.IsNullOrEmpty(asset.Sha256))
                    Add(errors, findings, new(MapDocumentRefusal.SchemaValueRequired, "native asset id, path and sha256 must be non-empty.")
                    { ResourceId = asset.Id, Index = i, FieldPath = Field(path, string.IsNullOrEmpty(asset.Id) ? "id" : string.IsNullOrEmpty(asset.Path) ? "path" : "sha256") });
                if (asset.PayloadVersion <= 0)
                    Add(errors, findings, Version($"native asset '{asset.Id}': payloadVersion must be positive.", Field(path, "payloadVersion"), asset.PayloadVersion, positiveOnly: true)
                        with { ResourceId = asset.Id, Index = i });
            }
        }
        if (doc.PlayableBounds is not { } bounds) return;
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MinZ) ||
            !float.IsFinite(bounds.MaxX) || !float.IsFinite(bounds.MaxZ) ||
            !(bounds.MaxX > bounds.MinX) || !(bounds.MaxZ > bounds.MinZ))
        {
            string path = Field("$", "playableBounds");
            string? nonfinite = !float.IsFinite(bounds.MinX) ? "minX" : !float.IsFinite(bounds.MinZ) ? "minZ" :
                !float.IsFinite(bounds.MaxX) ? "maxX" : !float.IsFinite(bounds.MaxZ) ? "maxZ" : null;
            bool x = !(bounds.MaxX > bounds.MinX);
            Add(errors, findings, new(nonfinite is null ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.ValueNonfinite,
                "playableBounds must be finite with MaxX > MinX and MaxZ > MinZ.")
            {
                FieldPath = Field(path, nonfinite ?? (x ? "maxX" : "maxZ")),
                MinimumNumber = nonfinite is null ? (x ? bounds.MinX : bounds.MinZ) : null,
                MaximumNumber = nonfinite is null ? (x ? bounds.MaxX : bounds.MaxZ) : null,
            });
        }
        if (!(bounds.MinX >= doc.Bounds.MinX) || !(bounds.MinZ >= doc.Bounds.MinZ) ||
            !(bounds.MaxX <= doc.Bounds.MaxX) || !(bounds.MaxZ <= doc.Bounds.MaxZ))
        {
            string component = !(bounds.MinX >= doc.Bounds.MinX) ? "minX" : !(bounds.MinZ >= doc.Bounds.MinZ) ? "minZ" :
                !(bounds.MaxX <= doc.Bounds.MaxX) ? "maxX" : "maxZ";
            bool x = component is "minX" or "maxX";
            Add(errors, findings, new(MapDocumentRefusal.BoundsInvalid, "playableBounds must be contained in bounds.")
            {
                FieldPath = Field(Field("$", "playableBounds"), component),
                MinimumNumber = float.IsFinite(x ? bounds.MinX : bounds.MinZ) ? (x ? bounds.MinX : bounds.MinZ) : null,
                MaximumNumber = float.IsFinite(x ? bounds.MaxX : bounds.MaxZ) ? (x ? bounds.MaxX : bounds.MaxZ) : null,
            });
        }
    }

    static void ValidateSupport(MapDocument doc, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (!Enum.IsDefined(doc.SupportRecipe)) Resolver("supportRecipe is unsupported.", "supportRecipe");
        if (doc.Surfaces is null) Add(errors, findings, new(MapDocumentRefusal.SchemaValueRequired, "surfaces must not be null.") { FieldPath = Field("$", "surfaces") });
        bool legacy = doc.ResolverIdentity is null or { PayloadVersion: 1, ResolverVersion: 1 };
        if (legacy)
        {
            if (doc.SupportRecipe != MapSupportRecipe.LegacyXzCallbackV1)
                Resolver("Resolver version 1 requires supportRecipe LegacyXzCallbackV1.", "supportRecipe", 1, MapSupportRecipe.LegacyXzCallbackV1);
            if (doc.Surfaces is { IsEmpty: false }) Resolver("Resolver version 1 requires no surfaces.", "surfaces", 1);
            if (doc.Placements.Any(p => p.SupportBinding is not null))
                Resolver("Resolver version 1 requires no support bindings.", "placements.supportBinding", 1);
        }
        else if (doc.ResolverIdentity is { PayloadVersion: 1, ResolverVersion: 2 })
        {
            if (doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
                Resolver("Resolver version 2 requires supportRecipe AuthoredBindingsV2.", "supportRecipe", 2, MapSupportRecipe.AuthoredBindingsV2);
        }
        else if (doc.ResolverIdentity is { PayloadVersion: not 1 } unsupported)
            Add(errors, findings, Version("resolverIdentity requires payload version 1 and resolver version 1 or 2.", Field(Field("$", "resolverIdentity"), "payloadVersion"), unsupported.PayloadVersion));
        else Resolver("resolverIdentity requires payload version 1 and resolver version 1 or 2.", "resolverIdentity");

        void Resolver(string message, string field, int? version = null, MapSupportRecipe? recipe = null)
        {
            var finding = MapDocumentFinding.ResolverContract(doc, message, field, "document", version, recipe);
            errors.Add(finding.Message);
            findings?.Add(finding);
        }

        var surfaces = new Dictionary<string, MapSurfaceRef>(StringComparer.Ordinal);
        if (doc.Surfaces is { } set)
        {
            for (int i = 0; i < set.Refs.Count; i++)
            {
                MapSurfaceRef surface = set.Refs[i];
                string path = $"{Field(Field("$", "surfaces"), "refs")}[{i}]";
                if (surface is null || string.IsNullOrWhiteSpace(surface.Id))
                {
                    Add(errors, findings, new(MapDocumentRefusal.SchemaValueRequired, "every surface needs a non-empty id.")
                    { FieldPath = surface is null ? path : Field(path, "id"), SurfaceId = surface?.Id, Index = i });
                    continue;
                }
                if (!surfaces.TryAdd(surface.Id, surface)) Add(errors, findings,
                    new(MapDocumentRefusal.IdentityDuplicate, $"duplicate surface id '{surface.Id}'.")
                    { SurfaceId = surface.Id, EntityId = surface.Id, SubjectKind = "surface", FieldPath = Field(path, "id"), Index = i });
                if (surface.Role != MapSurfaceRole.PaintOverride && surface.PaintPriority != 0)
                    Add(errors, findings, new(MapDocumentRefusal.SurfacePaintPriorityMisplaced,
                        $"surface '{surface.Id}': paintPriority is valid only for PaintOverride.")
                    { SurfaceId = surface.Id, FieldPath = Field(path, "paintPriority"), Index = i, ActualInteger = surface.PaintPriority });
                if (surface.Frame is null || !Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy))
                {
                    bool missing = surface.Frame is null;
                    bool role = !missing && !Enum.IsDefined(surface.Role);
                    Add(errors, findings, new(missing ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaEnumUnsupported,
                        $"surface '{surface.Id}': invalid frame, role or presence policy.")
                    { SurfaceId = surface.Id, Index = i, FieldPath = Field(path, missing ? "frame" : role ? "role" : "presencePolicy"),
                        EnumType = missing ? null : role ? typeof(MapSurfaceRole) : typeof(MapPresencePolicy),
                        ActualInteger = missing ? null : role ? (long)surface.Role : (long)surface.PresencePolicy });
                }
                else
                {
                    try { _ = surface.Frame.WorldXz(MapLatticeAddress.Corner(0, 0)); }
                    catch (MapDocumentException)
                    {
                        Add(errors, findings, new(MapDocumentRefusal.BoundsInvalid, $"surface '{surface.Id}': invalid lattice frame.")
                        { SurfaceId = surface.Id, FieldPath = Field(path, "frame"), Index = i });
                    }
                }
                if (surface.SemanticSha256 is not { Length: 64 } digest ||
                    digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    Add(errors, findings, new(MapDocumentRefusal.DigestMalformed, $"surface '{surface.Id}': semanticSha256 must be a lowercase SHA-256 digest.")
                    { SurfaceId = surface.Id, FieldPath = Field(path, "semanticSha256"), Index = i });
                if (surface.IndoorSpan is { } span &&
                    (string.IsNullOrWhiteSpace(span.Id) || span.ParentSpace is null ||
                     string.IsNullOrWhiteSpace(span.ParentSpace.Id) || string.IsNullOrWhiteSpace(span.ParentSpace.Anchor.SurfaceId) ||
                     span.DomainTags is null || span.DomainTags.Any(t => t is null)))
                {
                    string spanPath = Field(path, "indoorSpan");
                    string parentPath = Field(spanPath, "parentSpace");
                    string? requiredPath = string.IsNullOrWhiteSpace(span.Id) ? Field(spanPath, "id") :
                        span.ParentSpace is null ? parentPath :
                        string.IsNullOrWhiteSpace(span.ParentSpace.Id) ? Field(parentPath, "id") :
                        string.IsNullOrWhiteSpace(span.ParentSpace.Anchor.SurfaceId) ? Field(Field(parentPath, "anchor"), "surfaceId") :
                        span.DomainTags is null ? Field(spanPath, "domainTags") : null;
                    if (requiredPath is null && span.DomainTags is { } tags)
                    {
                        int tagIndex = Enumerable.Range(0, tags.Count).First(index => tags[index] is null);
                        requiredPath = $"{Field(spanPath, "domainTags")}[{tagIndex}]";
                    }
                    Add(errors, findings, new(MapDocumentRefusal.SchemaValueRequired, $"surface '{surface.Id}': invalid indoor span.")
                    { SurfaceId = surface.Id, FieldPath = requiredPath, Index = i });
                }
            }
        }
        for (int i = 0; i < doc.Placements.Count; i++)
            ValidateSupportBinding(doc.Placements[i], surfaces, errors, findings, $"{Field("$", "placements")}[{i}]");
    }

    /// <summary>One placement's support binding against the document's declared surfaces.</summary>
    internal static void ValidateSupportBinding(MapPlacement placement, IReadOnlyDictionary<string, MapSurfaceRef> surfaces,
        List<string> errors, List<MapDocumentFinding>? findings = null, string fieldPath = "$")
    {
        if (placement.SupportBinding is not { } binding) return;
        string prefix = $"placement '{placement.Id}': ";
        string path = Field(fieldPath, "supportBinding");
        if (placement.Y is not null) Required("explicit Y cannot have a support binding.", Field(fieldPath, "y"));
        if (!Enum.IsDefined(binding.Kind)) Add(errors, findings, new(MapDocumentRefusal.SchemaEnumUnsupported, prefix + "unsupported support binding kind.")
        { PlacementId = placement.Id, FieldPath = Field(path, "kind"), EnumType = typeof(MapSupportBindingKind), ActualInteger = (long)binding.Kind });
        else if (binding.Kind == MapSupportBindingKind.Surface)
        {
            if (string.IsNullOrWhiteSpace(binding.SurfaceId) || !surfaces.ContainsKey(binding.SurfaceId) || binding.SpaceId is not null)
                Required("surface support binding requires a declared surface and no spaceId.", Field(path,
                    string.IsNullOrWhiteSpace(binding.SurfaceId) || !surfaces.ContainsKey(binding.SurfaceId) ? "surfaceId" : "spaceId"));
        }
        else if (string.IsNullOrWhiteSpace(binding.SpaceId) || binding.SurfaceId is not null)
            Required("space support binding requires a spaceId and no surfaceId.", Field(path,
                string.IsNullOrWhiteSpace(binding.SpaceId) ? "spaceId" : "surfaceId"));
        if ((binding.ReferenceY is { } y && !float.IsFinite(y)) ||
            (binding.SearchBelow is { } below && (!float.IsFinite(below) || below < 0)) ||
            (binding.SearchAbove is { } above && (!float.IsFinite(above) || above < 0)))
        {
            var (field, value) = binding.ReferenceY is { } reference && !float.IsFinite(reference) ? ("referenceY", reference) :
                binding.SearchBelow is { } down && (!float.IsFinite(down) || down < 0) ? ("searchBelow", down) : ("searchAbove", binding.SearchAbove!.Value);
            bool finite = float.IsFinite(value);
            Add(errors, findings, new(finite ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                prefix + "support search values must be finite with nonnegative extents.")
            { PlacementId = placement.Id, FieldPath = Field(path, field), ActualNumber = finite ? value : null, MinimumNumber = field == "referenceY" ? null : 0 });
        }

        void Required(string message, string field) => Add(errors, findings,
            new(MapDocumentRefusal.SchemaValueRequired, prefix + message) { PlacementId = placement.Id, FieldPath = field });
    }

    // Reservation accepts newly imported placements above the old mark, but still checks their IDs. Prefab
    // reservations come first, so a stored document that seeds them before its tiles reports the same message.
    internal static long ValidateNumericIds(MapDocument doc, List<string> errors, bool requireHighWater = true,
        List<MapDocumentFinding>? findings = null)
    {
        long maximum = doc.NumericIdHighWaterMark;
        if (maximum < 0)
            Add(errors, findings, new(MapDocumentRefusal.ValueOutsideRange, "numericIdHighWaterMark must be nonnegative.")
            { FieldPath = Field("$", "numericIdHighWaterMark"), ActualInteger = maximum, HighWaterMark = maximum, MinimumInteger = 0 });
        var seen = new HashSet<long>();
        foreach (var (kind, entityId, id, path) in PrefabNumericReservations(doc))
        {
            string owner = $"{kind} '{entityId}'";
            if (id <= 0) Add(errors, findings, Numeric(MapDocumentRefusal.ValueOutsideRange, $"{owner}: numericId must be positive when present.", entityId, kind, id, doc.NumericIdHighWaterMark, path));
            if (!seen.Add(id)) Add(errors, findings, Numeric(MapDocumentRefusal.IdentityDuplicate, $"{owner}: duplicate numericId '{id}'.", entityId, kind, id, doc.NumericIdHighWaterMark, path));
            if (requireHighWater && id > doc.NumericIdHighWaterMark) Add(errors, findings, Numeric(MapDocumentRefusal.NumericIdHighWaterExceeded, $"{owner}: numericId exceeds numericIdHighWaterMark.", entityId, kind, id, doc.NumericIdHighWaterMark, path));
            maximum = System.Math.Max(maximum, id);
        }
        for (int i = 0; i < doc.Placements.Count; i++)
        {
            MapPlacement placement = doc.Placements[i];
            ValidateNumericId(placement, doc.NumericIdHighWaterMark, seen, errors, requireHighWater, findings, $"{Field("$", "placements")}[{i}]");
            if (placement.NumericId is { } id) maximum = System.Math.Max(maximum, id);
        }
        return maximum;
    }

    /// <summary>Counts each reservation once. A matching transfer owns a tombstone binding's numeric identity.
    /// Active transfers share their standalone placement's ID, and retired transfers reserve it themselves.</summary>
    internal static IEnumerable<(string Owner, long Id)> PrefabNumericIds(MapDocument doc)
    {
        foreach (var (kind, entityId, id, _) in PrefabNumericReservations(doc)) yield return ($"{kind} '{entityId}'", id);
    }

    static IEnumerable<(string Kind, string EntityId, long Id, string FieldPath)> PrefabNumericReservations(MapDocument doc)
    {
        if (doc.Prefabs is not { } set) yield break;
        var transferred = (set.Transfers ?? []).Where(t => t is not null).Select(t => (t.InstanceId, t.Binding)).ToHashSet();
        string prefabsPath = Field("$", "prefabs");
        var instances = set.Instances ?? [];
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var entries = instance?.Bindings?.Entries ?? [];
            string entriesPath = Field(Field($"{Field(prefabsPath, "instances")}[{i}]", "bindings"), "entries");
            for (int j = 0; j < entries.Count; j++)
            {
                var binding = entries[j];
                if (binding?.NumericId is { } id && !(instance!.Removed && instance.Unpacked && !binding.Removed && transferred.Contains((instance.Id, binding))))
                    yield return ("prefab binding", binding.WorldId, id, Field($"{entriesPath}[{j}]", "numericId"));
            }
        }
        var transfers = set.Transfers ?? [];
        for (int i = 0; i < transfers.Count; i++)
            if (transfers[i] is { Retired: true, Binding.NumericId: { } id } transfer)
                yield return ("retired transfer", transfer.Binding.WorldId, id, Field(Field($"{Field(prefabsPath, "transfers")}[{i}]", "binding"), "numericId"));
    }

    /// <summary>One placement's numeric ID. <paramref name="seen"/> accumulates the IDs of every placement checked
    /// so far in the same document.</summary>
    internal static void ValidateNumericId(MapPlacement placement, long highWaterMark, HashSet<long> seen,
        List<string> errors, bool requireHighWater = true, List<MapDocumentFinding>? findings = null, string fieldPath = "$")
    {
        if (placement.NumericId is not { } id) return;
        if (id <= 0)
            Add(errors, findings, Finding(MapDocumentRefusal.ValueOutsideRange, $"placement '{placement.Id}': numericId must be positive when present."));
        if (!seen.Add(id))
            Add(errors, findings, Finding(MapDocumentRefusal.IdentityDuplicate, $"placement '{placement.Id}': duplicate numericId '{id}'."));
        if (requireHighWater && id > highWaterMark)
            Add(errors, findings, Finding(MapDocumentRefusal.NumericIdHighWaterExceeded, $"placement '{placement.Id}': numericId exceeds numericIdHighWaterMark."));

        MapDocumentFinding Finding(MapDocumentRefusal reason, string message) =>
            Numeric(reason, message, placement.Id, "placement", id, highWaterMark, Field(fieldPath, "numericId")) with { PlacementId = placement.Id };
    }

    static string Field(string path, string name) => MapDocumentJsonPath.AppendProperty(path, name);

    static void Add(List<string> errors, List<MapDocumentFinding>? findings, MapDocumentFinding finding)
    {
        errors.Add(finding.Message);
        findings?.Add(finding);
    }

    static MapDocumentFinding Version(string message, string path, int version, bool positiveOnly = false) =>
        new(MapDocumentRefusal.SchemaPayloadVersionUnsupported, message)
        { FieldPath = path, ActualVersion = version, SupportedVersions = positiveOnly ? null : [1], MinimumInteger = positiveOnly ? 1 : null };

    static MapDocumentFinding Numeric(MapDocumentRefusal reason, string message, string entityId, string kind,
        long id, long mark, string path) => new(reason, message)
        {
            EntityId = entityId, SubjectKind = kind, NumericId = id, HighWaterMark = mark, FieldPath = path,
            ActualInteger = reason == MapDocumentRefusal.ValueOutsideRange ? id : null,
            MinimumInteger = reason == MapDocumentRefusal.ValueOutsideRange ? 1 : null,
        };
}
