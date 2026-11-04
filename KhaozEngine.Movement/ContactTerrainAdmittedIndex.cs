using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Next admitted column in each fixed direction. Heights and layer tie rules remain caller-owned.</summary>
internal sealed class ContactTerrainAdmittedIndex
{
    readonly int[][] _next = new int[8][];
    readonly int _width;
    internal ContactTerrainAdmittedIndex(GroundNavigation navigation)
    {
        var grid = navigation.Space.Layers[0];
        _width = grid.Width;
        var present = new bool[checked(grid.Width * grid.Height)];
        foreach (var layer in navigation.Graph.Layers)
            for (int z = 0; z < grid.Height; z++) for (int x = 0; x < grid.Width; x++)
                present[z * grid.Width + x] |= layer.IsAccepted(x, z);
        for (int direction = 0; direction < 8; direction++)
        {
            int dx = NavTraversalLayer.NeighborX(direction), dz = NavTraversalLayer.NeighborZ(direction);
            var next = _next[direction] = new int[present.Length]; Array.Fill(next, -1);
            for (int zi = 0; zi < grid.Height; zi++) for (int xi = 0; xi < grid.Width; xi++)
            {
                int x = dx > 0 ? grid.Width - 1 - xi : xi, z = dz > 0 ? grid.Height - 1 - zi : zi, nx = x + dx, nz = z + dz;
                if ((uint)nx >= (uint)grid.Width || (uint)nz >= (uint)grid.Height) continue;
                int cell = nz * grid.Width + nx;
                next[z * grid.Width + x] = present[cell] ? cell : next[cell];
            }
        }
    }
    internal int Next(int direction, int x, int z) => _next[direction][z * _width + x];
}
