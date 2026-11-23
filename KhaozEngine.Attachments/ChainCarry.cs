using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The share of the wearer's rigid substep motion carried by a chain.</summary>
internal readonly record struct ChainCarry(Vector3 Pivot, Vector3 Offset, Quaternion Turn)
{
    internal static ChainCarry Between(float trail, Vector3 previous, Vector3 current, Quaternion wearerTurn)
    {
        float carry = 1 - trail;
        var turn = carry == 0 ? Quaternion.Identity : Quaternion.Slerp(Quaternion.Identity, wearerTurn, carry);
        return new ChainCarry(previous, carry * (current - previous), turn);
    }

    internal Vector3 TransformPoint(Vector3 point) => Turn == Quaternion.Identity
        ? point + Offset
        : Pivot + Offset + TransformDirection(point - Pivot);

    internal Vector3 TransformDirection(Vector3 direction) => Turn == Quaternion.Identity
        ? direction
        : Vector3.Transform(direction, Turn);
}
