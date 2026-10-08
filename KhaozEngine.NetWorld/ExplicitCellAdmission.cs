using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>Classifies the complete imported movement batch under one destination lease. The
/// scope union is prepared before acquisition, and all derived state stays staged until accepted.</summary>
internal sealed class ExplicitCellAdmission(ExplicitPlayerMovement settings, MoveTuning tuning,
    WorldFrame frame, IPhysicsWorld? physics, WorldBounds? bounds) : ICellImportAdmission
{
    public CellAdmissionRead Acquire(World staged, IReadOnlyDictionary<long, Entity> entities, CellImportPurpose purpose)
    {
        MovementQueryLease? queries = null;
        try
        {
            MovementBoundary? boundary = null;
            if (bounds is not null)
            {
                MovementAvailability capture = bounds.TryCaptureExplicit(frame.Anchor, out boundary);
                if (capture != MovementAvailability.Known)
                    return new(capture == MovementAvailability.Invalid ? CellAdmissionOutcome.Refused : CellAdmissionOutcome.Unresolved,
                        detail: "The destination boundary has no complete path certificate.");
            }
            var actors = new List<(Entity Entity, PlayerMoveState State)>();
            MovementQueryScope? aggregate = null;
            foreach (Entity entity in entities.Values)
            {
                if (!staged.TryGet(entity, out MovementState movement)) continue;
                if (!staged.TryGet(entity, out ReplicatedPosition position) || position.Frame != frame)
                    return new(CellAdmissionOutcome.Refused, detail: "Imported movement has no destination-frame position.");
                staged.TryGet(entity, out MovementOwnerState owner);
                PlayerMoveState state = PlayerMoveState.From(position, movement, owner);
                MovementQueryScope scope = settings.Scope(state, frame);
                if (scope.CurrentSpace is not null || scope.Frame.Frame != frame ||
                    scope.Frame.PhysicsOrigin != new Vector3(frame.Anchor.X, 0, frame.Anchor.Z))
                    return new(CellAdmissionOutcome.Refused, detail: "Imported movement scope has the wrong frame or a selection hint.");
                if (aggregate is { } previous)
                {
                    if (scope.Identity != previous.Identity || scope.Frame != previous.Frame || scope.WorldId != previous.WorldId)
                        return new(CellAdmissionOutcome.Refused, detail: "Imported movement scopes disagree on their environment identity.");
                    aggregate = new(Vector3.Min(previous.Min, scope.Min), Vector3.Max(previous.Max, scope.Max),
                        Math.Max(previous.MaxRise, scope.MaxRise), Math.Max(previous.MaxDrop, scope.MaxDrop),
                        scope.WorldId, null, scope.Identity, scope.Frame);
                }
                else aggregate = scope;
                actors.Add((entity, state));
            }
            if (aggregate is not { } batch) return new(CellAdmissionOutcome.Accepted);
            MovementAvailability availability = settings.Environment.TryAcquire(batch, out queries);
            if (availability != MovementAvailability.Known || queries is null)
                return Reject(availability == MovementAvailability.Invalid ? CellAdmissionOutcome.Refused : CellAdmissionOutcome.Unresolved,
                    "Destination environment could not certify the imported batch.");
            IPhysicsWorld? expected = physics is IPhysicsWorldQueryView view ? view.SourceWorld : physics;
            if (!ReferenceEquals(queries.Id.SourceWorld, expected))
                return Reject(CellAdmissionOutcome.Refused, "Imported movement captured a different physics source.");
            foreach (var actor in actors)
            {
                var framed = new FramedMovementState(actor.State.Move, queries.Frame, null);
                var result = purpose == CellImportPurpose.Transfer
                    ? ExplicitCharacterMovement.ReclassifyContinuation(framed, tuning, settings.Water, queries, boundary)
                    : ExplicitCharacterMovement.SettlePlacement(framed, tuning, settings.Water, queries, boundary);
                if (result.Outcome != MovementStepOutcome.Advanced)
                    return Reject(result.Outcome is MovementStepOutcome.EnvironmentInvalid or MovementStepOutcome.PlacementRefused
                        ? CellAdmissionOutcome.Refused : CellAdmissionOutcome.Unresolved, "Imported movement placement was not admitted.");
                PlayerMoveState state = actor.State;
                state.Move = result.State.State;
                staged.Set(actor.Entity, ReplicatedPosition.InFrame(frame, state.Position));
                MovementComponents.Set(staged, actor.Entity, state);
            }
            queries.AssertCurrent();
            var accepted = new CellAdmissionRead(CellAdmissionOutcome.Accepted, queries);
            queries = null;
            return accepted;
        }
        catch (ArgumentException) { return Reject(CellAdmissionOutcome.Refused, "Imported movement has invalid environment data."); }
        catch (InvalidOperationException) { return Reject(CellAdmissionOutcome.Unresolved, "Imported movement environment changed during admission."); }
        finally { queries?.Dispose(); }

        CellAdmissionRead Reject(CellAdmissionOutcome outcome, string detail)
        {
            queries?.Dispose();
            queries = null;
            return new(outcome, detail: detail);
        }
    }
}
