using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;

namespace KhaozEngine.Tests.MapDoc;

internal static class RelationFixtures
{
    internal static MapRelationResult Relate(MapDocument doc, Vector3 a, Vector3 b,
        MapRelationQuery? q = null, IMapSurfaceSource? source = null)
        => Relate(doc, a, b, out _, q, source);

    internal static MapRelationResult Relate(MapDocument doc, Vector3 a, Vector3 b, out MapScopedSurfaces acquired,
        MapRelationQuery? q = null, IMapSurfaceSource? source = null)
    {
        MapSurfaceScope scope = ScopeFixtures.Around(WorldFrame.Origin, 4, 4, half: 8);
        acquired = ScopeFixtures.Acquire(source ?? MapDocumentSurfaceSource.Capture(doc), scope);
        return new MapSpaceRelations(acquired).Relate(new(WorldFrame.Origin, a), new(WorldFrame.Origin, b), q ?? new());
    }

    internal static MapRelationResult RelateIn(IMapSurfaceSource source, MapSurfaceScope scope, Vector3 a, Vector3 b)
    {
        MapScopedSurfaces acquired = ScopeFixtures.Acquire(source, scope);
        return new MapSpaceRelations(acquired).Relate(new(scope.Frame, a), new(scope.Frame, b), new());
    }
}
