using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>A complete prepared roof buffer or a typed refusal, with no partial buffer.</summary>
public sealed record MapRoofEvaluationBufferResult(MapSceneStatus Status, MapRoofEvaluationBuffer? Buffer, string? Detail)
{
    public MapSceneCapacity? Capacity { get; init; }
}

/// <summary>Prepared scoped membership and reusable roof intent. Roofs is a live read-only view.</summary>
public sealed class MapRoofEvaluationBuffer
{
    internal MapPreparationReservation? PreparationOwnership { get; private set; }
    public long RetainedBytes => checked(128L + _scratch.InventoryBytes + _intent.InventoryBytes + _sceneInventoryBytes);
    readonly long _sceneInventoryBytes;
    readonly MapScopedScene _scene;
    readonly MapSpaceMembership _membership;
    readonly MapSpaceMembershipScratch _scratch;
    readonly MapRoofIntentRows _intent;
    MapMembershipObservation _observation;

    MapRoofEvaluationBuffer(MapScopedScene scene, MapMembershipPreparation demand, long sceneInventoryBytes, MapMembershipDemandPlan plan)
    {
        _scene = scene;
        _sceneInventoryBytes = sceneInventoryBytes;
        _membership = new(scene.SurfaceView);
        _scratch = new(scene.SurfaceView, demand, plan);
        _intent = new(scene.CapturedBuildingIntent);
        _observation = new(MapMembershipStatus.Outside, null, default, scene.SurfaceView.ReadWitness);
    }
    /// <summary>Admits the complete scoped membership product against a fresh allowance from the scene's limits.
    /// Capacity, precision and input refusals carry no partial buffer. The returned Roofs view is reused by Evaluate.</summary>
    public static MapRoofEvaluationBufferResult Prepare(MapScopedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var status = MapMembershipPreparation.Measure(scene, out var demand, out string? detail);
        if (status != MapSceneStatus.CompleteForScope) return new(status, null, detail);
        var limits = scene.Source.Data.Limits;
        if (demand.Bytes > limits.MaxPreparedBytes) return Refused(new MapSceneCapacity(nameof(MapSceneLimits.MaxPreparedBytes), demand.Bytes, limits.MaxPreparedBytes));
        if (demand.Faces > limits.MaxPreparedFaces) return Refused(new MapSceneCapacity(nameof(MapSceneLimits.MaxPreparedFaces), demand.Faces, limits.MaxPreparedFaces));
        status = MapMembershipPreflight.Check(scene, demand);
        return status == MapSceneStatus.CompleteForScope ? PrepareAdmitted(scene, new MapSceneAdmission(limits), demand) : Refused(status);
    }
    internal static MapRoofEvaluationBufferResult Prepare(MapScopedScene scene, MapSceneAdmission admission, bool requireCompletePolicy = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(admission);
        var status = MapMembershipPreparation.Measure(scene, out var demand, out string? detail);
        if (status != MapSceneStatus.CompleteForScope) return new(status, null, detail);
        if (admission.PreparationRefusal(demand.Bytes, demand.Faces) is { } capacity) return Refused(capacity);
        status = MapMembershipPreflight.Check(scene, demand);
        return status == MapSceneStatus.CompleteForScope ? PrepareAdmitted(scene, admission, demand, requireCompletePolicy) : Refused(status);
    }
    static MapRoofEvaluationBufferResult PrepareAdmitted(MapScopedScene scene, MapSceneAdmission admission, MapMembershipPreparation demand, bool requireCompletePolicy = false)
    {
        using var preparation = admission.BeginPreparation();
        try
        {
            var sceneOwner = requireCompletePolicy ? scene.PreparationOwnership : null;
            using var dependency = sceneOwner is null ? null : preparation.WorkspaceOwner.Borrow(sceneOwner);
            long sceneInventoryBytes = sceneOwner?.Bytes ?? 0;
            if (requireCompletePolicy)
            {
                // The dependency remains charged in this view throughout validation and membership.
                // A refusal releases only our borrowed attribution, preserving the original receipt.
                using var validation = admission.BeginPreparation();
                if (!admission.TryReservePreparation(demand.Bytes, 0, out var validationCapacity)) return Refused(validationCapacity!);
                using var validationPrecharge = MapSceneAdmission.Suspend();
                var errors = MapPrismCoverage.Validate(scene.SurfaceView, scene.Scope.Surfaces.Limits.Refinement ?? new());
                if (errors.Count != 0) return new(MapSceneStatus.Invalid, null, "Incomplete canonical roof policy. " + string.Join("\n", errors));
            }
            var records = MapSpaceGeometry.Records(scene.SurfaceView).ToArray();
            var plan = new MapMembershipDemandPlan(scene.SurfaceView, records);
            long sparseBytes = checked(demand.Bytes + plan.Faces * (2 * 1280 + 8192) + plan.Cells * 8192L + plan.Associations * 192L);
            if (!admission.TryReservePreparation(sparseBytes, plan.Faces, out var capacity)) return Refused(capacity!);
            MapRoofEvaluationBuffer buffer;
            using (MapSceneAdmission.Suspend()) buffer = new(scene, demand, sceneInventoryBytes, plan);
            preparation.EnsureCapacity(buffer.RetainedBytes, sceneOwner);
            dependency?.Absorb();
            buffer.PreparationOwnership = preparation.Complete(buffer.RetainedBytes);
            return new(scene.Witness.WholeWorldComplete ? MapSceneStatus.WholeWorldComplete : MapSceneStatus.CompleteForScope, buffer, null);
        }
        catch (MapSceneCapacityException error) { return Refused(error.Capacity); }
    }
    static MapRoofEvaluationBufferResult Refused(MapSceneCapacity capacity) => new(MapSceneStatus.CapacityExceeded, null, capacity.Message) { Capacity = capacity };
    static MapRoofEvaluationBufferResult Refused(MapSceneStatus status) => new(status, null, status switch
    {
        MapSceneStatus.NotRepresentable => "Membership exact precision cannot be prepared.",
        MapSceneStatus.CapacityExceeded => "Membership preparation exceeds the scene allowance.",
        _ => "Invalid acquired membership geometry.",
    });
    public MapMembershipStatus Status => _observation.Status;
    public string? SpaceId => _observation.SpaceId;
    public IReadOnlyList<MapRoofIntent> Roofs => _intent.Roofs;

    public void Evaluate(MapFramePoint observer, MapRoofMode mode)
    {
        if (mode is not (MapRoofMode.Auto or MapRoofMode.AlwaysVisible or MapRoofMode.AlwaysHidden))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _observation = _membership.Query(observer, _scratch);
        _intent.Evaluate(_observation, mode);
    }
    internal MapRoofIntentResult Detach() => new(Status, SpaceId, _intent.Detach(), _scene.SurfaceView.ReadWitness);
}
