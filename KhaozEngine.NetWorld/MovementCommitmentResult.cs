using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>The authoritative result of one commitment lifecycle transition.</summary>
public readonly record struct MovementCommitmentResult(int Slot, uint Sequence, MovementCommitmentEndReason Reason,
    Vector3 Position, Vector2 Direction);

// The bridge from the request to the Locomotion state the float heads carry. The request moved down into
// KhaozEngine.Netcode with IAdminControllable (see TypeForwards.cs), below Locomotion, so these two halves of it
// could not move with it. Extension methods under the names the request's own internal members had, so both heads'
// call sites read exactly as they did.
internal static class MovementCommitmentRequestExtensions
{
    /// <summary>Builds the commitment this request describes, stamped with <paramref name="sequence"/>.</summary>
    public static MovementCommitment Start(this in MovementCommitmentRequest request, uint sequence) =>
        new(sequence, request.Direction, request.HorizontalSpeed, request.VerticalSpeed, request.Gravity,
            request.PreparationSeconds, request.RecoverySeconds, request.TimeoutSeconds);

    /// <summary>Refuses a request MovementCommitment would refuse. A default request never ran the constructor,
    /// which is the case this exists for.</summary>
    public static void Validate(this in MovementCommitmentRequest request) => _ = request.Start(1u);
}
