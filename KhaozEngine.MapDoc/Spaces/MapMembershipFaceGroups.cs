using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Patch face copies shared by all final cell consumers.</summary>
internal sealed class MapMembershipFaceGroups : IDisposable
{
    internal readonly Dictionary<int, MapCompiledFace[]> Local, World;
    readonly MapPreparationReservation? _workspace;
    internal long WorkspaceBytes { get; }
    internal MapMembershipFaceGroups(MapCompiledPatch local, MapCompiledPatch world, int cells)
    {
        long localCount = local.Faces.Count, worldCount = ReferenceEquals(local, world) ? 0 : world.Faces.Count;
        long face = Unsafe.SizeOf<MapCompiledFace>(), key = Unsafe.SizeOf<MapFaceKey>();
        // Both outputs survive until the final cell. Only one grouping grows and sorts at a time.
        // Grouping growth holds at most twice the faces, in addition to its sort buffer and final copy.
        WorkspaceBytes = checked(1024L + 256L * (Math.Min(cells, localCount) + Math.Min(cells, worldCount)) +
            face * (localCount + worldCount) + (3 * face + key + 4) * Math.Max(localCount, worldCount));
        _workspace = MapMembershipBuild.Kernel(WorkspaceBytes);
        try
        {
            Local = Group(local);
            World = ReferenceEquals(local, world) ? Local : Group(world);
        }
        catch { _workspace?.Dispose(); throw; }
    }
    static Dictionary<int, MapCompiledFace[]> Group(MapCompiledPatch patch) => patch.Faces
        .GroupBy(f => f.Key.Primitive).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Key).ToArray());
    public void Dispose() => _workspace?.Dispose();
}
