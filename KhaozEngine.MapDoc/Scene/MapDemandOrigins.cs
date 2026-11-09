using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Diagnostic provenance for limit copies whose effective allowances are remainders or minima. Every
/// method is a no-op unless a demand observer is subscribed.</summary>
internal static class MapDemandOrigins
{
    internal static CapacityDemandOrigin[] Of(object? source, string name, decimal allowance, decimal consumed = 0, decimal scale = 1) =>
        CapacityDemandDiagnostics.Origins(source, name, allowance, consumed, scale);

    internal static void Derive(object derived, string name, params IEnumerable<CapacityDemandOrigin>[] parts)
    {
        if (CapacityDemandDiagnostics.IsObserved) CapacityDemandDiagnostics.Derive(derived, name, parts.SelectMany(p => p));
    }

    internal static void Inherit(object copy, object source)
    {
        if (CapacityDemandDiagnostics.IsObserved) CapacityDemandDiagnostics.Inherit(copy, source);
    }

    /// <summary>Gives <paramref name="copy"/> the origins of one derived field of <paramref name="source"/>.</summary>
    internal static void Keep(object copy, string name, object source, decimal value)
    {
        if (!CapacityDemandDiagnostics.IsObserved) return;
        var origins = Of(source, name, value);
        if (!Plain(origins, source)) CapacityDemandDiagnostics.Derive(copy, name, origins);
    }

    // The minimum of one field over two records keeps both originating fields, unless both are the same plain value.
    static void Min(object derived, string name, object a, decimal va, object b, decimal vb)
    {
        var first = Of(a, name, va);
        var second = Of(b, name, vb);
        if (va == vb && Plain(first, a) && Plain(second, b)) return;
        CapacityDemandDiagnostics.Derive(derived, name, first.Concat(second));
    }

    static bool Plain(CapacityDemandOrigin[] origins, object source) =>
        origins.Length == 1 && ReferenceEquals(origins[0].Identity, source) && origins[0].Consumed == 0 && origins[0].Scale == 1;

    /// <summary>Records the field-wise minimum of a scope's query limits and a context's query cap.</summary>
    internal static void Intersect(MapQueryLimits derived, MapQueryLimits scope, MapQueryLimits cap)
    {
        if (CapacityDemandDiagnostics.IsObserved) StoreIntersection(derived, scope, cap);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StoreIntersection(MapQueryLimits derived, MapQueryLimits scope, MapQueryLimits cap)
    {
        Min(derived, nameof(MapQueryLimits.MaxCandidatePatches), scope, scope.MaxCandidatePatches, cap, cap.MaxCandidatePatches);
        Min(derived, nameof(MapQueryLimits.MaxInspectedFaces), scope, scope.MaxInspectedFaces, cap, cap.MaxInspectedFaces);
        Min(derived, nameof(MapQueryLimits.MaxSupportIntersections), scope, scope.MaxSupportIntersections, cap, cap.MaxSupportIntersections);
        Min(derived, nameof(MapQueryLimits.MaxPageReads), scope, scope.MaxPageReads, cap, cap.MaxPageReads);
        Min(derived, nameof(MapQueryLimits.MaxRecordReads), scope, scope.MaxRecordReads, cap, cap.MaxRecordReads);
        Min(derived, nameof(MapQueryLimits.MaxRecordDepth), scope, scope.MaxRecordDepth, cap, cap.MaxRecordDepth);
        if (derived.Refinement is { } refinement && scope.Refinement is { } a && cap.Refinement is { } b &&
            !ReferenceEquals(refinement, a) && !ReferenceEquals(refinement, b))
            StoreIntersection(refinement, a, b);
    }

    /// <summary>Records the field-wise minimum of two refinement profiles.</summary>
    internal static void Intersect(MapRefinementLimits derived, MapRefinementLimits a, MapRefinementLimits b)
    {
        if (CapacityDemandDiagnostics.IsObserved) StoreIntersection(derived, a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StoreIntersection(MapRefinementLimits derived, MapRefinementLimits a, MapRefinementLimits b)
    {
        Min(derived, nameof(MapRefinementLimits.MaxSourceFacesPerBound), a, a.MaxSourceFacesPerBound, b, b.MaxSourceFacesPerBound);
        Min(derived, nameof(MapRefinementLimits.MaxPairChecks), a, a.MaxPairChecks, b, b.MaxPairChecks);
        Min(derived, nameof(MapRefinementLimits.MaxRefinementFaces), a, a.MaxRefinementFaces, b, b.MaxRefinementFaces);
        Min(derived, nameof(MapRefinementLimits.MaxRefinementVertices), a, a.MaxRefinementVertices, b, b.MaxRefinementVertices);
        Min(derived, nameof(MapRefinementLimits.MaxContextPatches), a, a.MaxContextPatches, b, b.MaxContextPatches);
        Min(derived, nameof(MapRefinementLimits.MaxContextFaces), a, a.MaxContextFaces, b, b.MaxContextFaces);
    }

    /// <summary>Copies the provenance of the foliage fields other copies derive, so a later copy keeps it.</summary>
    internal static void InheritFoliage(MapFoliageLimits copy, MapFoliageLimits source, string except)
    {
        if (CapacityDemandDiagnostics.IsObserved) StoreFoliage(copy, source, except);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StoreFoliage(MapFoliageLimits copy, MapFoliageLimits source, string except)
    {
        Field(nameof(MapFoliageLimits.MaxPrepareScopes), source.MaxPrepareScopes);
        Field(nameof(MapFoliageLimits.MaxFaces), source.MaxFaces);
        Field(nameof(MapFoliageLimits.MaxSamples), source.MaxSamples);
        Field(nameof(MapFoliageLimits.MaxQuerySamples), source.MaxQuerySamples);
        Field(nameof(MapFoliageLimits.MaxQueryRetainedBytes), source.MaxQueryRetainedBytes);
        void Field(string name, decimal value) { if (name != except) Keep(copy, name, source, value); }
    }
}
