using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Device-free dependency and focus admission. Temporal frames are never readiness evidence.</summary>
public static class MapCapturePreparation
{
    public static MapSnapshotResult Prepare(MapSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var empty = new MapSceneWitness(request.Closure.Hash, request.Source.SourceGeneration, request.Scope.Digest, [], [], [], [], false, false);
        var readiness = new MapRenderReadiness(new(0, MapSceneStatus.Incomplete, null, null), 0, 0, 0, 0, 0);
        var dependencies = MapCaptureDependencies.Compute(request);
        var acquired = new List<MapSceneScope>(); var witnesses = new List<MapSceneWitness>();
        var source = MapCaptureFocusSource.Unresolved; var echo = request;
        bool capped = false;
        MapSnapshotResult Result(MapSceneStatus status, params string[] findings)
        {
            var scene = empty with
            {
                Required = witnesses.SelectMany(w => w.Required).Distinct().ToArray(),
                Present = witnesses.SelectMany(w => w.Present).Distinct().ToArray(),
                KnownEmpty = witnesses.SelectMany(w => w.KnownEmpty).Distinct().ToArray(),
                Unavailable = witnesses.SelectMany(w => w.Unavailable).Distinct().ToArray(),
                CompleteForScope = status == MapSceneStatus.CompleteForScope && !capped
            };
            return new(status, null, null, new(request.Scope, dependencies.RequiredScopes, acquired.AsReadOnly(), scene, capped, dependencies.LightingDigest),
                readiness, echo, source, findings);
        }
        try
        {
            if (request.Policy.WarmupFrames < 0 || request.Policy.MaxSettleOperations < 0 || request.PreviewWindowLimit < 1 ||
                request.Route == MapCaptureRoute.TopDown && !request.Framing.Orthographic) return Result(MapSceneStatus.Invalid, "capture.invalid");
            if (dependencies.Status == MapSceneStatus.Stale) return Result(MapSceneStatus.Stale, "closure.stale");
            foreach (string id in request.Policy.Render.MaterialResourceIds.Concat(request.Policy.Render.TerrainMaterials.Values)) request.Closure.GetResource(id);
            foreach (string id in request.Policy.Render.AnimatedAssets) request.Closure.GetAsset(id);
            if (dependencies.Status != MapSceneStatus.CompleteForScope && request.Requirement == MapCaptureRequirement.CompleteScope)
                return Result(dependencies.Status, "closure.unavailable");
            if (request.Focus is not null || request.Route == MapCaptureRoute.View)
            {
                echo = request with { Focus = request.Focus ?? request.Framing.Eye };
                source = request.Focus.HasValue ? MapCaptureFocusSource.Explicit : MapCaptureFocusSource.ViewEye;
            }
            else
            {
                var b = request.Scope.DrawBounds; double x = b.MinX / 2 + b.MaxX / 2, z = b.MinZ / 2 + b.MaxZ / 2;
                var support = request.Source.Acquire(MapCaptureDependencies.Scope(request.Scope, new(x - .001, b.MinY, z - .001, x + .001, b.MaxY, z + .001)));
                if (support.Scene is null) return Result(support.Status, "FocusUnavailable");
                echo = request with { Focus = MapCaptureFocus.Resolve(request, support.Scene) }; source = MapCaptureFocusSource.TopDownSupport;
            }
            int limit = request.Requirement == MapCaptureRequirement.Preview ? request.PreviewWindowLimit : 256;
            capped = dependencies.Status != MapSceneStatus.CompleteForScope || dependencies.RequiredScopes.Count > limit;
            var admission = new MapSceneAdmission(request.Source.Data.Limits);
            using var activation = admission.Enter();
            foreach (var scope in dependencies.RequiredScopes.Take(limit))
            {
                var read = MapSceneAcquisition.Run(request.Source, scope, admission); witnesses.Add(read.Witness);
                if (read.Scene is null)
                {
                    if (request.Requirement == MapCaptureRequirement.CompleteScope) return Result(read.Status, "closure.unavailable");
                    capped = true; break;
                }
                acquired.Add(scope);
            }
            readiness = readiness with { Pending = acquired.Count, CpuBytes = admission.RetainedBytes };
            if (acquired.Count == 0) return Result(MapSceneStatus.Incomplete, capped ? "scope.capped" : "closure.unavailable");
            return capped ? Result(MapSceneStatus.Incomplete, "scope.capped") : Result(MapSceneStatus.CompleteForScope);
        }
        catch (ArgumentException error) { return Result(MapSceneStatus.Invalid, error.Message == "FocusUnavailable" ? "FocusUnavailable" : "capture.invalid"); }
        catch (MapDocumentException) { return Result(MapSceneStatus.Missing, "closure.unavailable"); }
        catch (MapSceneCapacityException) { capped = request.Requirement == MapCaptureRequirement.Preview; return Result(capped ? MapSceneStatus.Incomplete : MapSceneStatus.CapacityExceeded, capped ? "scope.capped" : "closure.capacity"); }
    }
}
