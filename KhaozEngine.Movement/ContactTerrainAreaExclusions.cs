using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Narrows terrain links over their retained physical shell, including steep columns with no ordinary node.</summary>
internal static class ContactTerrainAreaExclusions
{
    internal static bool Allows(GroundNavigation navigation, NavLink link)
    {
        var terrain = navigation.Terrain;
        var space = navigation.Space;
        var from = ContactDropProof.Feet(space.Layers[link.FromLayer], link.FromX, link.FromZ);
        var to = ContactDropProof.Feet(space.Layers[link.ToLayer], link.ToX, link.ToZ);
        if (terrain?.Route(link with { Kind = NavLinkKind.Drop }) != ContactDropRoute.Terrain)
            return navigation.Footprint.AcceptsExclusions(from, to);
        var footprint = navigation.Footprint;
        if (!footprint.AcceptsExclusions(from, from) || !footprint.AcceptsExclusions(to, to)) return false;
        uint excluded = footprint.ExcludedAreas;
        if (excluded == 0) return true;
        var evidence = terrain.Evidence;
        var options = evidence.Options;
        // A centre may stray one radius from the affine route. Its shell needs a second radius of clearance.
        double radius = 2d * navigation.AgentRadius;
        int Cell(double coordinate, float origin) => (int)Math.Floor((coordinate - origin) / options.CellSize);
        int x0 = Cell(Math.Min(from.X, to.X) - radius, options.MinX), x1 = Cell(Math.Max(from.X, to.X) + radius, options.MinX);
        int z0 = Cell(Math.Min(from.Z, to.Z) - radius, options.MinZ), z1 = Cell(Math.Max(from.Z, to.Z) + radius, options.MinZ);
        for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
            {
                double left = options.MinX + cx * (double)options.CellSize, bottom = options.MinZ + cz * (double)options.CellSize;
                if (!NavFootprintGeometry.Touches(from, to, left, bottom, options.CellSize, radius)) continue;
                foreach (var entry in evidence.Column(cx, cz))
                {
                    var face = evidence.Face(entry.Face);
                    if (face.Analytic && face.Normal.Y > 0 && (entry.Areas & excluded) != 0) return false;
                }
            }
        return true;
    }
}
