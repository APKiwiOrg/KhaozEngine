using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

public sealed partial class EnvironmentAcquisitionFixture
{
    public bool ContactSupportEnabled;
    public delegate MovementAvailability AnalyticGroundHandler(in MovementSupportRequest request,
        out MovementAnalyticGround? ground);
    public delegate MovementAvailability ContactSupportHandler(in MovementContactSupportRequest request,
        out MovementContactSupportBinding binding);
    public AnalyticGroundHandler? OnAnalyticGround;
    public ContactSupportHandler? OnContactSupport;
    public int AnalyticGroundCalls;
    public int ContactSupportCalls;

    sealed class ContactPin : Pin, IMovementContactSupportPin
    {
        readonly EnvironmentAcquisitionFixture owner;
        public ContactPin(EnvironmentAcquisitionFixture owner, IPhysicsQueryLease physics) : base(owner, physics)
        {
            this.owner = owner;
        }

        public MovementAvailability SampleAnalyticGround(in MovementSupportRequest request,
            out MovementAnalyticGround? ground)
        {
            owner.AnalyticGroundCalls++;
            ground = null;
            AssertCurrent();
            return owner.OnAnalyticGround is { } sample ? sample(request, out ground) : MovementAvailability.Known;
        }

        public MovementAvailability BindContactSupport(in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding)
        {
            owner.ContactSupportCalls++;
            binding = default;
            AssertCurrent();
            return owner.OnContactSupport is { } bind ? bind(request, out binding) : MovementAvailability.Unresolved;
        }
    }
}
