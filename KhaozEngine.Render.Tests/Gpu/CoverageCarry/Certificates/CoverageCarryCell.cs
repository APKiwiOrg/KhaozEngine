using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// A research palette domain. Linear coefficients and command bits are fixed. The six palette translation
// coefficients vary independently inside inclusive binary32 bounds. No animation sampler is inferred from a name.
internal sealed record CoverageCarryCell(Matrix4x4[] Center, float Radius, CoverageCarryAnchor Anchor,
    CoverageCommandPose[] Commands, string Binding, string DomainDigest, CoverageCarryPlane[] Planes,
    float ReserveWorld, string Motion);

internal readonly record struct CoverageCarryPlane(int Triangle, int Other, int Command, Vector3 Axis);
internal readonly record struct CoverageCarryCheck(bool Accepted, string Cause, int Triangle, int Other,
    double Margin, long Operations, double SlopeUpper, int Command = -1);
internal sealed record CoverageCarryAttempt(string Motion, float ReserveWorld, float Radius,
    CoverageCarryCheck Check, double Milliseconds, long AllocatedBytes, long? PeakWorkingSet, int ProofBytes);
internal sealed record CoverageCarryBuild(CoverageCarryCell? Cell, CoverageCarryAttempt[] Attempts, CoverageCarryCell[] Candidates);
