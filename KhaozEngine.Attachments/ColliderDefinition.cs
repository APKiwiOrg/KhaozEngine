using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>A capsule attached to a host joint. Equal endpoints describe a sphere.</summary>
/// <param name="Joint">The ordinal host joint name.</param>
/// <param name="Start">The first endpoint in the body's rest model space, in metres.</param>
/// <param name="End">The second endpoint in the body's rest model space, in metres.</param>
/// <param name="Radius">The capsule radius in metres.</param>
public readonly record struct ColliderDefinition(string Joint, Vector3 Start, Vector3 End, float Radius)
{
    internal enum Fault { JointNameEmpty, JointNameMalformedUtf16, StartInvalid, EndInvalid, RadiusInvalid }

    internal Fault? FindFault()
    {
        if (string.IsNullOrWhiteSpace(Joint)) return Fault.JointNameEmpty;
        if (!AttachmentText.IsWellFormedUtf16(Joint)) return Fault.JointNameMalformedUtf16;
        if (!IsFinite(Start)) return Fault.StartInvalid;
        if (!IsFinite(End)) return Fault.EndInvalid;
        if (!float.IsFinite(Radius) || Radius <= 0) return Fault.RadiusInvalid;
        return null;
    }

    static bool IsFinite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    internal static int CompareCanonical(ColliderDefinition left, ColliderDefinition right)
    {
        int joint = StringComparer.Ordinal.Compare(left.Joint, right.Joint);
        if (joint != 0) return joint;
        int start = ComparePoint(left.Start, right.Start);
        if (start != 0) return start;
        int end = ComparePoint(left.End, right.End);
        return end != 0 ? end : left.Radius.CompareTo(right.Radius);
    }

    static int ComparePoint(Vector3 left, Vector3 right)
    {
        int x = left.X.CompareTo(right.X);
        if (x != 0) return x;
        int y = left.Y.CompareTo(right.Y);
        return y != 0 ? y : left.Z.CompareTo(right.Z);
    }
}
