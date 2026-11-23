using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Geometry arithmetic for the claim checks stays scalar and double throughout.
internal readonly record struct ClaimPoint(double X, double Y, double Z)
{
    internal ClaimPoint(Vector3 value) : this(value.X, value.Y, value.Z) { }

    public static ClaimPoint operator +(ClaimPoint left, ClaimPoint right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static ClaimPoint operator -(ClaimPoint left, ClaimPoint right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static ClaimPoint operator *(ClaimPoint value, double scalar) =>
        new(value.X * scalar, value.Y * scalar, value.Z * scalar);

    internal double LengthSquared => Dot(this, this);

    internal static double Dot(ClaimPoint left, ClaimPoint right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    internal static ClaimPoint Cross(ClaimPoint left, ClaimPoint right) =>
        new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);

    internal static ClaimPoint Min(ClaimPoint left, ClaimPoint right) =>
        new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

    internal static ClaimPoint Max(ClaimPoint left, ClaimPoint right) =>
        new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));
}
