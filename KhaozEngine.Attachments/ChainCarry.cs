using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The share of the wearer's rigid substep motion carried by a chain.</summary>
internal readonly record struct ChainCarry(Vector3 Pivot, Vector3 Offset, Quaternion Turn)
{
    internal Vector3 TransformPoint(Vector3 point) => Turn == Quaternion.Identity
        ? point + Offset
        : Pivot + Offset + TransformDirection(point - Pivot);

    internal Vector3 TransformDirection(Vector3 direction) => Turn == Quaternion.Identity
        ? direction
        : Vector3.Transform(direction, Turn);
}
