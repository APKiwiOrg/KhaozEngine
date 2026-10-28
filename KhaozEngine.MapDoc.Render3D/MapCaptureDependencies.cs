using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Computes closure from the frozen camera and source index. Dependency casters never nominate lights.</summary>
public static class MapCaptureDependencies
{
    public static MapCaptureDependencyResult Compute(MapSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopes = new List<MapSceneScope>(); var keys = new HashSet<MapSceneResourceKey>();
        var lights = new Dictionary<string, MapEffectiveLight>(StringComparer.Ordinal);
        string digest = "";
        int maxResources = request.Source.Data.Limits.MaxSelectedResources;
        try
        {
            request.Scope.Validate();
            if (request.Source.ClosureHash != request.Closure.Hash) return new(MapSceneStatus.Stale, request.Scope, [], digest);
            var domain = ShadowCaptureDomain.Compute(request.Framing.ViewProjection, request.Policy.Lighting.DirectionToLight,
                request.Policy.Lighting.Shadows, request.Policy.Lighting.MaxCasterDistance);
            void Add(MapBox3 b)
            {
                if (b.MaxX <= b.MinX || b.MaxZ <= b.MinZ) return;
                var scope = Scope(request.Scope, b);
                if (scopes.Any(s => s.Digest == scope.Digest)) return;
                if (DemandDiagnostics.Check(request, "MapCaptureDependencies.AddScope", "MaxCaptureScopes", scopes.Count, 1, 256, scopes.Count == 256, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException("MaxCaptureScopes", (long)scopes.Count + 1, 256);
                foreach (var key in request.Source.Data.Index.Query(b, out _, maxResources))
                    if (keys.Add(key) && DemandDiagnostics.Check(request, "MapCaptureDependencies.MaxSelectedResources", "MaxSelectedResources", 0, keys.Count, maxResources, keys.Count > maxResources, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), keys.Count, maxResources);
                scopes.Add(scope);
            }
            var draw = request.Scope.DrawBounds;
            // Compute the finite work count before stepping in world coordinates.
            double columns = Math.Ceiling((draw.MaxX - draw.MinX) / 64), rows = Math.Ceiling((draw.MaxZ - draw.MinZ) / 64);
            if (DemandDiagnostics.Check(request, "MapCaptureDependencies.ScopeGrid", "MaxCaptureScopes", 0, (decimal)Math.Min(long.MaxValue, columns * rows), 256, columns < 1 || rows < 1 || columns * rows > 256, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException("MaxCaptureScopes", (long)Math.Min(long.MaxValue, columns * rows), 256);
            for (int row = 0; row < (int)rows; row++)
                for (int column = 0; column < (int)columns; column++)
                {
                    double x = draw.MinX + column * 64, z = draw.MinZ + row * 64;
                    Add(new(x, draw.MinY, z, Math.Min(draw.MaxX, x + 64), draw.MaxY, Math.Min(draw.MaxZ, z + 64)));
                }
            double focusX = request.Framing.Eye.Frame.X * 128d + request.Framing.Eye.Local.X;
            double focusZ = request.Framing.Eye.Frame.Z * 128d + request.Framing.Eye.Local.Z;
            scopes.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
            double Distance(MapSceneScope s) { var b = s.DrawBounds; return Math.Pow(b.MinX / 2 + b.MaxX / 2 - focusX, 2) + Math.Pow(b.MinZ / 2 + b.MaxZ / 2 - focusZ, 2); }
            var lightCandidates = new HashSet<MapSceneResourceKey>();
            var admission = new MapSceneAdmission(request.Source.Data.Limits);
            using var activation = admission.Enter();
            using var lightInventory = admission.Workspace();
            long candidateBytes = 0, lightIndexBytes = 0;
            foreach (var volume in domain.ReceiverVolumes)
            {
                var receiver = Intersect(Box(volume), draw);
                if (receiver is not { } r) continue;
                if (request.Policy.Lighting.Shadows.Mode == ShadowMode.ShadowMap)
                {
                    var d = request.Policy.Lighting.DirectionToLight * request.Policy.Lighting.MaxCasterDistance;
                    Add(new(Math.Min(r.MinX, r.MinX + d.X), Math.Min(r.MinY, r.MinY + d.Y), Math.Min(r.MinZ, r.MinZ + d.Z),
                        Math.Max(r.MaxX, r.MaxX + d.X), Math.Max(r.MaxY, r.MaxY + d.Y), Math.Max(r.MaxZ, r.MaxZ + d.Z)));
                }
                foreach (var key in request.Source.Data.Index.Query(r, out _, maxResources, k => k.Kind == "Light"))
                {
                    if (lightCandidates.Contains(key)) continue;
                    if (DemandDiagnostics.Check(request, "MapCaptureDependencies.MaxCaptureLights", "MaxCaptureLights", lightCandidates.Count, 1, 256, lightCandidates.Count == 256, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException("MaxCaptureLights", lightCandidates.Count + 1, 256);
                    MapPreparationCollections.Set(lightCandidates, lightInventory, ref candidateBytes, 40);
                    lightCandidates.Add(key);
                    var bounds = request.Source.Data.MetadataByKey[key].Bounds;
                    using var probe = admission.BeginPreparation();
                    var found = MapSceneAcquisition.Run(request.Source, Scope(request.Scope, bounds), admission);
                    if (found.Scene is null) return new(found.Status, request.Scope, scopes.AsReadOnly(), digest) { Capacity = found.Capacity, Findings = found.Findings };
                    foreach (var light in MapEffectiveAnnotations.Lights(found.Scene))
                        if (Meets(light, r) && !lights.ContainsKey(light.Id))
                        {
                            MapPreparationCollections.Dictionary(lights, lightInventory, ref lightIndexBytes);
                            using var row = found.Scene.PreparationOwnership!.SplitRetained(128L + (light.ShadowExclusion is null ? 0 : 64));
                            lightInventory.Absorb(row);
                            lights.Add(light.Id, light);
                        }
                }
            }
            foreach (var light in lights.Values.OrderBy(l => l.Id, StringComparer.Ordinal))
            {
                double x = light.Position.X.ToDouble(), y = light.Position.Y.ToDouble(), z = light.Position.Z.ToDouble(); float r = light.Radius;
                Add(new(x - r, y - r, z - r, x + r, y + r, z + r));
            }
            digest = MapPlacementDigest.Hash(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/capture-lighting/1");
                var f = request.Framing; foreach (float value in new[] { f.NearPlane, f.FarPlane, f.VerticalSizeOrFovDegrees,
                    request.Policy.Lighting.DirectionToLight.X, request.Policy.Lighting.DirectionToLight.Y, request.Policy.Lighting.DirectionToLight.Z,
                    request.Policy.Lighting.MaxCasterDistance, request.Policy.Lighting.SunIntensity,
                    request.Policy.Lighting.SunColour.R, request.Policy.Lighting.SunColour.G, request.Policy.Lighting.SunColour.B, request.Policy.Lighting.SunColour.A }) w.WriteNumberValue(value);
                w.WriteStringValue(request.Policy.Lighting.ShadowDigest);
                var matrix = f.ViewProjection;
                foreach (var row in new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                    matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44 }) w.WriteNumberValue(row);
                foreach (var light in lights.Values.OrderBy(l => l.Id, StringComparer.Ordinal))
                {
                    w.WriteStringValue(light.Id); w.WriteNumberValue(light.Position.X.ToDouble()); w.WriteNumberValue(light.Position.Y.ToDouble());
                    w.WriteNumberValue(light.Position.Z.ToDouble()); w.WriteNumberValue(light.Radius); w.WriteNumberValue(light.Intensity); w.WriteBooleanValue(light.CastShadows);
                    w.WriteNumberValue(light.Colour.X); w.WriteNumberValue(light.Colour.Y); w.WriteNumberValue(light.Colour.Z); w.WriteStringValue(light.OwnerPlacementId);
                    if (light.ShadowExclusion is { } exclusion) w.WriteStringValue(exclusion.Digest());
                }
                w.WriteEndArray();
            });
            return new(MapSceneStatus.CompleteForScope, request.Scope, scopes.AsReadOnly(), digest);
        }
        catch (Exception error) when (error is MapSceneCapacityException or OverflowException)
        { return new(MapSceneStatus.CapacityExceeded, request.Scope, scopes.AsReadOnly(), digest) { Capacity = MapSceneCapacity.From(error), Findings = [error.Message] }; }
        catch (ArgumentException) { return new(MapSceneStatus.Invalid, request.Scope, scopes.AsReadOnly(), digest); }
    }
    static bool Meets(MapEffectiveLight l, MapBox3 b)
    {
        double x = l.Position.X.ToDouble(), y = l.Position.Y.ToDouble(), z = l.Position.Z.ToDouble();
        double dx = x - Math.Clamp(x, b.MinX, b.MaxX), dy = y - Math.Clamp(y, b.MinY, b.MaxY), dz = z - Math.Clamp(z, b.MinZ, b.MaxZ);
        return dx * dx + dy * dy + dz * dz <= l.Radius * (double)l.Radius;
    }
    static MapBox3 Box(MeshBounds b) => new(b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
    static MapBox3? Intersect(MapBox3 a, MapBox3 b)
    {
        var r = new MapBox3(Math.Max(a.MinX, b.MinX), Math.Max(a.MinY, b.MinY), Math.Max(a.MinZ, b.MinZ),
            Math.Min(a.MaxX, b.MaxX), Math.Min(a.MaxY, b.MaxY), Math.Min(a.MaxZ, b.MaxZ));
        return r.MinX < r.MaxX && r.MinY <= r.MaxY && r.MinZ < r.MaxZ ? r : null;
    }
    internal static MapSceneScope Scope(MapSceneScope original, MapBox3 b)
    {
        var f = original.Surfaces.Frame;
        // Reverse discovery intentionally has unbounded Y. Acquisition scopes remain finite.
        b = b with
        {
            MinY = double.IsFinite(b.MinY) ? b.MinY : original.DrawBounds.MinY,
            MaxY = double.IsFinite(b.MaxY) ? b.MaxY : original.DrawBounds.MaxY
        };
        return original with
        {
            DrawBounds = b,
            Surfaces = original.Surfaces with
            {
                LocalMin = new((float)(b.MinX - f.X * 128d), (float)(b.MinZ - f.Z * 128d)),
                LocalMax = new((float)(b.MaxX - f.X * 128d), (float)(b.MaxZ - f.Z * 128d)),
                MinY = (float)b.MinY,
                MaxY = (float)b.MaxY
            }
        };
    }
}
