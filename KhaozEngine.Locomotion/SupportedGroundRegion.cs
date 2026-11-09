using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

/// <summary>The ordinary pin's rounded frame mapping over an immutable producer capture.</summary>
internal sealed class SupportedGroundRegion(IMovementSupportedGroundRegion source, Vector3 origin,
    Vector2 minimum, Vector2 maximum, float low, float high)
{
    internal float MinimumHeight => low;
    internal float MaximumHeight => high;
    internal float MinimumNormalY => source.MinimumNormalY;
    internal MovementAvailability Sample(Vector2 axis, out FootAnalyticGround ground, out SupportHeightTranslation translation)
    {
        ground = default;
        translation = default;
        if (axis.X < minimum.X || axis.X > maximum.X || axis.Y < minimum.Y || axis.Y > maximum.Y)
            return MovementAvailability.Unresolved;
        var status = source.Sample(new(axis.X + origin.X, axis.Y + origin.Z), out float height, out var normal);
        if (status != MovementAvailability.Known) return status;
        float local = SupportedCapsulePose.LocalSupport(height, origin.Y);
        if (!float.IsFinite(local) || local < low || local > high || !MovementEnvironmentValidation.Unit(normal))
            return MovementAvailability.Unresolved;
        ground = new(local, normal);
        translation = new(local, origin.Y, height);
        return MovementAvailability.Known;
    }
}
