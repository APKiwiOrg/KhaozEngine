namespace KhaozEngine.Locomotion;

/// <summary>Optional same-pin support facts. The producer certifies scoped fields, installed features and legal links.</summary>
public interface IMovementContactSupportPin
{
    /// <summary>Known with null certifies analytic absence at this axis, never a global terrain fallback.</summary>
    MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground);
    /// <summary>Binds certified provenance through the captured selected registration and generation.</summary>
    MovementAvailability BindContactSupport(in MovementContactSupportRequest request, out MovementContactSupportBinding binding);
}
