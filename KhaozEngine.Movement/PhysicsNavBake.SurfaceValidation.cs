using System;

namespace KhaozEngine.Movement;

public sealed partial class PhysicsNavBake
{
    private static void ValidateCapturedSurface(int x, int z, int index, float height, float previous, float headroom)
    {
        if (!float.IsFinite(height) || height <= previous || float.IsNaN(headroom) || headroom < 0f)
        {
            const string message = "Physics surfaces must have finite ascending heights and nonnegative headroom.";
            if (!float.IsFinite(height))
                throw new NavigationBuildException(NavigationBuildRefusal.SurfaceHeightNonfinite, message)
                    { ColumnX = x, ColumnZ = z, SurfaceIndex = index, ActualHeight = height };
            if (height <= previous)
                throw new NavigationBuildException(NavigationBuildRefusal.SurfaceHeightNotAscending, message)
                    { ColumnX = x, ColumnZ = z, SurfaceIndex = index, ActualHeight = height, PreviousHeight = previous };
            throw new NavigationBuildException(float.IsNaN(headroom)
                ? NavigationBuildRefusal.SurfaceHeadroomNaN : NavigationBuildRefusal.SurfaceHeadroomNegative, message)
                { ColumnX = x, ColumnZ = z, SurfaceIndex = index, ActualHeadroom = headroom };
        }
    }

    // Inject raw surface faults after the probe's ordering and headroom derivation, without weakening the probe.
    internal static void InjectMalformedSurfaceForTests(int x, int z, int index, float height, float previous, float headroom) =>
        ValidateCapturedSurface(x, z, index, height, previous, headroom);
}
