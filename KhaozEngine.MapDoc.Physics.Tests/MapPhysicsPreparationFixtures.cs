using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Physics;

internal static class MapPhysicsPreparationFixtures
{
    internal static MapSceneScope Window(double minX = 0, double maxX = 64) => new(
        new(default, new((float)minX, -64), new((float)maxX, 64), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(minX, -500, -64, maxX, 500, 64), MapSceneChannels.All);
}
