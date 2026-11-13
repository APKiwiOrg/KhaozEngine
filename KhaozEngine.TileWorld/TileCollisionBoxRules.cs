using System.Globalization;

namespace KhaozEngine.TileWorld;

// What the catalog loader checks on an archetype's collision boxes, for every archetype Load, LoadJson and Merge add.
// The schema says the JSON shape. This says what the schema cannot: which collision kinds may carry boxes, and that
// every box is a finite volume. JSON has no NaN literal, but a number past the float range reads as an infinity, and
// Merge re-adds archetypes a caller can have edited in memory since their own load.
internal static class TileCollisionBoxRules
{
    // A box is the shape of an object collider, and only Solid and Diagonal build one. Wall and WallCorner build edge
    // boxes and None builds nothing, so boxes there would be ignored, and an author who listed them meant something.
    // Refusing keeps that mistake loud. Every bound is explicit, so unlike a walk surface's null extent nothing waits
    // on a tile size the catalog does not know, and an inverted or empty box is always refused here.
    internal static void ThrowIfInvalid(TileObjectArchetype a, string source)
    {
        if (a.CollisionBoxes is not { } boxes) return;
        TileCollisionKind kind = a.CollisionKind;
        if (kind is not (TileCollisionKind.Solid or TileCollisionKind.Diagonal))
            throw new TileWorldException(TileWorldRefusal.CatalogCollisionBoxKindUnsupported,
                $"{source}: archetype '{a.Id}' lists collision boxes, but its collisionKind {kind} builds no object collider. Boxes need Solid or Diagonal.")
            { SourceName = source, ArchetypeId = a.Id, CollisionKind = kind };
        for (int i = 0; i < boxes.Count; i++)
        {
            string at = $"{source}: archetype '{a.Id}' collision box {i}";
            if (boxes[i] is not { } box)
                throw new TileWorldException(TileWorldRefusal.CatalogCollisionBoxNull, $"{at} is null")
                { SourceName = source, ArchetypeId = a.Id, CollisionBoxIndex = i };
            float minX = box.MinX, maxX = box.MaxX, minY = box.MinY, maxY = box.MaxY, minZ = box.MinZ, maxZ = box.MaxZ;
            RequireFinite(source, a.Id, i, at, "minX", minX);
            RequireFinite(source, a.Id, i, at, "maxX", maxX);
            RequireFinite(source, a.Id, i, at, "minY", minY);
            RequireFinite(source, a.Id, i, at, "maxY", maxY);
            RequireFinite(source, a.Id, i, at, "minZ", minZ);
            RequireFinite(source, a.Id, i, at, "maxZ", maxZ);
            RequireFinite(source, a.Id, i, at, "yawDegrees", box.YawDegrees);
            RequireOrdered(source, a.Id, i, at, "minX", minX, "maxX", maxX);
            RequireOrdered(source, a.Id, i, at, "minY", minY, "maxY", maxY);
            RequireOrdered(source, a.Id, i, at, "minZ", minZ, "maxZ", maxZ);
        }
    }

    static void RequireFinite(string source, string archetypeId, int index, string at, string field, float value)
    {
        if (!float.IsFinite(value))
            throw new TileWorldException(TileWorldRefusal.CatalogCollisionBoxNonFinite,
                $"{at}: {field} {value.ToString(CultureInfo.InvariantCulture)} is not a finite number")
            { SourceName = source, ArchetypeId = archetypeId, CollisionBoxIndex = index, Field = field, Value = value };
    }

    static void RequireOrdered(string source, string archetypeId, int index, string at, string minField, float min,
        string maxField, float max)
    {
        if (!(min < max))
            throw new TileWorldException(TileWorldRefusal.CatalogCollisionBoxBoundsUnordered,
                $"{at}: {minField} {min.ToString(CultureInfo.InvariantCulture)} is not below {maxField} {max.ToString(CultureInfo.InvariantCulture)}, so the box is inverted or empty")
            {
                SourceName = source, ArchetypeId = archetypeId, CollisionBoxIndex = index,
                MinField = minField, MaxField = maxField, Minimum = min, Maximum = max,
            };
    }
}
