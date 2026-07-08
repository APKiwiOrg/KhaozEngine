using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The mass/inertia and initial-motion knobs for a dynamic body added via
/// <see cref="IPhysicsWorld.AddDynamic"/>. Kept a small value type so the seam stays backend-agnostic:
/// the backend derives the full inertia tensor from the body's shape and this <see cref="Mass"/>.
/// A body with <see cref="Mass"/> &lt;= 0 is treated as having infinite mass (unaffected by gravity or
/// impacts, but still moved by <see cref="LinearVelocity"/>/<see cref="AngularVelocity"/>) - a kinematic
/// body. <see cref="SleepThreshold"/> lets Bepu-style backends sleep a body that has come to rest so it
/// stops consuming solver time; a negative value keeps the backend default.</summary>
public readonly record struct DynamicBodyDescription
{
    /// <summary>Body mass in kilograms. The backend computes the inertia tensor from the shape and this
    /// mass. Values &lt;= 0 mean an infinite-mass (kinematic) body: gravity and collisions do not move it,
    /// but it still travels along <see cref="LinearVelocity"/>/<see cref="AngularVelocity"/>.</summary>
    public float Mass { get; init; }

    /// <summary>Initial linear velocity in metres/second (world space). Default: zero.</summary>
    public Vector3 LinearVelocity { get; init; }

    /// <summary>Initial angular velocity in radians/second (world space). Default: zero.</summary>
    public Vector3 AngularVelocity { get; init; }

    /// <summary>The velocity-squared magnitude below which the backend may put the body to sleep once it
    /// has settled. Negative keeps the backend default; zero disables sleeping (the body never sleeps).</summary>
    public float SleepThreshold { get; init; }

    /// <summary>A dynamic body of the given <paramref name="mass"/> at rest, backend-default sleeping.</summary>
    public DynamicBodyDescription(float mass)
    {
        Mass = mass;
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        SleepThreshold = -1f;
    }

    /// <summary>A dynamic body of the given <paramref name="mass"/> at rest, backend-default sleeping.</summary>
    public static DynamicBodyDescription WithMass(float mass) => new(mass);
}
