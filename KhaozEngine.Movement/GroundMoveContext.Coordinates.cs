using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    private readonly Func<float, float, float> _localHeight;
    private readonly Func<float, float, Vector3>? _localNormal;
    private readonly Func<float, float, Vector2>? _localClamp;
    private readonly Func<float, float, float, MovementMedium>? _localMedium;
    private Vector3 _origin;
    private bool _stepping;

    private float LocalHeight(float x, float z)
    {
        EnsureOrigin();
        float absolute = GroundHeight(x + _origin.X, z + _origin.Z);
        EnsureOrigin();
        return absolute - _origin.Y;
    }

    private Vector3 LocalNormal(float x, float z)
    {
        EnsureOrigin();
        Vector3 normal = GroundNormal!(x + _origin.X, z + _origin.Z);
        EnsureOrigin();
        return normal;
    }

    private Vector2 LocalClamp(float x, float z)
    {
        EnsureOrigin();
        Vector2 absolute = ClampXz!(x + _origin.X, z + _origin.Z);
        EnsureOrigin();
        return absolute - new Vector2(_origin.X, _origin.Z);
    }

    private MovementMedium LocalMedium(float x, float z, float feetY)
    {
        EnsureOrigin();
        MovementMedium absolute = Medium!(x + _origin.X, z + _origin.Z, feetY + _origin.Y);
        EnsureOrigin();
        return absolute with { WaterSurfaceY = absolute.WaterSurfaceY - _origin.Y };
    }

    private void EnsureOrigin()
    {
        if ((Physics?.Origin ?? Vector3.Zero) != _origin)
            throw new InvalidOperationException("The physics origin changed during a ground movement step.");
    }
}
