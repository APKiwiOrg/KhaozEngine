using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

// Shared by KhaozEngine.TileWorld.Physics.Tests, which runs the default-control row, and
// KhaozEngine.TileWorld.Physics.FloatingPoint.Tests, which runs every changed-control row.
public sealed partial class WaterBoundaryWadeCertificateTests
{
    // Free levels as surface, half height, enter fraction and proximity. The last row's proximity is 1.5 times the
    // smallest normal binary32, so with an error of twice that value the binary32 sum is subnormal and flushes.
    static readonly (float Surface, float HalfHeight, float Enter, float Proximity)[] Lines =
    [
        (-0.001f, 0.75f, 0.625f, 0), (-0.37f, 0.75f, 0.65f, 0), (-0.37f, 0.75f, 0.65f, 0.001f),
        (1000.3f, 0.9f, 0.55f, 0.001f), (-0.5f, 0.75f, 0.65f, -1.7632415E-38f),
    ];

    // Every error a Known trace can certify lies in [0, 0.001]. The second value is twice the smallest normal binary32.
    static readonly float[] Errors = [0, 2.3509887E-38f, 1e-4f, 5e-4f, MovementQueryLease.CoverageSkinMetres];

    static readonly (float Centre, float Delta)[] Paths =
        [(-0.1875f, 0), (-0.0625f, -0.125f), (0.3f, -0.7f), (-0.4f, 0.25f), (12.5f, -1e-7f), (-0.18749999f, 1)];

    // Span fractions in [0, 1], with the smallest step above zero and the largest below one.
    static readonly float[] Fractions = [0, 5.9604645E-08f, 0.25f, 0.5f, 0.99999994f, 1];

    // The facts the wade-depth certificate rests on, evaluated by the production helpers under one control mode:
    // a threshold never rises above the certificate's bound for any admitted error, a path centre never falls below
    // the certificate's lower bound at any fraction, so a clear certificate skips every contact Find could see.
    static void TheWadeBoundsHold(string mode, Func<WaterFloatingPointEnvironment, string, bool> change)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return;
        Assert.True(environment.SetNearest());
        var thresholds = new bool[Lines.Length * Errors.Length];
        var centres = new bool[Paths.Length * Fractions.Length];
        var skips = new bool[Lines.Length * Paths.Length * Fractions.Length * Errors.Length];
        var clear = new bool[Lines.Length * Paths.Length];
        try
        {
            if (!change(environment, mode)) return;
            for (int l = 0; l < Lines.Length; l++)
            {
                var (surface, half, enter, proximity) = Lines[l];
                double bound = MovementWaterBoundary.WadeThreshold(surface, half, enter, MovementQueryLease.CoverageSkinMetres, proximity);
                for (int e = 0; e < Errors.Length; e++)
                    thresholds[l * Errors.Length + e] =
                        MovementWaterBoundary.WadeThreshold(surface, half, enter, Errors[e], proximity) <= bound;
                for (int p = 0; p < Paths.Length; p++)
                {
                    var (centre, delta) = Paths[p];
                    clear[l * Paths.Length + p] = MovementWaterBoundaryCertificate.WadeClear(surface, half, enter, proximity, centre, delta);
                    for (int f = 0; f < Fractions.Length; f++)
                        for (int e = 0; e < Errors.Length; e++)
                            skips[((l * Paths.Length + p) * Fractions.Length + f) * Errors.Length + e] =
                                !clear[l * Paths.Length + p] || MovementWaterBoundary.PathCentreY(centre, delta, Fractions[f]) >
                                MovementWaterBoundary.WadeThreshold(surface, half, enter, Errors[e], proximity);
                }
            }
            for (int p = 0; p < Paths.Length; p++)
                for (int f = 0; f < Fractions.Length; f++)
                    centres[p * Fractions.Length + f] = MovementWaterBoundary.PathCentreY(Paths[p].Centre, Paths[p].Delta, Fractions[f]) >=
                        MovementWaterBoundaryCertificate.LowestCentreY(Paths[p].Centre, Paths[p].Delta);
        }
        finally { environment.Restore(); }
        Assert.All(thresholds, Assert.True);
        Assert.All(centres, Assert.True);
        Assert.All(skips, Assert.True);
        // The matrix holds both answers, so neither assertion above passes by an empty or one-sided case.
        Assert.Contains(true, clear);
        Assert.Contains(false, clear);
    }
}
