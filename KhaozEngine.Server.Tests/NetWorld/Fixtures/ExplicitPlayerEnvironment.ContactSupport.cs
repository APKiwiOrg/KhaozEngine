using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

internal sealed partial class ExplicitPlayerEnvironment
{
    sealed partial class Pin : IMovementContactSupportPin
    {
        public MovementAvailability SampleAnalyticGround(in MovementSupportRequest request,
            out MovementAnalyticGround? ground)
        {
            ground = null;
            AssertCurrent();
            return request.IsValid && request.Body.CurrentSpace == new MovementSpaceKey("world", "room")
                ? MovementAvailability.Known : MovementAvailability.Unresolved;
        }

        public MovementAvailability BindContactSupport(in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding)
        {
            binding = default;
            AssertCurrent();
            var room = new MovementSpaceKey("world", "room");
            if (!request.IsValid || request.Static != owner.FloorHandle || request.FeatureId is < 0 or > 5 ||
                request.Transition.OriginSpace != room || Math.Abs(request.Transition.StartFeet.X) > 16 ||
                Math.Abs(request.Transition.StartFeet.Z) > 16 || Math.Abs(request.Axis.X) > 8 || Math.Abs(request.Axis.Y) > 8)
                return MovementAvailability.Unresolved;
            binding = new(new("world", "floor"), room, null, owner.Identity);
            return MovementAvailability.Known;
        }
    }
}
